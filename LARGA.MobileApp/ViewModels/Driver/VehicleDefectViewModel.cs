using System;
using System.IO;
using System.Windows.Input;
using Microsoft.Maui.Storage;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using System.Threading.Tasks;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Services;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Storage;

namespace LARGA.MobileApp.ViewModels.Driver;

[QueryProperty(nameof(ChecklistItem), "item")]
public class VehicleDefectViewModel : BindableObject
{
    private readonly IMaintenanceService _maintenanceService;
    private readonly IShiftManagementService _shiftManagementService;

    private string _checklistItem = string.Empty;
    private string _titleReport = string.Empty;
    private string _description = string.Empty;
    private string _priority = "High";
    private string? _photoPath;
    private ImageSource? _defectPhoto;

    public string ChecklistItem
    {
        get => _checklistItem;
        set { _checklistItem = value; OnPropertyChanged(); }
    }

    public string TitleReport
    {
        get => _titleReport;
        set { _titleReport = value; OnPropertyChanged(); }
    }

    public string Description
    {
        get => _description;
        set { _description = value; OnPropertyChanged(); }
    }

    public ImageSource? DefectPhoto
    {
        get => _defectPhoto;
        set { _defectPhoto = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasPhoto)); }
    }

    public bool HasPhoto => DefectPhoto != null;

    public bool IsLow
    {
        get => _priority == "Low";
        set { if (value) { _priority = "Low"; RefreshPriority(); } }
    }

    public bool IsMedium
    {
        get => _priority == "Medium";
        set { if (value) { _priority = "Medium"; RefreshPriority(); } }
    }

    public bool IsHigh
    {
        get => _priority == "High";
        set { if (value) { _priority = "High"; RefreshPriority(); } }
    }

    private void RefreshPriority()
    {
        OnPropertyChanged(nameof(IsLow));
        OnPropertyChanged(nameof(IsMedium));
        OnPropertyChanged(nameof(IsHigh));
    }

    public ICommand AttachPhotoCommand { get; }
    public ICommand SubmitReportCommand { get; }

    public VehicleDefectViewModel(IMaintenanceService maintenanceService, IShiftManagementService shiftManagementService)
    {
        _maintenanceService = maintenanceService;
        _shiftManagementService = shiftManagementService;

        AttachPhotoCommand = new Command(async () => await AttachPhotoAsync());

        SubmitReportCommand = new Command(async () =>
        {
            if (string.IsNullOrWhiteSpace(TitleReport) || string.IsNullOrWhiteSpace(Description))
            {
                await Shell.Current.DisplayAlert("Incomplete Report", "Please provide both a title and a description before submitting.", "OK");
                return;
            }

            var priorityLevel = IsLow ? PriorityLevel.Low : IsMedium ? PriorityLevel.Medium : PriorityLevel.High;

            var currentUser = CrossFirebaseAuth.Current.CurrentUser;
            var driverId = currentUser?.Uid ?? string.Empty;

            var assignedTaxi = await _shiftManagementService.GetCurrentUserAssignedTaxiAsync();
            var taxiId = assignedTaxi?.TaxiId ?? string.Empty;

            // Only set when reported mid-shift (end-shift inspection) - a pre-shift defect is
            // reported before clock-in, so there's no shift to link it to yet.
            string? shiftId = null;
            try { shiftId = await SecureStorage.GetAsync("ActiveShiftDocumentId"); } catch { }

            string? supportingPhotoUrl = null;
            if (!string.IsNullOrWhiteSpace(_photoPath))
            {
                try
                {
                    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    supportingPhotoUrl = await UploadPhotoAsync(_photoPath, $"maintenance_logs/{driverId}/{timestamp}/defect.jpg");
                }
                catch (Exception uploadEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Defect photo upload error: {uploadEx.Message}");
                    await Shell.Current.DisplayAlert("Upload Failed", "Unable to upload the photo to cloud storage. Please check your internet connection and try again.", "OK");
                    return;
                }
            }

            var record = new MaintenanceRecord
            {
                TaxiId = taxiId,
                ShiftId = string.IsNullOrWhiteSpace(shiftId) ? null : shiftId,
                ManagerId = null, // Not yet assigned; manager sets this when creating a work order
                MaintenanceType = MaintenanceType.BreakdownRepair,
                IssueTitle = TitleReport,
                IssueDescription = Description,
                DateLogged = System.DateTime.UtcNow,
                PriorityLevel = priorityLevel,
                SupportingPhotoUrl = supportingPhotoUrl,
                ReportedByDriverId = driverId,
                Status = "Reported"
            };

            var recordId = await _maintenanceService.SubmitMaintenanceRecordAsync(record);

            if (string.IsNullOrEmpty(recordId))
            {
                await Shell.Current.DisplayAlert("Error", "Failed to submit report. Please try again.", "OK");
                return;
            }

            LARGA.MobileApp.Services.AuditLogWriter.Record("VehicleDefectReported",
                $"Vehicle defect report submitted for {taxiId}: {record.IssueTitle}.");

            await Shell.Current.GoToAsync($"..?defectSubmitted=true&reportId={recordId}");
        });
    }

    private async Task AttachPhotoAsync()
    {
        try
        {
            if (MediaPicker.Default.IsCaptureSupported)
            {
                var photo = await MediaPicker.Default.CapturePhotoAsync();
                if (photo != null)
                {
                    _photoPath = photo.FullPath;
                    var stream = await photo.OpenReadAsync();
                    DefectPhoto = ImageSource.FromStream(() => stream);
                }
            }
        }
        catch (System.Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Camera failed: {ex.Message}", "OK");
        }
    }

    // Same pattern as FuelReportViewModel.UploadImageAsync - stores the actual photo in
    // Firebase Storage and returns a durable download URL, instead of the local device
    // cache path (which stops resolving once the cache is cleared or the report is viewed
    // from a different device - the bug LAR-61 was filed against).
    private static async Task<string> UploadPhotoAsync(string localFilePath, string remotePath)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
        {
            throw new ArgumentException("File path is invalid or does not exist", nameof(localFilePath));
        }

        var storageRef = CrossFirebaseStorage.Current.GetRootReference().GetChild(remotePath);
        await storageRef.PutFile(localFilePath).AwaitAsync();
        return await storageRef.GetDownloadUrlAsync();
    }
}
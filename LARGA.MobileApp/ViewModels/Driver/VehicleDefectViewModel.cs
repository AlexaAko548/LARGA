using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using System.Threading.Tasks;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Services;
using Plugin.Firebase.Auth;

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

            var record = new MaintenanceRecord
            {
                TaxiId = taxiId,
                ManagerId = null, // Not yet assigned; manager sets this when creating a work order
                MaintenanceType = MaintenanceType.BreakdownRepair,
                IssueTitle = TitleReport,
                IssueDescription = Description,
                DateLogged = System.DateTime.UtcNow,
                PriorityLevel = priorityLevel,
                SupportingPhotoUrl = _photoPath, // NOTE: local device path for now; photo upload to Firebase Storage is a follow-up
                ReportedByDriverId = driverId,
                Status = "Reported"
            };

            var recordId = await _maintenanceService.SubmitMaintenanceRecordAsync(record);

            if (string.IsNullOrEmpty(recordId))
            {
                await Shell.Current.DisplayAlert("Error", "Failed to submit report. Please try again.", "OK");
                return;
            }

            await Shell.Current.GoToAsync($"..?defectSubmitted=true");
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
}
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
    // The photo is read into memory as soon as it's taken: the camera saves it in the app's cache,
    // which Android may clear (e.g. when the phone is low on storage) before the report is submitted.
    private byte[]? _photoBytes;
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

        AttachPhotoCommand = new Command(async () => await LARGA.MobileApp.Services.NavigationGate.RunAsync(AttachPhotoAsync));

        // LAR-124: one submit at a time - on a slow connection drivers tap again thinking it didn't go.
        SubmitReportCommand = new Command(async () => await SubmitAsync(), () => !IsSubmitting);
    }

    private bool _isSubmitting;
    public bool IsSubmitting
    {
        get => _isSubmitting;
        private set
        {
            _isSubmitting = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubmitButtonText));
            ((Command)SubmitReportCommand).ChangeCanExecute();
        }
    }

    public string SubmitButtonText => IsSubmitting ? "Submitting…" : "Submit Report";

    // A matching open report this recent is the same report sent twice, not a new defect.
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(30);

    private async Task SubmitAsync()
    {
        if (IsSubmitting)
        {
            return;
        }

        IsSubmitting = true;
        try
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

            // Already went through on an earlier tap (or before the app was reopened): treat it as
            // sent instead of filing - and uploading the photo for - a second copy.
            string? existingId = await _maintenanceService.FindRecentDuplicateReportAsync(driverId, taxiId, TitleReport, DuplicateWindow);
            if (existingId is not null)
            {
                await Shell.Current.GoToAsync($"..?defectSubmitted=true&reportId={existingId}");
                return;
            }

            // Only set when reported mid-shift (end-shift inspection) - a pre-shift defect is
            // reported before clock-in, so there's no shift to link it to yet.
            string? shiftId = null;
            try { shiftId = await SecureStorage.GetAsync("ActiveShiftDocumentId"); } catch { }

            string? supportingPhotoUrl = null;
            if (_photoBytes is { Length: > 0 })
            {
                try
                {
                    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    supportingPhotoUrl = await UploadPhotoAsync(_photoBytes, $"maintenance_logs/{driverId}/{timestamp}/defect.jpg");
                }
                catch (Exception uploadEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Defect photo upload error: {uploadEx}");
                    string message = "Unable to upload the photo to cloud storage. Please check your internet connection and try again.";
#if DEBUG
                    message += $" ({uploadEx.GetType().Name}: {uploadEx.Message})";
#endif
                    await Shell.Current.DisplayAlert("Upload Failed", message, "OK");
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
        }
        finally
        {
            IsSubmitting = false;
        }
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
                    // MNT-004 (review follow-up): a raw camera capture can be several MB - a real
                    // mobile upload connection can need well over the 30s UploadPhotoAsync allows
                    // for that, "Upload Failed" even though the connection is genuinely fine. Still
                    // read into memory immediately, same as before (see the field comment on
                    // _photoBytes) - just compressed first, the same resize-and-recompress every
                    // other photo upload in this app already does.
                    _photoBytes = await CompressPhotoAsync(photo, maxDimension: 1600, quality: 80);
                    byte[] bytes = _photoBytes;
                    DefectPhoto = ImageSource.FromStream(() => new MemoryStream(bytes));
                }
            }
        }
        catch (System.Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Camera failed: {ex.Message}", "OK");
        }
    }

    // Same resize/re-orient/recompress approach as PreShiftStep2ViewModel/EndShiftStep2ViewModel's
    // ProcessAndOrientPhotoAsync, adapted to return bytes instead of writing to a file - this
    // ViewModel keeps the photo in memory rather than a cache file path (see the comment on
    // _photoBytes above).
    private static async Task<byte[]> CompressPhotoAsync(FileResult photo, int maxDimension, int quality)
    {
        return await Task.Run(async () =>
        {
#if ANDROID
            Android.Graphics.Bitmap? sampledBitmap = null;
            Android.Graphics.Bitmap? finalBitmap = null;

            try
            {
                int rotationDegrees = 0;
                try
                {
                    var exif = new Android.Media.ExifInterface(photo.FullPath);
                    int orientation = exif.GetAttributeInt(Android.Media.ExifInterface.TagOrientation, 1);
                    rotationDegrees = orientation switch
                    {
                        6 => 90,
                        3 => 180,
                        8 => 270,
                        _ => 0
                    };
                }
                catch { }

                var options = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
                using (var boundsStream = await photo.OpenReadAsync())
                {
                    Android.Graphics.BitmapFactory.DecodeStream(boundsStream, null, options);
                }

                options.InSampleSize = 1;
                int maxSourceDim = Math.Max(options.OutWidth, options.OutHeight);
                while (maxSourceDim / options.InSampleSize > maxDimension)
                {
                    options.InSampleSize *= 2;
                }
                options.InJustDecodeBounds = false;

                using (var decodeStream = await photo.OpenReadAsync())
                {
                    sampledBitmap = Android.Graphics.BitmapFactory.DecodeStream(decodeStream, null, options);
                }

                if (sampledBitmap == null) throw new Exception("Failed to decode image stream.");

                if (rotationDegrees != 0)
                {
                    using var matrix = new Android.Graphics.Matrix();
                    matrix.PostRotate(rotationDegrees);
                    finalBitmap = Android.Graphics.Bitmap.CreateBitmap(sampledBitmap, 0, 0, sampledBitmap.Width, sampledBitmap.Height, matrix, true);
                }
                else
                {
                    finalBitmap = sampledBitmap;
                }

                using var outStream = new MemoryStream();
                finalBitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg, quality, outStream);
                return outStream.ToArray();
            }
            finally
            {
                if (finalBitmap != null && finalBitmap != sampledBitmap)
                {
                    finalBitmap.Recycle();
                    finalBitmap.Dispose();
                }
                if (sampledBitmap != null)
                {
                    sampledBitmap.Recycle();
                    sampledBitmap.Dispose();
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
#else
            using var stream = await photo.OpenReadAsync();
            using var image = Microsoft.Maui.Graphics.Platform.PlatformImage.FromStream(stream);
            using var downsized = image.Downsize(maxDimension, true);
            using var outStream = new MemoryStream();
            downsized.Save(outStream, Microsoft.Maui.Graphics.ImageFormat.Jpeg, quality / 100f);
            return outStream.ToArray();
#endif
        });
    }

    // Same pattern as FuelReportViewModel.UploadImageAsync - stores the actual photo in
    // Firebase Storage and returns a durable download URL, instead of the local device
    // cache path (which stops resolving once the cache is cleared or the report is viewed
    // from a different device - the bug LAR-61 was filed against).
    //
    // MNT-004: the Firebase Storage SDK has no caller-facing timeout of its own, so on a weak
    // or offline connection PutBytes/GetDownloadUrlAsync could hang far longer than a driver
    // would ever wait - stuck on "Submitting..." with no error and nothing saved. WaitAsync
    // bounds each step so SubmitAsync's catch can show "Upload Failed" instead.
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(30);

    private static async Task<string> UploadPhotoAsync(byte[] photoBytes, string remotePath)
    {
        var storageRef = CrossFirebaseStorage.Current.GetRootReference().GetChild(remotePath);
        await storageRef.PutBytes(photoBytes, new StorageMetadata(contentType: "image/jpeg")).AwaitAsync().WaitAsync(UploadTimeout);
        return await storageRef.GetDownloadUrlAsync().WaitAsync(UploadTimeout);
    }
}
using LARGA.MobileApp.Services;
using LARGA.SharedCore.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace LARGA.MobileApp.ViewModels.Driver;

public class PreShiftStep2ViewModel : BindableObject, IQueryAttributable
{
    private bool _areStep1InspectionsComplete = true;
    private readonly IShiftManagementService _shiftService;
    private readonly IOcrService _ocrService;
    private readonly IPhotoStorageService _photoStorage;
    private readonly IGpsTelemetryService _telemetryService;
    private readonly IEmergencyMonitor _emergencyMonitor;
    private Dictionary<string, bool> _inspection = new();
    private List<string> _defectReportIds = new();
    private string _assignedTaxiId = string.Empty;
    private string? _odometerPhotoLocalPath;
    private string? _fuelPhotoLocalPath;

    private string _assignedUnitPlate = "Loading...";
    public string AssignedUnitPlate
    {
        get => _assignedUnitPlate;
        private set { _assignedUnitPlate = value; OnPropertyChanged(); }
    }

    public bool AreStep1InspectionsComplete
    {
        get => _areStep1InspectionsComplete;
        set { _areStep1InspectionsComplete = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanStartShift)); }
    }

    private string _startingOdometer = string.Empty;
    public string StartingOdometer
    {
        get => _startingOdometer;
        set
        {
            _startingOdometer = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStartShift));
            OnPropertyChanged(nameof(IsOdometerScanned));
            OnPropertyChanged(nameof(OdometerButtonText));
        }
    }

    public bool IsOdometerScanned => !string.IsNullOrWhiteSpace(StartingOdometer);
    public string OdometerButtonText => IsOdometerScanned ? $"📷 {StartingOdometer} km" : "📷 Scan odometer dashboard";

    private ImageSource? _fuelPhoto;
    public ImageSource? FuelPhoto
    {
        get => _fuelPhoto;
        set
        {
            _fuelPhoto = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPhoto));
            OnPropertyChanged(nameof(CanStartShift));
        }
    }

    public bool HasPhoto => FuelPhoto != null;

    private bool _isHalfTankSelected;
    public bool IsHalfTankSelected
    {
        get => _isHalfTankSelected;
        set
        {
            _isHalfTankSelected = value;
            if (value) IsBelowHalfTankSelected = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStartShift));
        }
    }

    private bool _isBelowHalfTankSelected;
    public bool IsBelowHalfTankSelected
    {
        get => _isBelowHalfTankSelected;
        set
        {
            if (_isBelowHalfTankSelected == value) return;
            _isBelowHalfTankSelected = value;
            if (value) IsHalfTankSelected = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PenaltyNoteVisible));
            OnPropertyChanged(nameof(CanStartShift));
        }
    }

    public bool PenaltyNoteVisible => IsBelowHalfTankSelected;

    public bool CanStartShift =>
        !string.IsNullOrWhiteSpace(StartingOdometer) &&
        HasPhoto &&
        (IsHalfTankSelected || IsBelowHalfTankSelected);

    public ICommand ScanOdometerCommand { get; }
    public ICommand AttachPhotoCommand { get; }
    public ICommand ConfirmStartShiftCommand { get; }

    public PreShiftStep2ViewModel(IShiftManagementService shiftService, IOcrService ocrService, IPhotoStorageService photoStorage, IGpsTelemetryService telemetryService, IEmergencyMonitor emergencyMonitor)
    {
        _shiftService = shiftService;
        _ocrService = ocrService;
        _photoStorage = photoStorage;
        _telemetryService = telemetryService;
        _emergencyMonitor = emergencyMonitor;

        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Register<PreShiftStep2ViewModel, OdometerScannedData, string>(this, "PreShiftOdometerScanned", (r, data) =>
        {
            r.StartingOdometer = data.OdometerText;
        });

        _ = LoadAssignedUnitAsync();

        ScanOdometerCommand = new Command(async () => await ScanOdometerAsync());
        AttachPhotoCommand = new Command(async () => await AttachPhotoAsync());
        ConfirmStartShiftCommand = new Command(async () => await ConfirmStartShiftAsync());
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("inspection", out var value) && value is Dictionary<string, bool> inspection)
        {
            _inspection = inspection;
        }
        if (query.TryGetValue("defectReportIds", out var ids) && ids is List<string> reportIds)
        {
            _defectReportIds = reportIds;
        }
    }

    private async Task LoadAssignedUnitAsync()
    {
        try
        {
            var taxi = await _shiftService.GetCurrentUserAssignedTaxiAsync();
            if (taxi != null)
            {
                _assignedTaxiId = taxi.TaxiId;
                AssignedUnitPlate = string.IsNullOrWhiteSpace(taxi.PlateNumber)
                    ? taxi.Model
                    : taxi.PlateNumber.Replace("-", " · ");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Assigned Unit Error: {ex.Message}");
        }
    }

    private async Task AttachPhotoAsync()
    {
        try
        {
            if (MediaPicker.Default.IsCaptureSupported)
            {
                FuelPhoto = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();

                var photo = await MediaPicker.Default.CapturePhotoAsync();
                if (photo != null)
                {
                    string localFilePath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid():N}_{photo.FileName}");

                    await ProcessAndOrientPhotoAsync(photo, localFilePath, maxDimension: 1024, quality: 75);

                    string? oldFilePath = _fuelPhotoLocalPath;
                    _fuelPhotoLocalPath = localFilePath;

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        FuelPhoto = ImageSource.FromFile(localFilePath);
                    });

                    TryDeleteCachedFile(oldFilePath);
                }
            }
        }
        catch (Exception ex)
        {
            await SafeDisplayAlert("Camera Error", $"Failed to process image: {ex.Message}");
        }
    }

    private async Task ScanOdometerAsync()
    {
        try
        {
            if (MediaPicker.Default.IsCaptureSupported)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();

                var photo = await MediaPicker.Default.CapturePhotoAsync();
                if (photo != null)
                {
                    string localFilePath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid():N}_{photo.FileName}");

                    // Odometer digits are a small fraction of a full-dashboard shot's frame,
                    // and thin seven-segment LCD strokes are already a hard case for general
                    // OCR (see OdometerScanPage's NormalizeOdometerCandidate comment) - a
                    // 1280px downscale can shrink them to just a handful of pixels each,
                    // right around where JPEG compression/antialiasing blurs the strokes
                    // together and detection quietly fails. Odometer-specific max dimension
                    // bumped well above the other photo captures (fuel receipt, fuel level)
                    // to give OCR more actual pixels to work with; quality left the same.
                    await ProcessAndOrientPhotoAsync(photo, localFilePath, maxDimension: 2560, quality: 85);

                    string? oldFilePath = _odometerPhotoLocalPath;
                    _odometerPhotoLocalPath = localFilePath;

                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        var navigation = Shell.Current?.Navigation ?? Application.Current?.MainPage?.Navigation;
                        if (navigation != null)
                        {
                            await navigation.PushModalAsync(
                                new LARGA.MobileApp.Views.Driver.OdometerScanPage(_ocrService, localFilePath, "PreShiftOdometerScanned"));
                        }
                    });

                    TryDeleteCachedFile(oldFilePath);
                }
            }
        }
        catch (Exception ex)
        {
            await SafeDisplayAlert("Camera Error", $"Failed to process image: {ex.Message}");
        }
    }

    private async Task ConfirmStartShiftAsync()
    {
        // Clock-in + photo uploads take a few seconds - without this, a second tap in that
        // window starts a second shift (and the first run's cleanup deletes the photos the
        // second run is still trying to upload).
        if (_isSubmitting)
        {
            return;
        }

        _isSubmitting = true;
        try
        {
            await StartShiftCoreAsync();
        }
        finally
        {
            _isSubmitting = false;
        }
    }

    private bool _isSubmitting;

    private static readonly Dictionary<string, string> InspectionLabels = new()
    {
        { "Tires", "Tire condition" },
        { "Hood", "Under the hood" },
        { "Lights", "Lights & electronics" },
        { "Interior", "Interior & comfort" },
        { "Exterior", "Exterior scratches / dents" },
    };

    private List<string> FlagReasons()
    {
        var reasons = _inspection.Where(kv => !kv.Value)
            .Select(kv => $"{(InspectionLabels.TryGetValue(kv.Key, out var label) ? label : kv.Key)} failed")
            .ToList();
        if (IsBelowHalfTankSelected)
        {
            reasons.Add("Fuel below half-tank");
        }
        if (_defectReportIds.Count > 0)
        {
            reasons.Add($"{_defectReportIds.Count} defect report(s) filed");
        }
        return reasons;
    }

    /// <summary>Uploads the inspection photos and sends the clock-in to the manager (see
    /// ClockInPendingViewModel for the wait). Nothing is clocked in yet.</summary>
    private async Task SubmitForApprovalAsync(int startMileage, List<string> flagReasons)
    {
        try
        {
            string driverId = Plugin.Firebase.Auth.CrossFirebaseAuth.Current.CurrentUser?.Uid ?? "unknown_driver";
            string prefix = $"handover_checklists/{driverId}/request_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}/pre";
            string? fuelUrl = await UploadIfPresentAsync(_fuelPhotoLocalPath, $"{prefix}_fuel.jpg");
            string? odometerUrl = await UploadIfPresentAsync(_odometerPhotoLocalPath, $"{prefix}_odometer.jpg");

            string requestId = await _shiftService.SubmitClockInRequestAsync(new ClockInRequestSubmission
            {
                TaxiId = _assignedTaxiId,
                StartMileage = startMileage,
                Inspection = _inspection,
                IsBelowHalfTank = IsBelowHalfTankSelected,
                FuelDashboardUrl = fuelUrl,
                OdometerPhotoUrl = odometerUrl,
                DefectReportIds = _defectReportIds,
                FlagReasons = string.Join("; ", flagReasons),
            });

            // The wait survives leaving the app: the dashboard sends the driver back here.
            Preferences.Set(ClockInPendingViewModel.PendingRequestKey, requestId);

            StartingOdometer = string.Empty;
            FuelPhoto = null;
            IsHalfTankSelected = false;
            IsBelowHalfTankSelected = false;
            TryDeleteCachedFile(_odometerPhotoLocalPath);
            TryDeleteCachedFile(_fuelPhotoLocalPath);
            _odometerPhotoLocalPath = null;
            _fuelPhotoLocalPath = null;

            await Shell.Current.GoToAsync("clockin-pending");
        }
        catch (InvalidOperationException rule)
        {
            // No valid license, or the unit is under maintenance - written for the driver.
            await SafeDisplayAlert("Can't start shift", rule.Message);
        }
        catch (Exception ex)
        {
            await SafeDisplayAlert("Error", $"Couldn't send your inspection to the manager: {ex.Message}");
        }
    }

    private async Task<string?> UploadIfPresentAsync(string? localPath, string storagePath)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            return null;
        }

        byte[] bytes = await File.ReadAllBytesAsync(localPath);
        return await _photoStorage.UploadPhotoAsync(storagePath, bytes);
    }

    private async Task StartShiftCoreAsync()
    {
        if (!CanStartShift)
        {
            await SafeDisplayAlert("Required", "Please complete all fields (Odometer, Fuel Level, Fuel Photo).");
            return;
        }

        if (string.IsNullOrWhiteSpace(_assignedTaxiId))
        {
            await SafeDisplayAlert("Missing assignment", "No assigned taxi was found for your account. Please contact the manager.");
            return;
        }

        try
        {
            string digitsOnly = new string(StartingOdometer.Where(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(digitsOnly) || !int.TryParse(digitsOnly, out int startMileage) || startMileage <= 0)
            {
                await SafeDisplayAlert("Invalid odometer reading", "Please rescan or enter a valid starting odometer reading before starting the shift.");
                return;
            }

            // Paper Ch. IV: a flagged inspection (failed item / reported defect, or fuel below the
            // required half-tank) isn't clocked in automatically - it's sent to the manager.
            List<string> flagReasons = FlagReasons();
            if (flagReasons.Count > 0)
            {
                await SubmitForApprovalAsync(startMileage, flagReasons);
                return;
            }

            string newDocumentId;
            try
            {
                newDocumentId = await _shiftService.ClockInAsync(_assignedTaxiId, startMileage);
            }
            catch (InvalidOperationException rule)
            {
                // Operating-day rule (before 6:00 AM, or a shift already open) - the message
                // is written for the driver.
                await SafeDisplayAlert("Can't start shift", rule.Message);
                return;
            }

            if (string.IsNullOrWhiteSpace(newDocumentId))
            {
                await SafeDisplayAlert("Error", "Failed to start shift. Please try again.");
                return;
            }

            await SecureStorage.SetAsync("ActiveShiftDocumentId", newDocumentId);
            // Fuel reports read the shift they belong to from here.
            Preferences.Set("CurrentShiftId", newDocumentId);
            Preferences.Set("IsShiftActive", true);
            Preferences.Set("ShiftStartTime", LARGA.SharedCore.ShiftClock.LocalNow.ToString("o"));

            // The shift has started regardless of how this goes - a failed checklist/photo
            // upload only means the manager won't see this inspection, so it never blocks.
            await ShiftChecklistUploader.SubmitAsync(
                _shiftService, _photoStorage, newDocumentId, isEndShift: false, _inspection,
                IsBelowHalfTankSelected, _fuelPhotoLocalPath, _odometerPhotoLocalPath);

            // LAR-77 Contextual Auto-Cutoff Protocol: telemetry starts strictly on a
            // successful clock-in, never before.
            _telemetryService.Start(newDocumentId);

            // LAR-86/87: automated emergency detection runs for the length of the shift.
            _ = _emergencyMonitor.StartAsync(newDocumentId);

            StartingOdometer = string.Empty;
            FuelPhoto = null;
            IsHalfTankSelected = false;
            IsBelowHalfTankSelected = false;

            TryDeleteCachedFile(_odometerPhotoLocalPath);
            TryDeleteCachedFile(_fuelPhotoLocalPath);
            _odometerPhotoLocalPath = null;
            _fuelPhotoLocalPath = null;

            if (Shell.Current != null)
            {
                await Shell.Current.GoToAsync("active-shift");
            }
        }
        catch (Exception ex)
        {
            await SafeDisplayAlert("Error", $"Failed to start shift: {ex.Message}");
        }
    }

    private static async Task<string> ProcessAndOrientPhotoAsync(FileResult photo, string targetPath, int maxDimension = 1280, int quality = 80)
    {
        return await Task.Run(async () =>
        {
#if ANDROID
            Android.Graphics.Bitmap? sampledBitmap = null;
            Android.Graphics.Bitmap? finalBitmap = null;

            try
            {
                // 1. Read EXIF Orientation
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

                // 2. Decode bounds only (no heavy memory allocation)
                var options = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
                using (var boundsStream = await photo.OpenReadAsync())
                {
                    Android.Graphics.BitmapFactory.DecodeStream(boundsStream, null, options);
                }

                // 3. Calculate safe downsampling ratio
                options.InSampleSize = 1;
                int maxSourceDim = Math.Max(options.OutWidth, options.OutHeight);
                while (maxSourceDim / options.InSampleSize > maxDimension)
                {
                    options.InSampleSize *= 2;
                }
                options.InJustDecodeBounds = false;

                // 4. Decode the smaller, memory-safe bitmap
                using (var decodeStream = await photo.OpenReadAsync())
                {
                    sampledBitmap = Android.Graphics.BitmapFactory.DecodeStream(decodeStream, null, options);
                }

                if (sampledBitmap == null) throw new Exception("Failed to decode image stream.");

                // 5. Apply matrix rotation only if necessary
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

                // 6. Compress and save
                using (var fileStream = File.Create(targetPath))
                {
                    finalBitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg, quality, fileStream);
                }

                return targetPath;
            }
            finally
            {
                // CRITICAL: Aggressively free native unmanaged Android memory instantly to prevent the OS from killing the app
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

                // Secondary garbage collection to ensure the unmanaged buffers are flushed
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
#else
            using var stream = await photo.OpenReadAsync();
            using var image = Microsoft.Maui.Graphics.Platform.PlatformImage.FromStream(stream);
            using var downsized = image.Downsize(maxDimension, true);
            using var outStream = File.Create(targetPath);
            downsized.Save(outStream, Microsoft.Maui.Graphics.ImageFormat.Jpeg, quality / 100f);
            return targetPath;
#endif
        });
    }

    private static void TryDeleteCachedFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to delete cached file '{filePath}': {ex.Message}");
        }
    }

    private async Task SafeDisplayAlert(string title, string message, string cancel = "OK")
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Application.Current?.MainPage;
            if (page != null)
            {
                await page.DisplayAlert(title, message, cancel);
            }
        });
    }
}
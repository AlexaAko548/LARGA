using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using LARGA.MobileApp.Services;
using LARGA.SharedCore;
using LARGA.SharedCore.Services;

namespace LARGA.MobileApp.ViewModels.Driver;

public class EndShiftStep2ViewModel : BindableObject, IQueryAttributable
{
    private readonly IOcrService _ocrService;
    private readonly IShiftManagementService _shiftService;
    private readonly IPhotoStorageService _photoStorage;
    private readonly IGpsTelemetryService _telemetryService;
    private readonly IEmergencyMonitor _emergencyMonitor;
    private Dictionary<string, bool> _inspection = new();
    private bool _isSubmitting;

    // Drives the Confirm & End Shift button: disabled and "Ending shift…" while the clock-out runs.
    private bool IsSubmitting
    {
        get => _isSubmitting;
        set
        {
            _isSubmitting = value;
            OnPropertyChanged(nameof(IsNotSubmitting));
            OnPropertyChanged(nameof(EndButtonText));
        }
    }

    public bool IsNotSubmitting => !_isSubmitting;

    public string EndButtonText => _isSubmitting ? "Ending shift…" : "🛡️ Confirm & End Shift";
    private string? _odometerPhotoLocalPath;
    private string? _fuelPhotoLocalPath;

    // Review follow-up: this page hard-coded another unit's plate instead of showing the one
    // actually on this shift. Same AssignedUnitPlate pattern PreShiftStep2ViewModel uses.
    private string _assignedUnitPlate = "Loading...";
    public string AssignedUnitPlate
    {
        get => _assignedUnitPlate;
        private set { _assignedUnitPlate = value; OnPropertyChanged(); }
    }

    private string _finalOdometer = string.Empty;
    public string FinalOdometer
    {
        get => _finalOdometer;
        set
        {
            if (_finalOdometer == value) return;
            _finalOdometer = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOdometerScanned));
            OnPropertyChanged(nameof(OdometerButtonText));
            OnPropertyChanged(nameof(IsComplete));
        }
    }

    public bool IsOdometerScanned => !string.IsNullOrWhiteSpace(FinalOdometer);
    public string OdometerButtonText => IsOdometerScanned ? $"📷 {FinalOdometer} km" : "📷 Scan odometer dashboard";

    private ImageSource? _fuelPhoto;
    public ImageSource? FuelPhoto
    {
        get => _fuelPhoto;
        set
        {
            _fuelPhoto = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPhoto));
            OnPropertyChanged(nameof(IsComplete));
        }
    }

    public bool HasPhoto => FuelPhoto != null;

    private bool _isHalfTankSelected;
    public bool IsHalfTankSelected
    {
        get => _isHalfTankSelected;
        set
        {
            if (_isHalfTankSelected == value) return;
            _isHalfTankSelected = value;
            if (value) IsBelowHalfTankSelected = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsComplete));
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
            OnPropertyChanged(nameof(IsComplete));
        }
    }

    public bool PenaltyNoteVisible => IsBelowHalfTankSelected;
    public bool IsComplete => IsOdometerScanned && HasPhoto && (IsHalfTankSelected || IsBelowHalfTankSelected);

    public ICommand ScanOdometerCommand { get; }
    public ICommand AttachFuelPhotoCommand { get; }
    public ICommand ConfirmEndShiftCommand { get; }
    public ICommand SelectFuelCommand { get; }

    public EndShiftStep2ViewModel(IOcrService ocrService, IShiftManagementService shiftService, IPhotoStorageService photoStorage, IGpsTelemetryService telemetryService, IEmergencyMonitor emergencyMonitor)
    {
        _ocrService = ocrService;
        _shiftService = shiftService;
        _photoStorage = photoStorage;
        _telemetryService = telemetryService;
        _emergencyMonitor = emergencyMonitor;
        _ = LoadAssignedUnitAsync();

        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Register<EndShiftStep2ViewModel, OdometerScannedData, string>(this, "EndShiftOdometerScanned", (r, data) =>
        {
            r.FinalOdometer = data.OdometerText;
        });

        ScanOdometerCommand = new Command(async () => await ScanOdometerAsync());
        AttachFuelPhotoCommand = new Command(async () => await AttachFuelPhotoAsync());

        ConfirmEndShiftCommand = new Command(async () =>
        {
            if (!IsComplete)
            {
                await Shell.Current.DisplayAlert("Required", "Please complete all fields (Odometer, Fuel Level, Fuel Photo).", "OK");
                return;
            }

            // Same double-tap guard as PreShiftStep2ViewModel.ConfirmStartShiftAsync.
            if (IsSubmitting)
            {
                return;
            }

            IsSubmitting = true;
            using IDisposable loading = LoadingState.Begin();
            try
            {
                string digitsOnly = new string(FinalOdometer.Where(char.IsDigit).ToArray());
                if (string.IsNullOrWhiteSpace(digitsOnly) || !int.TryParse(digitsOnly, out int endMileage) || endMileage <= 0)
                {
                    await Shell.Current.DisplayAlert("Invalid odometer reading", "Please rescan or enter a valid ending odometer reading before ending the shift.", "OK");
                    return;
                }

                // The unit has to actually be back: ending the shift is what stops the
                // late-return clock (ShiftRules.LateReturnFee), so it only counts at the garage.
                if (!await EnsureAtGarageAsync())
                {
                    return;
                }

                var charges = new ShiftEndCharges(0m, 0m);
                string activeShiftId = await SecureStorage.GetAsync("ActiveShiftDocumentId");

                // GPS-006: this is empty when the shift was already closed elsewhere (6:00 AM
                // auto-close, a manager) while the driver was mid-checklist - App.xaml.cs's
                // ShiftClosedRemotely handler already cleared it. Falling through used to skip
                // ClockOutAsync entirely (nothing left to call it on) yet still land on "Shift
                // Completed" below with zeroed charges, silently discarding the odometer, fuel
                // verification, and photos the driver just filled in. ActiveShiftViewModel now
                // leaves for the dashboard the moment that happens, so reaching this screen at all
                // with no shift to end should be rare - but fail loudly instead of faking success
                // for whatever narrow timing window still lets it through.
                if (string.IsNullOrEmpty(activeShiftId))
                {
                    await Shell.Current.DisplayAlert("Shift not ended",
                        "This shift was already ended elsewhere, so nothing was submitted. Please check with your manager if you need your final odometer or fuel level recorded.", "OK");
                    await Shell.Current.GoToAsync("//driver-dashboard");
                    return;
                }

                // DSM-041: the final reading can't be below the one the shift started at.
                // No start reading on the shift: nothing to compare, so it's allowed.
                int? startMileage = await _shiftService.GetShiftStartMileageAsync(activeShiftId);
                if (startMileage is null)
                {
#if ANDROID
                    Android.Util.Log.Warn("LARGA_SHIFT", $"Shift {activeShiftId} has no startMileage - final odometer not checked.");
#else
                    System.Diagnostics.Debug.WriteLine($"Shift {activeShiftId} has no startMileage - final odometer not checked.");
#endif
                }
                else if (endMileage < startMileage.Value)
                {
                    await Shell.Current.DisplayAlert("Odometer below start",
                        $"The final odometer ({endMileage:N0} km) is lower than the starting odometer ({startMileage.Value:N0} km). Please rescan the odometer.", "OK");
                    return;
                }

                charges = await _shiftService.ClockOutAsync(activeShiftId, endMileage, IsBelowHalfTankSelected, "");

                // The shift is closed from here on. Release everything tied to it before the
                // photo uploads below, so a slow or stuck upload can't leave GPS tracking and
                // emergency detection running on a shift that has already ended.
                // LAR-77 Contextual Auto-Cutoff Protocol: telemetry severs strictly on a
                // successful clock-out.
                _telemetryService.Stop();

                // LAR-86/87: automated emergency detection stops with the shift.
                _emergencyMonitor.Stop();

                SecureStorage.Remove("ActiveShiftDocumentId");
                Preferences.Remove("IsShiftActive");
                Preferences.Remove("CurrentShiftId");

                await ShiftChecklistUploader.SubmitAsync(
                    _shiftService, _photoStorage, activeShiftId, isEndShift: true, _inspection,
                    IsBelowHalfTankSelected, _fuelPhotoLocalPath, _odometerPhotoLocalPath);

                Preferences.Remove("IsShiftActive");
                Preferences.Remove("CurrentShiftId");

                FinalOdometer = string.Empty;
                FuelPhoto = null;
                IsHalfTankSelected = false;
                IsBelowHalfTankSelected = false;

                TryDeleteCachedFile(_odometerPhotoLocalPath);
                TryDeleteCachedFile(_fuelPhotoLocalPath);
                _odometerPhotoLocalPath = null;
                _fuelPhotoLocalPath = null;

                await Shell.Current.GoToAsync("shift-completed", new Dictionary<string, object>
                {
                    { "lateFee", charges.LateFee },
                    { "fuelPenalty", charges.FuelPenalty },
                });
            }
            catch (Exception ex)
            {
                FirestoreErrors.Log("End shift", ex);
                await Shell.Current.DisplayAlert("Shift not ended", FirestoreErrors.FriendlyMessage(ex, "end your shift"), "OK");
            }
            finally
            {
                IsSubmitting = false;
            }
        });

        SelectFuelCommand = new Command<string>((option) =>
        {
            if (option == "HalfTank") IsHalfTankSelected = true;
            else if (option == "BelowHalf") IsBelowHalfTankSelected = true;
        });
    }

    /// <summary>
    /// Blocks ending the shift unless the phone is within the garage geofence. No location
    /// (permission denied, GPS off, no fix) or a mock/fake location also blocks - otherwise
    /// turning GPS off would skip the check. Shows the reason; returns true when allowed.
    /// </summary>
    private async Task<bool> EnsureAtGarageAsync()
    {
        const string title = "Return to the garage";

        var permission = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        if (permission != PermissionStatus.Granted)
        {
            permission = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        }

        if (permission != PermissionStatus.Granted)
        {
            await Shell.Current.DisplayAlert(title,
                "LARGA needs your location to confirm the unit is back at the garage. Allow location access for LARGA in your phone's settings, then try again.", "OK");
            return false;
        }

        Location? location;
        try
        {
            location = await Geolocation.Default.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.High, TimeSpan.FromSeconds(15)));
        }
        catch (FeatureNotEnabledException)
        {
            await Shell.Current.DisplayAlert(title,
                "Location is turned off. Turn on your phone's location (GPS), then try again.", "OK");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Garage location check failed: {ex.Message}");
            location = null;
        }

        if (location == null)
        {
            await Shell.Current.DisplayAlert(title,
                "Couldn't get your location. Make sure GPS is on and you have a clear signal, then try again.", "OK");
            return false;
        }

        if (location.IsFromMockProvider)
        {
            await Shell.Current.DisplayAlert(title,
                "A fake/mock location app is active. Turn it off to end your shift.", "OK");
            return false;
        }

        GarageGeofence garage = await _shiftService.GetGarageGeofenceAsync();
        double distance = ShiftRules.DistanceMeters(location.Latitude, location.Longitude, garage.Latitude, garage.Longitude);
        if (distance > garage.RadiusMeters)
        {
            string away = distance >= 1000 ? $"{distance / 1000:0.0} km" : $"{distance:0} m";
            await Shell.Current.DisplayAlert(title,
                $"You're {away} from the garage. Bring the unit back to the garage to end your shift.", "OK");
            return false;
        }

        return true;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("inspection", out var value) && value is Dictionary<string, bool> inspection)
        {
            _inspection = inspection;
        }
    }

    private async Task AttachFuelPhotoAsync()
    {
        try
        {
            if (MediaPicker.Default.IsCaptureSupported)
            {
                var photo = await MediaPicker.Default.CapturePhotoAsync();
                if (photo != null)
                {
                    string localFilePath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid():N}_{photo.FileName}");

                    await PhotoResizer.ResizeToJpegAsync(photo.FullPath, localFilePath, maxDimension: 1024, quality: 75);

                    string? oldFilePath = _fuelPhotoLocalPath;
                    _fuelPhotoLocalPath = localFilePath;

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        FuelPhoto = null;
                        FuelPhoto = ImageSource.FromFile(localFilePath);
                    });

                    TryDeleteCachedFile(oldFilePath);
                }
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Camera failed: {ex.Message}", "OK");
        }
    }

    private async Task ScanOdometerAsync()
    {
        try
        {
            if (MediaPicker.Default.IsCaptureSupported)
            {
                var photo = await MediaPicker.Default.CapturePhotoAsync();
                if (photo != null)
                {
                    string localFilePath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid():N}_{photo.FileName}");

                    // Same reasoning as PreShiftStep2ViewModel.ScanOdometerAsync - odometer
                    // digits need more effective pixels than the 1280px default gives them.
                    await PhotoResizer.ResizeToJpegAsync(photo.FullPath, localFilePath, maxDimension: 2560, quality: 85);

                    string? oldFilePath = _odometerPhotoLocalPath;
                    _odometerPhotoLocalPath = localFilePath;

                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        var navigation = Shell.Current?.Navigation ?? Application.Current?.MainPage?.Navigation;
                        if (navigation != null)
                        {
                            await navigation.PushModalAsync(
                                new LARGA.MobileApp.Views.Driver.OdometerScanPage(_ocrService, localFilePath, "EndShiftOdometerScanned"));
                        }
                    });

                    TryDeleteCachedFile(oldFilePath);
                }
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Camera failed: {ex.Message}", "OK");
        }
    }

    private async Task LoadAssignedUnitAsync()
    {
        using var _loading = LARGA.MobileApp.Services.LoadingState.Begin(); // LAR-117: app-wide loading indicator
        try
        {
            var taxi = await _shiftService.GetCurrentUserAssignedTaxiAsync();
            if (taxi != null)
            {
                AssignedUnitPlate = string.IsNullOrWhiteSpace(taxi.PlateNumber)
                    ? taxi.Model
                    : taxi.PlateNumber.Replace("-", " · ");
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Assigned Unit Error: {ex.Message}");
        }
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
}
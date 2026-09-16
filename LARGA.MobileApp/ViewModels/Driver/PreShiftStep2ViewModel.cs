using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using Plugin.Firebase.Auth;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.SharedCore.Services;

namespace LARGA.MobileApp.ViewModels.Driver;

public class PreShiftStep2ViewModel : BindableObject, IQueryAttributable
{
    private bool _areStep1InspectionsComplete = true;
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
            OnPropertyChanged(nameof(OdometerButtonText)); // Triggers the dynamic text update
        }
    }

    public bool IsOdometerScanned => !string.IsNullOrWhiteSpace(StartingOdometer);

    // Dynamically formats the button text to match the design exactly
    public string OdometerButtonText => IsOdometerScanned ? $"📷 {StartingOdometer} km" : "📷 Scan odometer dashboard";

    private ImageSource _fuelPhoto;
    public ImageSource FuelPhoto
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

    /// <summary>Firebase Cloud Storage download URL for the uploaded fuel-level photo, set once
    /// AttachPhotoAsync's upload completes. Null while nothing's been captured yet, or if the
    /// upload failed - HasPhoto/CanStartShift only depend on the local FuelPhoto preview, so a
    /// failed upload doesn't block the driver from starting their shift.</summary>
    private string? _fuelPhotoUrl;
    public string? FuelPhotoUrl
    {
        get => _fuelPhotoUrl;
        set { _fuelPhotoUrl = value; OnPropertyChanged(); }
    }

    /// <summary>Firebase Cloud Storage download URL for the odometer dashboard photo captured
    /// during the scan (the one frame OCR actually read the number from), relayed back from
    /// OdometerScanPage alongside the recognized number itself.</summary>
    private string? _odometerPhotoUrl;
    public string? OdometerPhotoUrl
    {
        get => _odometerPhotoUrl;
        set { _odometerPhotoUrl = value; OnPropertyChanged(); }
    }

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

    private readonly IPhotoStorageService _photoStorageService;

    public PreShiftStep2ViewModel(IPhotoStorageService photoStorageService)
    {
        _photoStorageService = photoStorageService;

        ScanOdometerCommand = new Command(async () => await ScanOdometerAsync());
        AttachPhotoCommand = new Command(async () => await AttachPhotoAsync());
        ConfirmStartShiftCommand = new Command(async () => await ConfirmStartShiftAsync());
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
                    // Read the full-resolution capture into memory once, then hand back a
                    // brand-new MemoryStream on every call. MAUI's Image control can invoke
                    // the ImageSource.FromStream factory more than once per photo (layout
                    // passes, DPI recalculation, re-render on rebind) - closing over a single
                    // already-opened Stream meant every read after the first hit an
                    // exhausted/consumed stream and decoded a corrupt, blurry-looking bitmap.
                    // This is why the first capture always looked fine but a retake didn't.
                    byte[] photoBytes;
                    using (var stream = await photo.OpenReadAsync())
                    using (var buffer = new MemoryStream())
                    {
                        await stream.CopyToAsync(buffer);
                        photoBytes = buffer.ToArray();
                    }

                    FuelPhoto = ImageSource.FromStream(() => new MemoryStream(photoBytes));

                    string driverId = CrossFirebaseAuth.Current.CurrentUser?.Uid ?? "unknown_driver";
                    string path = $"fuel_photos/{driverId}/preshift_{DateTime.UtcNow:yyyyMMddHHmmss}.jpg";
                    FuelPhotoUrl = await _photoStorageService.UploadPhotoAsync(path, photoBytes);
                }
            }
        }
        catch (System.Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Camera failed: {ex.Message}", "OK");
        }
    }

    private async Task ScanOdometerAsync()
    {
        await Shell.Current.GoToAsync("odometer-scan");
    }

    private async Task ConfirmStartShiftAsync()
    {
        if (!CanStartShift)
        {
            await Shell.Current.DisplayAlert("Required", "Please complete all fields (Odometer, Fuel Level, Fuel Photo).", "OK");
            return;
        }

        Microsoft.Maui.Storage.Preferences.Set("IsShiftActive", true);
        await Shell.Current.GoToAsync("../../active-shift");
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("ScannedOdometer", out var odometer))
        {
            StartingOdometer = odometer.ToString();
        }
        if (query.TryGetValue("OdometerPhotoUrl", out var photoUrl))
        {
            OdometerPhotoUrl = photoUrl.ToString();
        }
    }
}
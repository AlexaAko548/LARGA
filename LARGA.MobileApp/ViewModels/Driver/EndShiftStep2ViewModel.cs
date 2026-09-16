using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using Plugin.Firebase.Auth;
using LARGA.SharedCore.Services;

namespace LARGA.MobileApp.ViewModels.Driver;

public class EndShiftStep2ViewModel : BindableObject, IQueryAttributable
{
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

    // Dynamically formats the button text to match the Pre-Shift design
    public string OdometerButtonText => IsOdometerScanned ? $"📷 {FinalOdometer} km" : "📷 Scan odometer dashboard";

    private ImageSource _fuelPhoto;
    public ImageSource FuelPhoto
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

    /// <summary>Firebase Cloud Storage download URL for the uploaded fuel-level photo - see
    /// PreShiftStep2ViewModel.FuelPhotoUrl for the same rationale (null on failure, non-blocking).</summary>
    private string? _fuelPhotoUrl;
    public string? FuelPhotoUrl
    {
        get => _fuelPhotoUrl;
        set { _fuelPhotoUrl = value; OnPropertyChanged(); }
    }

    /// <summary>Firebase Cloud Storage download URL for the odometer dashboard photo, relayed
    /// back from OdometerScanPage alongside the recognized number.</summary>
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

    private readonly IPhotoStorageService _photoStorageService;

    public EndShiftStep2ViewModel(IPhotoStorageService photoStorageService)
    {
        _photoStorageService = photoStorageService;

        // Routes to the active OCR scanner page
        ScanOdometerCommand = new Command(async () => await Shell.Current.GoToAsync("odometer-scan"));

        AttachFuelPhotoCommand = new Command(async () => await AttachFuelPhotoAsync());

        ConfirmEndShiftCommand = new Command(async () =>
        {
            if (!IsComplete)
            {
                await Shell.Current.DisplayAlert("Required", "Please complete all fields (Odometer, Fuel Level, Fuel Photo).", "OK");
                return;
            }

            Preferences.Remove("IsShiftActive");
            await Shell.Current.GoToAsync("shift-completed");
        });

        SelectFuelCommand = new Command<string>((option) =>
        {
            if (option == "HalfTank") IsHalfTankSelected = true;
            else if (option == "BelowHalf") IsBelowHalfTankSelected = true;
        });
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
                    string path = $"fuel_photos/{driverId}/endshift_{DateTime.UtcNow:yyyyMMddHHmmss}.jpg";
                    FuelPhotoUrl = await _photoStorageService.UploadPhotoAsync(path, photoBytes);
                }
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Camera failed: {ex.Message}", "OK");
        }
    }

    // Catches the selected string (and photo URL) returned from OdometerScanPage
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("ScannedOdometer", out var odometer))
        {
            FinalOdometer = odometer.ToString();
        }
        if (query.TryGetValue("OdometerPhotoUrl", out var photoUrl))
        {
            OdometerPhotoUrl = photoUrl.ToString();
        }
    }
}
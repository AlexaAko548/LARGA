using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;

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

    public EndShiftStep2ViewModel()
    {
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
                    var stream = await photo.OpenReadAsync();
                    FuelPhoto = ImageSource.FromStream(() => stream);
                }
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Camera failed: {ex.Message}", "OK");
        }
    }

    // Catches the selected string returned from OdometerScanPage
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("ScannedOdometer", out var odometer))
        {
            FinalOdometer = odometer.ToString();
        }
    }
}
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;

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
        }
    }

    public bool IsOdometerScanned => !string.IsNullOrWhiteSpace(StartingOdometer);

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
            _isBelowHalfTankSelected = value;
            if (value) IsHalfTankSelected = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStartShift));
        }
    }

    public bool CanStartShift =>
        !string.IsNullOrWhiteSpace(StartingOdometer) &&
        HasPhoto &&
        (IsHalfTankSelected || IsBelowHalfTankSelected);

    public ICommand ScanOdometerCommand { get; }
    public ICommand AttachPhotoCommand { get; }
    public ICommand ConfirmStartShiftCommand { get; }

    public PreShiftStep2ViewModel()
    {
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
                    var stream = await photo.OpenReadAsync();
                    FuelPhoto = ImageSource.FromStream(() => stream);
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

        // 1. Set the shift state to active in the device's local storage
        Microsoft.Maui.Storage.Preferences.Set("IsShiftActive", true);

        // 2. Cleanly route to the Active Shift screen.
        // The "../../" pops Step 2 and Step 1 off the stack so the Home Tab functions perfectly.
        await Shell.Current.GoToAsync("../../active-shift");
    }

    // Catches the scanned value returned from the OdometerScanPage
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("ScannedOdometer", out var odometer))
        {
            StartingOdometer = odometer.ToString();
        }
    }
}
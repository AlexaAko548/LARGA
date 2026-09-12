using LARGA.MobileApp.Services;
using LARGA.SharedCore.Services;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;

namespace LARGA.MobileApp.ViewModels.Driver;

// Removed IQueryAttributable since we are now using WeakReferenceMessenger for Modals
public class PreShiftStep2ViewModel : BindableObject
{
    private bool _areStep1InspectionsComplete = true;

    // Inject both required services
    private readonly IShiftManagementService _shiftService;
    private readonly IOcrService _ocrService;

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

    public PreShiftStep2ViewModel(IShiftManagementService shiftService, IOcrService ocrService)
    {
        _shiftService = shiftService;
        _ocrService = ocrService; // Store the service to pass to the modal

        // Register the Messenger to listen for the modal's return value
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Register<PreShiftStep2ViewModel, string, string>(this, "OdometerScanned", (r, scannedText) =>
        {
            StartingOdometer = scannedText;
        });

        _ = LoadAssignedUnitAsync();

        ScanOdometerCommand = new Command(async () => await ScanOdometerAsync());
        AttachPhotoCommand = new Command(async () => await AttachPhotoAsync());
        ConfirmStartShiftCommand = new Command(async () => await ConfirmStartShiftAsync());
    }

    private async Task LoadAssignedUnitAsync()
    {
        try
        {
            var taxi = await _shiftService.GetCurrentUserAssignedTaxiAsync();
            if (taxi != null)
            {
                AssignedUnitPlate = string.IsNullOrWhiteSpace(taxi.PlateNumber)
                    ? taxi.Model
                    : taxi.PlateNumber.Replace("-", "·");
            }
        }
        catch (System.Exception ex)
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
        // 1. Explicitly request camera permissions before launching the live scanner
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted) return; // Exit if denied
        }

        // 2. Open the dynamic live scanner Modal, passing ONLY the injected OCR service.
        // The OdometerScanPage will handle the live camera stream itself.
        await Application.Current.MainPage.Navigation.PushModalAsync(
            new LARGA.MobileApp.Views.Driver.OdometerScanPage(_ocrService));
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
}
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using LARGA.MobileApp.Services;

namespace LARGA.MobileApp.ViewModels.Driver;

// Removed IQueryAttributable since we use WeakReferenceMessenger for Modals
public class EndShiftStep2ViewModel : BindableObject
{
    private readonly IOcrService _ocrService;

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

    // Inject the OCR service via the constructor
    public EndShiftStep2ViewModel(IOcrService ocrService)
    {
        _ocrService = ocrService;

        // Register the Messenger to listen for the modal's return value
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Register<EndShiftStep2ViewModel, string, string>(this, "OdometerScanned", (r, scannedText) =>
        {
            FinalOdometer = scannedText;
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

            Preferences.Remove("IsShiftActive");
            await Shell.Current.GoToAsync("shift-completed");
        });

        SelectFuelCommand = new Command<string>((option) =>
        {
            if (option == "HalfTank") IsHalfTankSelected = true;
            else if (option == "BelowHalf") IsBelowHalfTankSelected = true;
        });
    }

    private async Task ScanOdometerAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted) return;
        }

        if (MediaPicker.Default.IsCaptureSupported)
        {
            var photo = await MediaPicker.Default.CapturePhotoAsync();

            if (photo != null)
            {
                using var stream = await photo.OpenReadAsync();
                using var memoryStream = new MemoryStream();
                await stream.CopyToAsync(memoryStream);
                byte[] imageBytes = memoryStream.ToArray();

                await Application.Current.MainPage.Navigation.PushModalAsync(
                    new LARGA.MobileApp.Views.Driver.OdometerScanPage(_ocrService, imageBytes));
            }
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
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Camera failed: {ex.Message}", "OK");
        }
    }
}
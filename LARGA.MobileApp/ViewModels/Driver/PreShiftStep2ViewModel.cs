using System.Windows.Input;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Driver;

public class PreShiftStep2ViewModel : BindableObject
{
    private bool _isHalfTankSelected;
    public bool IsHalfTankSelected
    {
        get => _isHalfTankSelected;
        set { _isHalfTankSelected = value; OnPropertyChanged(); }
    }

    public ICommand ScanOdometerCommand { get; }
    public ICommand AttachPhotoCommand { get; }
    public ICommand ConfirmStartShiftCommand { get; }

    public PreShiftStep2ViewModel()
    {
        ScanOdometerCommand = new Command(async () => await ScanOdometerAsync());
        AttachPhotoCommand = new Command(async () => await AttachPhotoAsync());
        ConfirmStartShiftCommand = new Command(async () => await StartShiftAsync());
    }

    private async Task ScanOdometerAsync()
    {
        // Google ML Kit OCR integration for live odometer reading
    }

    private async Task AttachPhotoAsync()
    {
        // Native camera capture logic for fuel gauge validation
    }

    private async Task StartShiftAsync()
    {
        // Finalize checklist and transition back to active dashboard state
        await Shell.Current.GoToAsync("//driver-dashboard");
    }
}
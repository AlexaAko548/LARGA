using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Driver;

public class EndShiftStep2ViewModel : BindableObject
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
            OnPropertyChanged(nameof(IsComplete));
        }
    }

    public bool IsOdometerScanned => !string.IsNullOrWhiteSpace(FinalOdometer);

    private bool _hasPhoto;
    public bool HasPhoto
    {
        get => _hasPhoto;
        set { _hasPhoto = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsComplete)); }
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

    public EndShiftStep2ViewModel()
    {
        ScanOdometerCommand = new Command(async () =>
        {
            await Task.Delay(500);
            FinalOdometer = "58,609 km";
        });

        AttachFuelPhotoCommand = new Command(async () =>
        {
            await Task.Delay(500);
            HasPhoto = true;
        });

        ConfirmEndShiftCommand = new Command(async () =>
        {
            if (!IsComplete)
            {
                await Shell.Current.DisplayAlert("Required", "Please complete all fields.", "OK");
                return;
            }
            await Shell.Current.GoToAsync("shift-completed");
        });

        SelectFuelCommand = new Command<string>((option) =>
        {
            if (option == "HalfTank") IsHalfTankSelected = true;
            else if (option == "BelowHalf") IsBelowHalfTankSelected = true;
        });
    }
}
using System.Windows.Input;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Driver;

public class EndShiftStep2ViewModel : BindableObject
{
    private string _finalOdometer = string.Empty;
    private bool _isHalfTank = true;

    public string FinalOdometer
    {
        get => _finalOdometer;
        set
        {
            if (_finalOdometer == value) return;
            _finalOdometer = value;
            OnPropertyChanged();
        }
    }

    public bool IsHalfTank
    {
        get => _isHalfTank;
        set
        {
            if (_isHalfTank == value) return;
            _isHalfTank = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBelowHalfTank));
            OnPropertyChanged(nameof(PenaltyNoteVisible));
        }
    }

    public bool IsBelowHalfTank
    {
        get => !_isHalfTank;
        set => IsHalfTank = !value;
    }

    public bool PenaltyNoteVisible => IsBelowHalfTank;

    public ICommand ScanOdometerCommand { get; }
    public ICommand AttachFuelPhotoCommand { get; }
    public ICommand ConfirmEndShiftCommand { get; }

    public EndShiftStep2ViewModel()
    {
        ScanOdometerCommand = new Command(async () =>
        {
            // TODO: trigger camera capture + OCR (LAR-12 integration point)
            FinalOdometer = "58,609 km";
        });

        AttachFuelPhotoCommand = new Command(async () =>
        {
            // TODO: trigger camera capture for fuel dashboard photo
        });

        ConfirmEndShiftCommand = new Command(async () =>
        {
            // TODO: save EndShift HandoverChecklist + update ShiftLog (EndMileage, Status=Completed) to Firestore
            await Shell.Current.GoToAsync("shift-completed");
        });
    }
}
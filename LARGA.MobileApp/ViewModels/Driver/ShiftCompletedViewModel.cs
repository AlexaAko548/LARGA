using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace LARGA.MobileApp.ViewModels.Driver;

public class ShiftCompletedViewModel : BindableObject
{
    public ICommand ReturnToHomeCommand { get; }

    public ShiftCompletedViewModel()
    {
        ReturnToHomeCommand = new Command(async () =>
        {
            // 1. Wipe the shift memory so the dashboard reverts to OFFLINE
            Preferences.Remove("IsShiftActive");
            Preferences.Remove("ShiftStartTime");

            // 2. Use the '//' absolute route to completely destroy the checklist stack
            // This guarantees no back button will appear on the Dashboard
            await Shell.Current.GoToAsync("//driver-dashboard");
        });
    }
}
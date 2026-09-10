using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace LARGA.MobileApp.ViewModels.Driver;

public class ProfileViewModel
{
    public ICommand LogoutCommand { get; }

    public ProfileViewModel()
    {
        LogoutCommand = new Command(async () =>
        {
            // Clears the stuck active shift state from the device memory[cite: 1]
            Preferences.Remove("IsShiftActive");
            Preferences.Remove("ShiftStartTime");

            // Routes the user completely out of the dashboard and back to the landing screen
            await Shell.Current.GoToAsync("///landing");
        });
    }
}
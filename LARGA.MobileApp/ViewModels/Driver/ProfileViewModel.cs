using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using Plugin.Firebase.Auth; // Required for Firebase SignOut

namespace LARGA.MobileApp.ViewModels.Driver;

public class ProfileViewModel
{
    public ICommand LogoutCommand { get; }

    public ProfileViewModel()
    {
        LogoutCommand = new Command(async () =>
        {
            // 1. Clear the stuck active shift state from the device memory[cite: 8]
            Preferences.Remove("IsShiftActive");
            Preferences.Remove("ShiftStartTime");

            // 2. Destroy the Firebase persistent session
            await CrossFirebaseAuth.Current.SignOutAsync();

            // 3. Route the user completely out of the dashboard[cite: 8]
            await Shell.Current.GoToAsync("//landing");
        });
    }
}
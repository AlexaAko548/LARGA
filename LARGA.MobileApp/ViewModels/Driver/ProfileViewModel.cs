using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Driver;

public class ProfileViewModel
{
    public ICommand LogoutCommand { get; }

    public ProfileViewModel()
    {
        // Routes the user completely out of the dashboard and back to the login screen
        LogoutCommand = new Command(async () => await Shell.Current.GoToAsync("///landing"));
    }
}
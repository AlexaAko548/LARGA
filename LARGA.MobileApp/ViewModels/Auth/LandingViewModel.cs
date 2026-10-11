using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Auth;

public class LandingViewModel
{
    public ICommand SelectRoleCommand { get; }

    public LandingViewModel()
    {
        SelectRoleCommand = new Command<string>(async (role) => await OnSelectRoleAsync(role));
    }

    private async Task OnSelectRoleAsync(string role)
    {
        // Pass the clicked role securely into the route dictionary
        await Shell.Current.GoToAsync($"login?SelectedRole={role}");
    }
}
using System.Windows.Input;

namespace LARGA.MobileApp.ViewModels;

public class LandingViewModel
{
    public ICommand SelectRoleCommand { get; }

    public LandingViewModel()
    {
        SelectRoleCommand = new Command<string>(async (role) => await OnSelectRoleAsync(role));
    }

    private async Task OnSelectRoleAsync(string role)
    {
        // Navigate to login
        await Shell.Current.GoToAsync("login");
    }
}

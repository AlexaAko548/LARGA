using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using LARGA.SharedCore.Services;

namespace LARGA.MobileApp.ViewModels.Auth;

public class ForgotPasswordViewModel : INotifyPropertyChanged
{
    private readonly IFirebaseAuthService _authService;
    private string _email = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Email
    {
        get => _email;
        set => SetProperty(ref _email, value);
    }

    public ICommand SendEmailCommand { get; }

    public ForgotPasswordViewModel(IFirebaseAuthService authService)
    {
        _authService = authService;
        SendEmailCommand = new Command(async () => await OnSendEmailAsync());
    }

    private async Task OnSendEmailAsync()
    {
        if (string.IsNullOrWhiteSpace(Email))
        {
            await Application.Current.MainPage.DisplayAlert("Error", "Please enter an email address.", "OK");
            return;
        }

        var success = await _authService.SendPasswordResetEmailAsync(Email);

        if (success)
        {
            await Application.Current.MainPage.DisplayAlert("Email Sent", "If this account exists, a secure password reset link has been sent to your inbox.", "OK");
            await Shell.Current.GoToAsync(".."); // Navigates back to the login screen
        }
        else
        {
            await Application.Current.MainPage.DisplayAlert("Error", "Could not send reset email. Please try again later.", "OK");
        }
    }

    protected void SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string propertyName = "")
    {
        if (Equals(backingStore, value)) return;
        backingStore = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
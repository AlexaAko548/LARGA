using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.ApplicationModel; // Required for MainThread execution
using Microsoft.Maui.Controls;
using LARGA.SharedCore.Services;

namespace LARGA.MobileApp.ViewModels;

public class LoginViewModel : INotifyPropertyChanged
{
    private readonly IFirebaseAuthService _authService;
    private string _email = string.Empty;
    private string _password = string.Empty;
    private string _errorMessage = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Email
    {
        get => _email;
        set => SetProperty(ref _email, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public ICommand LoginCommand { get; }
    public ICommand ForgotPasswordCommand { get; }

    public LoginViewModel(IFirebaseAuthService authService)
    {
        _authService = authService;
        LoginCommand = new Command(async () => await OnLoginAsync());
        ForgotPasswordCommand = new Command(async () => await OnForgotPasswordAsync());
    }

    private async Task OnForgotPasswordAsync()
    {
        await Shell.Current.GoToAsync("forgot-password-email");
    }

    private async Task OnLoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Please enter both email and password.";
            return;
        }

        try
        {
            ErrorMessage = string.Empty;

            // Log in from FirebaseAuthService
            var userId = await _authService.LoginAsync(Email, Password);
            if (!string.IsNullOrEmpty(userId))
            {
                // Fetch the user's role from Firestore
                var role = await _authService.GetUserRoleAsync(userId);

                // Route explicitly based on role, forcing execution on the UI thread
                if (role?.Equals("Driver", StringComparison.OrdinalIgnoreCase) == true)
                {
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        await Shell.Current.GoToAsync("//driver-dashboard");
                    });
                }
                else if (role?.Equals("Manager", StringComparison.OrdinalIgnoreCase) == true)
                {
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        await Shell.Current.GoToAsync("//manager-dashboard");
                    });
                }
                else
                {
                    ErrorMessage = "Unrecognized user role.";
                }
            }
            else
            {
                ErrorMessage = "Invalid credentials.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Login failed: {ex.Message}";
        }
    }

    protected void SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string propertyName = "")
    {
        if (Equals(backingStore, value)) return;
        backingStore = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
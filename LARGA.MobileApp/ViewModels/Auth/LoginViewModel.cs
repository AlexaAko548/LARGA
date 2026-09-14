using System;
using System.Collections.Generic; // Required for IDictionary
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using LARGA.SharedCore.Services;

namespace LARGA.MobileApp.ViewModels.Auth;

public class LoginViewModel : INotifyPropertyChanged, IQueryAttributable
{
    private readonly IFirebaseAuthService _authService;
    private string _email = string.Empty;
    private string _password = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _rememberMe;
    private string _expectedRole = "Driver"; // Fallback default

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Email { get => _email; set => SetProperty(ref _email, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string ErrorMessage { get => _errorMessage; set => SetProperty(ref _errorMessage, value); }

    public bool RememberMe
    {
        get => _rememberMe;
        set => SetProperty(ref _rememberMe, value);
    }

    public ICommand LoginCommand { get; }
    public ICommand ForgotPasswordCommand { get; }

    public LoginViewModel(IFirebaseAuthService authService)
    {
        _authService = authService;
        LoginCommand = new Command(async () => await OnLoginAsync());
        ForgotPasswordCommand = new Command(async () => await OnForgotPasswordAsync());

        // Removed LoadSavedCredentialsAsync() from here. It must wait for the role.
    }

    // Catch the role passed from the Landing Page
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("SelectedRole", out var roleValue))
        {
            _expectedRole = roleValue?.ToString() ?? "Driver";
        }

        // Load credentials NOW that we know if it is a Driver or Manager
        _ = LoadSavedCredentialsAsync();
    }

    private async Task LoadSavedCredentialsAsync()
    {
        // Use role-specific keys to prevent overlap
        RememberMe = Preferences.Get($"{_expectedRole}_RememberMe", false);
        if (RememberMe)
        {
            Email = Preferences.Get($"{_expectedRole}_SavedEmail", string.Empty);
            Password = await SecureStorage.GetAsync($"{_expectedRole}_SavedPassword") ?? string.Empty;
        }
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
            var userId = await _authService.LoginAsync(Email, Password);

            if (!string.IsNullOrEmpty(userId))
            {
                // Save or clear credentials using the prefixed keys
                if (RememberMe)
                {
                    Preferences.Set($"{_expectedRole}_RememberMe", true);
                    Preferences.Set($"{_expectedRole}_SavedEmail", Email);
                    await SecureStorage.SetAsync($"{_expectedRole}_SavedPassword", Password);
                }
                else
                {
                    Preferences.Remove($"{_expectedRole}_RememberMe");
                    Preferences.Remove($"{_expectedRole}_SavedEmail");
                    SecureStorage.Remove($"{_expectedRole}_SavedPassword");
                }

                var role = await _authService.GetUserRoleAsync(userId);

                if (role?.Equals("Driver", StringComparison.OrdinalIgnoreCase) == true)
                {
                    MainThread.BeginInvokeOnMainThread(async () => await Shell.Current.GoToAsync("//driver-dashboard"));
                }
                else if (role?.Equals("Manager", StringComparison.OrdinalIgnoreCase) == true)
                {
                    MainThread.BeginInvokeOnMainThread(async () => await Shell.Current.GoToAsync("//manager-dashboard"));
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
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using LARGA.SharedCore.Services;

namespace LARGA.MobileApp.ViewModels.Auth;

public class LoginViewModel : INotifyPropertyChanged, IQueryAttributable
{
    private readonly IFirebaseAuthService _authService;
    private string _email = string.Empty;
    private string _password = string.Empty;
    private string _errorMessage = string.Empty;
    private string _expectedRole = string.Empty;

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

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("SelectedRole", out var roleValue))
        {
            _expectedRole = roleValue?.ToString() ?? string.Empty;
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
                var role = await _authService.GetUserRoleAsync(userId);
                var normalizedRole = NormalizeRole(role);

                // FIX: Strictly compare the database role with the landing page button they clicked
                if (!string.Equals(role, _expectedRole, StringComparison.OrdinalIgnoreCase))
                {
                    ErrorMessage = $"Access Denied: You selected {_expectedRole}, but your account is registered as a {role}.";
                    return;
                }

                if (role?.Equals("Driver", StringComparison.OrdinalIgnoreCase) == true)
                {
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        await Shell.Current.GoToAsync("//driver-dashboard");
                    });
                }
                else if (IsManagerRole(normalizedRole))
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

    private static string NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return string.Empty;
        }

        return role.Trim().Replace("_", " ").Replace("-", " ");
    }

    private static bool IsDriverRole(string? role)
    {
        return !string.IsNullOrWhiteSpace(role) &&
               role.Contains("driver", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsManagerRole(string? role)
    {
        return !string.IsNullOrWhiteSpace(role) &&
               role.Contains("manager", StringComparison.OrdinalIgnoreCase);
    }

    protected void SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string propertyName = "")
    {
        if (Equals(backingStore, value)) return;
        backingStore = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
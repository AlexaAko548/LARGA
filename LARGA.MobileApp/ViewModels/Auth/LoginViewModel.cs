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
    private bool _isBusy;
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

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            SetProperty(ref _isBusy, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNotBusy)));
        }
    }
    public bool IsNotBusy => !_isBusy;

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

    private Task LoadSavedCredentialsAsync()
    {
        // Use role-specific keys to prevent overlap
        RememberMe = Preferences.Get($"{_expectedRole}_RememberMe", false);
        if (RememberMe)
        {
            Email = Preferences.Get($"{_expectedRole}_SavedEmail", string.Empty);
        }

        // Remember Me keeps the email only (audit B-04): Firebase already keeps the user signed in,
        // so the password itself never needs to be on the phone. Clear any an older build saved.
        SecureStorage.Remove($"{_expectedRole}_SavedPassword");
        return Task.CompletedTask;
    }

    private async Task OnForgotPasswordAsync()
    {
        await Shell.Current.GoToAsync("forgot-password-email");
    }

    private async Task OnLoginAsync()
    {
        if (IsBusy) return; // ignore repeat taps while a sign-in is in flight

        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ShowError("Please enter both email and password.");
            return;
        }

        if (!LARGA.SharedCore.InputValidator.IsValidEmail(Email))
        {
            ShowError("Enter a valid email address, e.g. juan.delacruz@gmail.com.");
            return;
        }

        IsBusy = true;
        ShowError(string.Empty);
        try
        {
            // Throws AuthFailedException with a user-facing message on any failure.
            var userId = await _authService.LoginAsync(Email.Trim(), Password);

            string role;
            try
            {
                role = await _authService.GetUserRoleAsync(userId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Role lookup failed: {ex}");
                await SignOutQuietlyAsync();
                ShowError($"Signed in, but could not load your profile: {ex.Message}");
                return;
            }

            // An Assistant Manager gets the manager dashboard too, with the Manager-only parts left out.
            string? route = LARGA.SharedCore.StaffRoles.HomeRoute(role);

            if (route is null)
            {
                // Don't leave a half-signed-in session behind; LandingPage would silently ignore it.
                await SignOutQuietlyAsync();
                ShowError(string.IsNullOrEmpty(role)
                    ? "Your account has no role assigned (users/{uid}.role). Please contact your manager."
                    : $"Unrecognized user role \"{role}\".");
                return;
            }

            // DSM-006: the account must belong to the tile it was signed in from - a Manager can't
            // get in through the Driver tile or the other way round. Signed out straight away, and
            // the tile's remembered credentials are left as they were.
            if (!LARGA.SharedCore.StaffRoles.MatchesLoginTile(role, _expectedRole))
            {
                await SignOutQuietlyAsync();
                string otherTile = _expectedRole.Equals("Driver", StringComparison.OrdinalIgnoreCase) ? "Manager" : "Driver";
                ShowError($"This account is not registered as a {_expectedRole}. Please use the {otherTile} login.");
                return;
            }

            LARGA.MobileApp.Services.StaffSession.Role = role.Trim();

            // Only persist credentials once the login fully succeeded.
            await SaveOrClearCredentialsAsync();

            // Awaited (not fire-and-forget) so navigation/page-construction failures land in the catch below.
            await MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync(route));
        }
        catch (AuthFailedException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Login flow error: {ex}");
            ShowError($"Login failed ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task SaveOrClearCredentialsAsync()
    {
        // Save or clear credentials using the prefixed keys
        if (RememberMe)
        {
            Preferences.Set($"{_expectedRole}_RememberMe", true);
            Preferences.Set($"{_expectedRole}_SavedEmail", Email);
        }
        else
        {
            Preferences.Remove($"{_expectedRole}_RememberMe");
            Preferences.Remove($"{_expectedRole}_SavedEmail");
        }

        return Task.CompletedTask;
    }

    private async Task SignOutQuietlyAsync()
    {
        try { await _authService.SignOutAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Sign-out after failed login failed: {ex}"); }
    }

    // Bound properties must change on the UI thread or Android may drop/reject the update.
    private void ShowError(string message) =>
        MainThread.BeginInvokeOnMainThread(() => ErrorMessage = message);

    protected void SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string propertyName = "")
    {
        if (Equals(backingStore, value)) return;
        backingStore = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
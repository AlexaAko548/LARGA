using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.MobileApp.Services;
using LARGA.SharedCore;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.ViewModels.Auth;

/// <summary>
/// The change-password screen a user can't leave while users/{uid}.mustChangePassword is true
/// (ForcedPasswordChangeRules): shown over the dashboard right after login or auto-login. The only
/// other way out is signing out.
/// </summary>
public class ForcedPasswordChangeViewModel : BindableObject
{
    private string _currentPassword = string.Empty;
    public string CurrentPassword
    {
        get => _currentPassword;
        set { _currentPassword = value; OnPropertyChanged(); }
    }

    private string _newPassword = string.Empty;
    public string NewPassword
    {
        get => _newPassword;
        set { _newPassword = value; OnPropertyChanged(); }
    }

    private string _confirmNewPassword = string.Empty;
    public string ConfirmNewPassword
    {
        get => _confirmNewPassword;
        set { _confirmNewPassword = value; OnPropertyChanged(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotBusy)); }
    }

    public bool IsNotBusy => !IsBusy;

    public ICommand SaveCommand { get; }
    public ICommand SignOutCommand { get; }

    /// <summary>Set by the page: closes it once the password is changed.</summary>
    public Func<Task>? Completed { get; set; }

    public ForcedPasswordChangeViewModel()
    {
        SaveCommand = new Command(async () => await SaveAsync());
        SignOutCommand = new Command(async () => await SignOutAsync());
    }

    private async Task SaveAsync()
    {
        if (IsBusy) return;

        if (ForcedPasswordChangeRules.Validate(CurrentPassword, NewPassword, ConfirmNewPassword) is string error)
        {
            await Shell.Current.DisplayAlert("Can't save yet", error, "OK");
            return;
        }

        IsBusy = true;
        try
        {
            IFirebaseUser? user = CrossFirebaseAuth.Current.CurrentUser;
            if (user is null || string.IsNullOrWhiteSpace(user.Email))
            {
                await Shell.Current.DisplayAlert("Error", "Unable to verify account email. Please log in again.", "OK");
                return;
            }

            try
            {
                // Firebase needs a recent sign-in to change the password.
                await CrossFirebaseAuth.Current.SignInWithEmailAndPasswordAsync(user.Email, CurrentPassword);
                await CrossFirebaseAuth.Current.CurrentUser.UpdatePasswordAsync(NewPassword);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Forced password change failed: {ex.Message}");
                await Shell.Current.DisplayAlert("Error", "Incorrect temporary password, or the password couldn't be updated.", "OK");
                return;
            }

            try
            {
                await CrossFirebaseFirestore.Current
                    .GetCollection("users")
                    .GetDocument(user.Uid)
                    .UpdateDataAsync(new Dictionary<object, object> { [ForcedPasswordChangeRules.Field] = false });
            }
            catch (Exception ex)
            {
                // The password is changed; the flag clears next time (this screen would show again).
                FirestoreErrors.Log("Clear mustChangePassword", ex);
            }

            CurrentPassword = NewPassword = ConfirmNewPassword = string.Empty;
            await Shell.Current.DisplayAlert("Password updated", "Your new password is set. Use it the next time you sign in.", "OK");
            if (Completed is not null)
            {
                await Completed();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SignOutAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await SignOutService.SignOutAsync();
            if (Completed is not null)
            {
                await Completed();
            }
            await Shell.Current.GoToAsync("//landing");
        }
        finally
        {
            IsBusy = false;
        }
    }
}

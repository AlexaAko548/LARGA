using System;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.MobileApp.Services;
using LARGA.SharedCore;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;

namespace LARGA.MobileApp.ViewModels.Auth;

/// <summary>
/// The change-password screen a user can't leave while users/{uid}.mustChangePassword is true
/// (ForcedPasswordChangeRules): shown over the dashboard right after login or auto-login. The only
/// other way out is signing out. Same form as the regular Change Password screens
/// (PasswordFormViewModel: show/hide eyes, live policy checklist, save enabled once valid).
/// </summary>
public class ForcedPasswordChangeViewModel : LARGA.MobileApp.ViewModels.Shared.PasswordFormViewModel
{
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
                await (CrossFirebaseAuth.Current.CurrentUser ?? user).UpdatePasswordAsync(NewPassword);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Forced password change failed: {ex.Message}");
                await Shell.Current.DisplayAlert("Error", "Incorrect current password, or the password couldn't be updated.", "OK");
                return;
            }

            await MustChangePasswordFlag.ClearAsync();
            AuditLogWriter.Record(StaffSession.IsManager || StaffSession.IsAssistantManager ? "ManagerPasswordChanged" : "DriverPasswordChanged",
                "Replaced their temporary password.");

            ClearForm();
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

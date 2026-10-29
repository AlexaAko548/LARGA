using System;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.MobileApp.Services;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;

namespace LARGA.MobileApp.ViewModels.Manager;

public class ChangePasswordViewModel : LARGA.MobileApp.ViewModels.Shared.PasswordFormViewModel
{
    // One save at a time - a second tap while the first is still validating or saving is ignored.
    private readonly LARGA.MobileApp.Services.BusyGate _saveGate = new();

    public ICommand SaveCommand { get; }
    public ICommand GoBackCommand { get; }

    public ChangePasswordViewModel()
    {
        SaveCommand = new Command(async () => await _saveGate.RunAsync(SaveAsync, showLoading: false));
        GoBackCommand = new Command(async () => await GoBackAsync());
    }

    /// <summary>Back (nav bar arrow or the phone's back button): asks first when something was typed.</summary>
    public Task GoBackAsync() => LARGA.MobileApp.Services.NavigationGate.RunAsync(async () =>
    {
        if (await LARGA.MobileApp.ViewModels.Shared.UnsavedChangesPrompt.ConfirmLeaveAsync(HasChanges))
        {
            await Shell.Current.GoToAsync("..");
        }
    });

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentPassword) || string.IsNullOrWhiteSpace(NewPassword) || string.IsNullOrWhiteSpace(ConfirmNewPassword))
        {
            await Shell.Current.DisplayAlert("Missing Info", "Please fill in all three fields.", "OK");
            return;
        }

        if (NewPassword != ConfirmNewPassword)
        {
            await Shell.Current.DisplayAlert("Passwords Don't Match", "New password and confirmation must match.", "OK");
            return;
        }

        if (NewPassword == CurrentPassword)
        {
            await Shell.Current.DisplayAlert("Same Password", LARGA.SharedCore.InputValidator.SamePasswordMessage, "OK");
            return;
        }

        // Same policy as account creation and ManagerWeb (InputValidator.PasswordRules).
        if (LARGA.SharedCore.InputValidator.ValidatePassword(NewPassword) is string passwordError)
        {
            await Shell.Current.DisplayAlert("Invalid Password", passwordError, "OK");
            return;
        }

        IsBusy = true;
        try
        {
            var user = CrossFirebaseAuth.Current.CurrentUser;
            var email = user?.Email;
            if (string.IsNullOrWhiteSpace(email))
            {
                await Shell.Current.DisplayAlert("Error", "Couldn't verify your account. Please log out and back in, then try again.", "OK");
                return;
            }

            // Firebase requires a recent sign-in before it allows a sensitive operation like a
            // password change. Signing in again with the current password refreshes that,
            // and also doubles as verifying the current password is actually correct.
            await CrossFirebaseAuth.Current.SignInWithEmailAndPasswordAsync(email, CurrentPassword);
            await CrossFirebaseAuth.Current.CurrentUser.UpdatePasswordAsync(NewPassword);
            await LARGA.MobileApp.Services.MustChangePasswordFlag.ClearAsync();
            AuditLogWriter.Record("ManagerPasswordChanged", "Changed their own mobile app password.");

            ClearForm();

            await Shell.Current.DisplayAlert("Success", "Your password has been updated.", "OK");
            await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Change Password Error: {ex.Message}");
            await Shell.Current.DisplayAlert("Error", "Couldn't change your password. Double-check your current password and try again.", "OK");
        }
        finally
        {
            IsBusy = false;
        }
    }
}

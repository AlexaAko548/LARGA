using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;

namespace LARGA.MobileApp.ViewModels.Driver;

public class DriverChangePasswordViewModel : BindableObject
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
    public ICommand GoBackCommand { get; }

    public DriverChangePasswordViewModel()
    {
        SaveCommand = new Command(async () => await SaveAsync());
        GoBackCommand = new Command(async () => await Shell.Current.GoToAsync(".."));
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentPassword) || string.IsNullOrWhiteSpace(NewPassword) || string.IsNullOrWhiteSpace(ConfirmNewPassword))
        {
            await Shell.Current.DisplayAlert("Missing Info", "Please fill in all fields.", "OK");
            return;
        }

        if (NewPassword != ConfirmNewPassword)
        {
            await Shell.Current.DisplayAlert("Passwords Don't Match", "New password and confirmation must match.", "OK");
            return;
        }

        if (NewPassword.Length < 6)
        {
            await Shell.Current.DisplayAlert("Invalid Password", "Your new password must be at least 6 characters.", "OK");
            return;
        }

        IsBusy = true;
        try
        {
            var user = CrossFirebaseAuth.Current.CurrentUser;
            var email = user?.Email;
            if (string.IsNullOrWhiteSpace(email))
            {
                await Shell.Current.DisplayAlert("Error", "Unable to verify account email. Please log in again.", "OK");
                return;
            }

            await CrossFirebaseAuth.Current.SignInWithEmailAndPasswordAsync(email, CurrentPassword);
            await CrossFirebaseAuth.Current.CurrentUser.UpdatePasswordAsync(NewPassword);

            CurrentPassword = string.Empty;
            NewPassword = string.Empty;
            ConfirmNewPassword = string.Empty;

            await Shell.Current.DisplayAlert("Success", "Your password has been updated.", "OK");
            await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Driver Change Password Error: {ex.Message}");
            await Shell.Current.DisplayAlert("Error", "Incorrect current password or password update failed.", "OK");
        }
        finally
        {
            IsBusy = false;
        }
    }
}

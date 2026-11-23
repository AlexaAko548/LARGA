using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.MobileApp.Services;
using LARGA.SharedCore;
using LARGA.SharedCore.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.ViewModels.Manager;

public class UpdateContactNumberViewModel : BindableObject
{
    private string _currentNumber = string.Empty;
    public string CurrentNumber
    {
        get => _currentNumber;
        set { _currentNumber = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasChanges)); }
    }

    private string _newNumber = string.Empty;
    public string NewNumber
    {
        get => _newNumber;
        set { _newNumber = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasChanges)); }
    }

    private string _confirmNewNumber = string.Empty;
    public string ConfirmNewNumber
    {
        get => _confirmNewNumber;
        set { _confirmNewNumber = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasChanges)); }
    }

    // BETA-062: a contact-number change is security-sensitive (it feeds the SOS auto-answer
    // allowlist), so it's confirmed with the account password the same way a password change is.
    private string _password = string.Empty;
    public string Password
    {
        get => _password;
        set { _password = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasChanges)); }
    }

    public LARGA.MobileApp.ViewModels.Shared.PasswordVisibility PasswordField { get; } = new();

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotBusy)); }
    }

    public bool IsNotBusy => !IsBusy;

    // The number on file when the screen opened, so a prefilled field doesn't count as an edit.
    private string _loadedNumber = string.Empty;

    /// <summary>Anything typed that leaving the screen would throw away.</summary>
    public bool HasChanges => NewNumber.Length > 0 || ConfirmNewNumber.Length > 0 || Password.Length > 0 || CurrentNumber != _loadedNumber;

    public ICommand LoadCurrentNumberCommand { get; }
    // One save at a time - a second tap while the first is still validating or saving is ignored.
    private readonly LARGA.MobileApp.Services.BusyGate _saveGate = new();

    public ICommand SaveCommand { get; }
    public ICommand GoBackCommand { get; }

    public UpdateContactNumberViewModel()
    {
        LoadCurrentNumberCommand = new Command(async () => await LoadCurrentNumberAsync());
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

    // Shows the number on file in the Current Number field, same as the driver's screen.
    private async Task LoadCurrentNumberAsync()
    {
        using var _loading = LARGA.MobileApp.Services.LoadingState.Begin(); // LAR-117: app-wide loading indicator
        try
        {
            var user = CrossFirebaseAuth.Current.CurrentUser;
            if (user == null) return;

            var doc = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(user.Uid)
                .GetDocumentSnapshotAsync<ContactNumberProxy>();

            if (doc?.Data == null) return;

            _loadedNumber = doc.Data.PhoneNumber ?? string.Empty;
            CurrentNumber = _loadedNumber;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Load Contact Number Error: {ex.Message}");
        }
    }

    private async Task SaveAsync()
    {
        // Everything (including the validation alerts) is inside this try: an unhandled exception
        // here would otherwise escape the async-void Command/BusyGate and crash the app (BETA-062).
        try
        {
            if (string.IsNullOrWhiteSpace(CurrentNumber) || string.IsNullOrWhiteSpace(NewNumber) || string.IsNullOrWhiteSpace(ConfirmNewNumber) || string.IsNullOrWhiteSpace(Password))
            {
                await Shell.Current.DisplayAlert("Missing Info", "Please fill in all fields, including your password.", "OK");
                return;
            }

            if (!InputValidator.SamePhone(NewNumber, ConfirmNewNumber))
            {
                await Shell.Current.DisplayAlert("Numbers Don't Match", "New number and confirmation must match.", "OK");
                return;
            }

            // Same rule as ManagerWeb (InputValidator): a Philippine mobile number, stored as +639XXXXXXXXX.
            if (InputValidator.ValidatePhilippineMobile(NewNumber) is string phoneError)
            {
                await Shell.Current.DisplayAlert("Invalid Number", phoneError, "OK");
                return;
            }

            IsBusy = true;
            var user = CrossFirebaseAuth.Current.CurrentUser;
            if (user == null) return;

            var email = user.Email;
            if (string.IsNullOrWhiteSpace(email))
            {
                await Shell.Current.DisplayAlert("Error", "Couldn't verify your account. Please log out and back in, then try again.", "OK");
                return;
            }

            // BETA-062: confirm the password before a security-sensitive contact-number change.
            // Re-authenticating also doubles as verifying the password is actually correct. Goes
            // through IFirebaseAuthService rather than a raw SignInWithEmailAndPasswordAsync call
            // so a network failure or Firebase lockout isn't misreported as "wrong password"
            // (review follow-up on BETA-062).
            IFirebaseAuthService? authService = Application.Current?.Handler?.MauiContext?.Services
                .GetService<IFirebaseAuthService>();
            try
            {
                if (authService is not null)
                {
                    await authService.LoginAsync(email, Password);
                }
                else
                {
                    await CrossFirebaseAuth.Current.SignInWithEmailAndPasswordAsync(email, Password);
                }
            }
            catch (AuthFailedException ex)
            {
                await Shell.Current.DisplayAlert("Couldn't Confirm Password", ex.Message, "OK");
                return;
            }
            catch (Exception)
            {
                await Shell.Current.DisplayAlert("Incorrect Password", "That password is incorrect.", "OK");
                return;
            }

            var doc = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(user.Uid)
                .GetDocumentSnapshotAsync<ContactNumberProxy>();

            var actualCurrentNumber = doc?.Data?.PhoneNumber ?? string.Empty;
            if (!InputValidator.SamePhone(actualCurrentNumber, CurrentNumber))
            {
                await Shell.Current.DisplayAlert("Doesn't Match", "That doesn't match the number currently on file.", "OK");
                return;
            }

            var updates = new Dictionary<object, object>
            {
                ["phoneNumber"] = InputValidator.NormalizePhilippineMobile(NewNumber)!
            };

            await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(user.Uid)
                .UpdateDataAsync(updates);

            // LAR-86/87: the driver app's SOS auto-answer allowlist (system_configs/global.managerPhoneNumbers)
            // follows this number through ManagerWeb's ManagerPhoneAllowlistSyncService, its only writer.
            AuditLogWriter.Record("ManagerContactNumberChanged", "Updated their own contact number.");

            _loadedNumber = string.Empty;
            CurrentNumber = string.Empty;
            NewNumber = string.Empty;
            ConfirmNewNumber = string.Empty;
            Password = string.Empty;

            await Shell.Current.DisplayAlert("Success", "Your contact number has been updated.", "OK");
            await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Update Contact Number Error: {ex.Message}");
            await SafeAlert.ShowAsync("Error", "Couldn't update your contact number. Please try again.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private class ContactNumberProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("phoneNumber")]
        public string PhoneNumber { get; set; } = string.Empty;
    }
}

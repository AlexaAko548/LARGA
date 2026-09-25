using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
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
        set { _currentNumber = value; OnPropertyChanged(); }
    }

    private string _newNumber = string.Empty;
    public string NewNumber
    {
        get => _newNumber;
        set { _newNumber = value; OnPropertyChanged(); }
    }

    private string _confirmNewNumber = string.Empty;
    public string ConfirmNewNumber
    {
        get => _confirmNewNumber;
        set { _confirmNewNumber = value; OnPropertyChanged(); }
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

    public UpdateContactNumberViewModel()
    {
        SaveCommand = new Command(async () => await SaveAsync());
        GoBackCommand = new Command(async () => await Shell.Current.GoToAsync(".."));
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentNumber) || string.IsNullOrWhiteSpace(NewNumber) || string.IsNullOrWhiteSpace(ConfirmNewNumber))
        {
            await Shell.Current.DisplayAlert("Missing Info", "Please fill in all three fields.", "OK");
            return;
        }

        if (NewNumber != ConfirmNewNumber)
        {
            await Shell.Current.DisplayAlert("Numbers Don't Match", "New number and confirmation must match.", "OK");
            return;
        }

        IsBusy = true;
        try
        {
            var user = CrossFirebaseAuth.Current.CurrentUser;
            if (user == null) return;

            var doc = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(user.Uid)
                .GetDocumentSnapshotAsync<ContactNumberProxy>();

            var actualCurrentNumber = doc?.Data?.PhoneNumber ?? string.Empty;
            if (!string.Equals(actualCurrentNumber.Trim(), CurrentNumber.Trim(), StringComparison.Ordinal))
            {
                await Shell.Current.DisplayAlert("Doesn't Match", "That doesn't match the number currently on file.", "OK");
                return;
            }

            var updates = new Dictionary<object, object>
            {
                ["phoneNumber"] = NewNumber.Trim()
            };

            await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(user.Uid)
                .UpdateDataAsync(updates);

            CurrentNumber = string.Empty;
            NewNumber = string.Empty;
            ConfirmNewNumber = string.Empty;

            await Shell.Current.DisplayAlert("Success", "Your contact number has been updated.", "OK");
            await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Update Contact Number Error: {ex.Message}");
            await Shell.Current.DisplayAlert("Error", "Couldn't update your contact number. Please try again.", "OK");
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

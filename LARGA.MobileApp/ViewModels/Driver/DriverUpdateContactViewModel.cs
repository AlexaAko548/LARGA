using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.ViewModels.Driver;

public class DriverUpdateContactViewModel : BindableObject
{
    private static readonly Regex ContactNumberRegex = new("^\\+?\\d{10,13}$", RegexOptions.Compiled);

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

    public ICommand LoadCurrentNumberCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand GoBackCommand { get; }

    public DriverUpdateContactViewModel()
    {
        LoadCurrentNumberCommand = new Command(async () => await LoadCurrentNumberAsync());
        SaveCommand = new Command(async () => await SaveAsync());
        GoBackCommand = new Command(async () => await Shell.Current.GoToAsync(".."));
    }

    private async Task LoadCurrentNumberAsync()
    {
        try
        {
            var user = CrossFirebaseAuth.Current.CurrentUser;
            if (user == null) return;

            var doc = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(user.Uid)
                .GetDocumentSnapshotAsync<ContactNumberProxy>();

            if (doc?.Data == null) return;

            CurrentNumber = doc.Data.PhoneNumber ?? string.Empty;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Driver Load Contact Number Error: {ex.Message}");
        }
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentNumber) || string.IsNullOrWhiteSpace(NewNumber) || string.IsNullOrWhiteSpace(ConfirmNewNumber))
        {
            await Shell.Current.DisplayAlert("Missing Info", "Please fill in all fields.", "OK");
            return;
        }

        if (NewNumber != ConfirmNewNumber)
        {
            await Shell.Current.DisplayAlert("Numbers Don't Match", "New number and confirmation must match.", "OK");
            return;
        }

        var normalizedCurrent = NormalizePhone(CurrentNumber);
        var normalizedNew = NormalizePhone(NewNumber);

        if (!ContactNumberRegex.IsMatch(normalizedNew))
        {
            await Shell.Current.DisplayAlert("Invalid Number", "Please enter a valid contact number (10-13 digits).", "OK");
            return;
        }

        IsBusy = true;
        try
        {
            var user = CrossFirebaseAuth.Current.CurrentUser;
            if (user == null)
            {
                await Shell.Current.DisplayAlert("Error", "You are not logged in.", "OK");
                return;
            }

            var doc = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(user.Uid)
                .GetDocumentSnapshotAsync<ContactNumberProxy>();

            var currentOnFile = NormalizePhone(doc?.Data?.PhoneNumber ?? string.Empty);
            if (!string.Equals(currentOnFile, normalizedCurrent, StringComparison.Ordinal))
            {
                await Shell.Current.DisplayAlert("Doesn't Match", "Current number does not match our records.", "OK");
                return;
            }

            var updates = new Dictionary<object, object>
            {
                ["phoneNumber"] = normalizedNew
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
            System.Diagnostics.Debug.WriteLine($"Driver Update Contact Number Error: {ex.Message}");
            await Shell.Current.DisplayAlert("Error", "Unable to update your contact number.", "OK");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string NormalizePhone(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var trimmed = input.Trim();
        if (trimmed.StartsWith("+", StringComparison.Ordinal))
        {
            return "+" + Regex.Replace(trimmed[1..], "[^0-9]", string.Empty);
        }

        return Regex.Replace(trimmed, "[^0-9]", string.Empty);
    }

    private class ContactNumberProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("phoneNumber")]
        public string PhoneNumber { get; set; } = string.Empty;
    }
}

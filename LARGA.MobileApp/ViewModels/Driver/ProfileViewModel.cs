using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.MobileApp.Services;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.ViewModels.Driver;

// Read-only: license details are set by the manager (Manager Driver Profile > Upload License
// Photo), not self-reported by the driver - a driver has no command here that writes to their
// own licenseNumber/licenseClassification/licenseExpiryDate fields.
public class ProfileViewModel : BindableObject
{
    private string _fullName = "Loading...";
    public string FullName
    {
        get => _fullName;
        set { _fullName = value; OnPropertyChanged(); OnPropertyChanged(nameof(Initials)); }
    }

    public string Initials => NameHelper.Initials(FullName);

    private string _statusText = "none";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private Color _statusColor = Colors.Gray;
    public Color StatusColor
    {
        get => _statusColor;
        set { _statusColor = value; OnPropertyChanged(); }
    }

    private Color _statusBgColor = Colors.WhiteSmoke;
    public Color StatusBgColor
    {
        get => _statusBgColor;
        set { _statusBgColor = value; OnPropertyChanged(); }
    }

    private string _credentialsSummary = "No details yet.";
    public string CredentialsSummary
    {
        get => _credentialsSummary;
        set { _credentialsSummary = value; OnPropertyChanged(); }
    }

    public ICommand LoadProfileCommand { get; }
    public ICommand LogoutCommand { get; }

    public ProfileViewModel()
    {
        LoadProfileCommand = new Command(async () => await LoadProfileAsync());

        LogoutCommand = new Command(async () =>
        {
            Preferences.Remove("IsShiftActive");
            Preferences.Remove("ShiftStartTime");
            await CrossFirebaseAuth.Current.SignOutAsync();
            await Shell.Current.GoToAsync("//landing");
        });
    }

    private async Task LoadProfileAsync()
    {
        try
        {
            var currentUser = CrossFirebaseAuth.Current.CurrentUser;
            if (currentUser == null) return;

            var doc = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(currentUser.Uid)
                .GetDocumentSnapshotAsync<DriverProfileProxy>();

            if (doc?.Data == null) return;

            FullName = string.IsNullOrWhiteSpace(doc.Data.FullName) ? "Driver" : doc.Data.FullName;
            (StatusText, StatusColor, StatusBgColor) = LicenseStatusHelper.Describe(doc.Data.LicenseExpiryDate);

            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(doc.Data.LicenseNumber)) details.Add($"License No: {doc.Data.LicenseNumber}");
            if (!string.IsNullOrWhiteSpace(doc.Data.LicenseClassification)) details.Add($"DL Codes: {doc.Data.LicenseClassification}");
            if (doc.Data.LicenseExpiryDate != null)
            {
                var expiry = FirestoreDateTimeFix.Apply(doc.Data.LicenseExpiryDate.Value.UtcDateTime).ToLocalTime();
                details.Add($"Expires: {expiry:MMM d, yyyy}");
            }

            CredentialsSummary = details.Count == 0 ? "No details yet." : string.Join("\n", details);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Driver Profile Load Error: {ex.Message}");
        }
    }

    private class DriverProfileProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("fullName")]
        public string FullName { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("licenseNumber")]
        public string LicenseNumber { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("licenseClassification")]
        public string LicenseClassification { get; set; } = string.Empty;

        // DateTimeOffset?, not DateTime? - see LicenseStatusHelper.Describe for why.
        [Plugin.Firebase.Firestore.FirestoreProperty("licenseExpiryDate")]
        public DateTimeOffset? LicenseExpiryDate { get; set; }
    }
}

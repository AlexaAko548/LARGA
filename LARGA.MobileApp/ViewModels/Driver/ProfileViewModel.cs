using System;
using System;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.MobileApp.Services;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
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
        set { _fullName = value; OnPropertyChanged(); }
    }

    private string _contactNumber = "N/A";
    public string ContactNumber
    {
        get => _contactNumber;
        set { _contactNumber = value; OnPropertyChanged(); }
    }

    private string _profileImageUrl = string.Empty;
    public string ProfileImageUrl
    {
        get => _profileImageUrl;
        set { _profileImageUrl = value; OnPropertyChanged(); }
    }

    private string _licenseNumber = "N/A";
    public string LicenseNumber
    {
        get => _licenseNumber;
        set { _licenseNumber = value; OnPropertyChanged(); }
    }

    private string _expiryDateDisplay = "N/A";
    public string ExpiryDateDisplay
    {
        get => _expiryDateDisplay;
        set { _expiryDateDisplay = value; OnPropertyChanged(); }
    }

    private string _statusText = "NONE";
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

    private string _dlCodes = "N/A";
    public string DlCodes
    {
        get => _dlCodes;
        set { _dlCodes = value; OnPropertyChanged(); }
    }

    private string _paymentReliability = "N/A";
    public string PaymentReliability
    {
        get => _paymentReliability;
        set { _paymentReliability = value; OnPropertyChanged(); }
    }

    private string _shiftPunctualityDisplay = "0%";
    public string ShiftPunctualityDisplay
    {
        get => _shiftPunctualityDisplay;
        set { _shiftPunctualityDisplay = value; OnPropertyChanged(); }
    }

    private string _damageHistoryDisplay = "0 Incidents";
    public string DamageHistoryDisplay
    {
        get => _damageHistoryDisplay;
        set { _damageHistoryDisplay = value; OnPropertyChanged(); }
    }

    public ICommand LoadProfileCommand { get; }
    public ICommand ChangePasswordCommand { get; }
    public ICommand UpdateContactNumberCommand { get; }
    public ICommand LogoutCommand { get; }

    public ProfileViewModel()
    {
        LoadProfileCommand = new Command(async () => await LoadProfileAsync());
        ChangePasswordCommand = new Command(async () => await Shell.Current.GoToAsync("driver-change-password"));
        UpdateContactNumberCommand = new Command(async () => await Shell.Current.GoToAsync("driver-update-contact-number"));

        LogoutCommand = new Command(async () =>
        {
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

            var profileDoc = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(currentUser.Uid)
                .GetDocumentSnapshotAsync<DriverProfileProxy>();

            if (profileDoc?.Data == null) return;

            var profile = profileDoc.Data;
            FullName = string.IsNullOrWhiteSpace(profile.FullName) ? "Driver" : profile.FullName;
            ContactNumber = string.IsNullOrWhiteSpace(profile.PhoneNumber) ? "N/A" : profile.PhoneNumber;
            ProfileImageUrl = profile.ProfileImageUrl ?? string.Empty;

            LicenseNumber = string.IsNullOrWhiteSpace(profile.LicenseNumber) ? "N/A" : profile.LicenseNumber;
            DlCodes = string.IsNullOrWhiteSpace(profile.LicenseClassification) ? "N/A" : profile.LicenseClassification;
            ExpiryDateDisplay = profile.LicenseExpiryDate != null
                ? FirestoreDateTimeFix.Apply(profile.LicenseExpiryDate.Value.UtcDateTime).ToLocalTime().ToString("MMM dd, yyyy").ToUpperInvariant()
                : "N/A";

            var (licenseText, licenseColor) = LicenseStatusHelper.Describe(profile.LicenseExpiryDate);
            StatusText = licenseText switch
            {
                "active" => "VALID",
                "expiring soon" => "EXPIRING SOON",
                "expired" => "EXPIRED",
                _ => "NONE"
            };
            StatusColor = licenseColor;

            PaymentReliability = "N/A";
            ShiftPunctualityDisplay = "0%";
            DamageHistoryDisplay = "0 Incidents";

            await LoadPerformanceAsync(currentUser.Uid);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Driver Profile Load Error: {ex.Message}");
        }
    }

    private async Task LoadPerformanceAsync(string uid)
    {
        try
        {
            var userPerformanceDoc = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(uid)
                .GetDocumentSnapshotAsync<UserPerformanceProxy>();

            if (userPerformanceDoc?.Data != null)
            {
                PaymentReliability = ResolvePaymentReliability(userPerformanceDoc.Data);
                ShiftPunctualityDisplay = ResolveShiftPunctuality(userPerformanceDoc.Data);
                DamageHistoryDisplay = ResolveDamageHistory(userPerformanceDoc.Data.DamageHistory);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Driver Profile User Performance Load Error: {ex.Message}");
        }

        try
        {
            var performanceDoc = await CrossFirebaseFirestore.Current
                .GetCollection("driverPerformance")
                .GetDocument(uid)
                .GetDocumentSnapshotAsync<DriverPerformanceProxy>();

            if (performanceDoc?.Data == null) return;

            if (!string.IsNullOrWhiteSpace(performanceDoc.Data.PaymentReliability))
            {
                PaymentReliability = performanceDoc.Data.PaymentReliability.ToUpperInvariant();
            }

            if (performanceDoc.Data.ShiftPunctuality != null)
            {
                var punctuality = performanceDoc.Data.ShiftPunctuality.Value;
                if (punctuality <= 1) punctuality *= 100;
                ShiftPunctualityDisplay = $"{Math.Clamp(Math.Round(punctuality), 0, 100)}%";
            }

            if (performanceDoc.Data.DamageHistory != null)
            {
                DamageHistoryDisplay = ResolveDamageHistory(performanceDoc.Data.DamageHistory);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Driver Profile Performance Collection Load Error: {ex.Message}");
        }
    }

    private static string ResolvePaymentReliability(UserPerformanceProxy profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.PaymentReliability)) return profile.PaymentReliability.ToUpperInvariant();

        var arrears = profile.CurrentArrears ?? 0;
        if (arrears <= 0) return "EXCELLENT";
        if (arrears <= 100) return "GOOD";
        if (arrears <= 300) return "FAIR";

        return "POOR";
    }

    private static string ResolveShiftPunctuality(UserPerformanceProxy profile)
    {
        var punctuality = profile.ShiftPunctuality
                         ?? profile.ShiftPunctualityRate;

        if (punctuality == null) return "0%";

        var value = punctuality.Value;
        if (value <= 1) value *= 100;

        return $"{Math.Clamp(Math.Round(value), 0, 100)}%";
    }

    private static string ResolveDamageHistory(long? damageHistory)
    {
        var incidents = damageHistory ?? 0;

        return incidents == 1 ? "1 Incident" : $"{incidents} Incidents";
    }

    private class DriverProfileProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("fullName")]
        public string FullName { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("phoneNumber")]
        public string PhoneNumber { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("profileImageUrl")]
        public string? ProfileImageUrl { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("licenseNumber")]
        public string LicenseNumber { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("licenseClassification")]
        public string LicenseClassification { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("licenseExpiryDate")]
        public DateTimeOffset? LicenseExpiryDate { get; set; }
    }

    private class UserPerformanceProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("currentArrears")]
        public double? CurrentArrears { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("paymentReliability")]
        public string PaymentReliability { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("shiftPunctuality")]
        public double? ShiftPunctuality { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("shiftPunctualityRate")]
        public double? ShiftPunctualityRate { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("damageHistory")]
        public long? DamageHistory { get; set; }
    }

    private class DriverPerformanceProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("paymentReliability")]
        public string PaymentReliability { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("shiftPunctuality")]
        public double? ShiftPunctuality { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("damageHistory")]
        public long? DamageHistory { get; set; }
    }
}

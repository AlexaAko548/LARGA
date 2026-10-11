using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.MobileApp.Services;
using LARGA.SharedCore.Services;
using Microsoft.Maui.ApplicationModel;
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

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); }
    }

    public ICommand LoadProfileCommand { get; }
    public ICommand ChangePasswordCommand { get; }
    public ICommand UpdateContactNumberCommand { get; }
    public ICommand LogoutCommand { get; }

    public ProfileViewModel(IGpsTelemetryService telemetryService, IEmergencyMonitor emergencyMonitor)
    {
        LoadProfileCommand = new Command(async () => await LoadProfileAsync());
        ChangePasswordCommand = new Command(async () => await Shell.Current.GoToAsync("driver-change-password"));
        UpdateContactNumberCommand = new Command(async () => await Shell.Current.GoToAsync("driver-update-contact-number"));

        LogoutCommand = new Command(async () =>
        {
            // LAR-96: a driver mid-shift must clock out first - logging out would kill GPS
            // telemetry and emergency detection (both stopped below) while the shift Firestore
            // still shows them Active, leaving a manager blind to a driver who's actually still
            // on the road. Same local flag DriverDashboardPage already treats as the source of
            // truth for "is a shift currently open" (set at clock-in, cleared at clock-out).
            if (Preferences.Get("IsShiftActive", false))
            {
                await Shell.Current.DisplayAlert(
                    "Still on Shift",
                    "You can't log out while a shift is active. Please clock out first.",
                    "OK");
                return;
            }

            // No GPS points after logout - they'd be written for a driver who isn't signed in.
            telemetryService.Stop();
            // LAR-86/87: likewise stop emergency detection when the driver signs out.
            emergencyMonitor.Stop();
            await CrossFirebaseAuth.Current.SignOutAsync();
            await Shell.Current.GoToAsync("//landing");
        });

        // Automatically load profile on instantiation
        Task.Run(async () => await LoadProfileAsync());
    }

    public async Task LoadProfileAsync()
    {
        if (IsBusy) return;

        try
        {
            SetPropertyOnMainThread(() => IsBusy = true);

            // Give Firebase Auth up to 1.5 seconds to restore cached credentials if null
            var currentUser = CrossFirebaseAuth.Current.CurrentUser;
            int retries = 0;
            while (currentUser == null && retries < 3)
            {
                await Task.Delay(500);
                currentUser = CrossFirebaseAuth.Current.CurrentUser;
                retries++;
            }

            if (currentUser == null)
            {
                Debug.WriteLine("[ProfileViewModel] CurrentUser is NULL. User not authenticated.");
                SetPropertyOnMainThread(() => FullName = "Driver");
                return;
            }

            // 1. Fetch User / Driver Profile Record
            var profileDoc = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(currentUser.Uid)
                .GetDocumentSnapshotAsync<DriverProfileProxy>();

            if (profileDoc?.Data != null)
            {
                var profile = profileDoc.Data;

                var (licenseText, licenseColor) = LicenseStatusHelper.Describe(profile.LicenseExpiryDate);
                var formattedExpiry = profile.LicenseExpiryDate != null
                    ? FirestoreDateTimeFix.Apply(profile.LicenseExpiryDate.Value.UtcDateTime).ToLocalTime().ToString("MMM dd, yyyy").ToUpperInvariant()
                    : "N/A";

                var statusDisplay = licenseText switch
                {
                    "active" => "VALID",
                    "expiring soon" => "EXPIRING SOON",
                    "expired" => "EXPIRED",
                    _ => "NONE"
                };

                SetPropertyOnMainThread(() =>
                {
                    FullName = string.IsNullOrWhiteSpace(profile.FullName) ? "Driver" : profile.FullName;
                    ContactNumber = string.IsNullOrWhiteSpace(profile.PhoneNumber) ? "N/A" : profile.PhoneNumber;
                    ProfileImageUrl = profile.ProfileImageUrl ?? string.Empty;
                    LicenseNumber = string.IsNullOrWhiteSpace(profile.LicenseNumber) ? "N/A" : profile.LicenseNumber;
                    DlCodes = string.IsNullOrWhiteSpace(profile.LicenseClassification) ? "N/A" : profile.LicenseClassification;
                    ExpiryDateDisplay = formattedExpiry;
                    StatusText = statusDisplay;
                    StatusColor = licenseColor;
                });
            }
            else
            {
                Debug.WriteLine($"[ProfileViewModel] Document data not found for users/{currentUser.Uid}");
                SetPropertyOnMainThread(() => FullName = "Driver");
            }

            // 2. Fetch Performance Record
            await LoadPerformanceAsync(currentUser.Uid);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfileViewModel] Driver Profile Load Error: {ex.Message}");
        }
        finally
        {
            SetPropertyOnMainThread(() => IsBusy = false);
        }
    }

    private async Task LoadPerformanceAsync(string uid)
    {
        // Try user-level performance metrics first
        try
        {
            var userPerformanceDoc = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(uid)
                .GetDocumentSnapshotAsync<UserPerformanceProxy>();

            if (userPerformanceDoc?.Data != null)
            {
                var data = userPerformanceDoc.Data;

                // The live balance (same calculator as the Ledger screen), not users.currentArrears -
                // nothing keeps that field up to date.
                decimal? currentDebt = null;
                try
                {
                    currentDebt = (await DriverDebtCalculator.LoadAsync(uid)).TotalDebt;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProfileViewModel] Debt Load Error: {ex.Message}");
                }

                var reliability = ResolvePaymentReliability(data, currentDebt);
                var punctuality = ResolveShiftPunctuality(data);
                var damage = ResolveDamageHistory(data.DamageHistory);

                SetPropertyOnMainThread(() =>
                {
                    if (reliability is not null) PaymentReliability = reliability;
                    ShiftPunctualityDisplay = punctuality;
                    DamageHistoryDisplay = damage;
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfileViewModel] User Performance Load Error: {ex.Message}");
        }

        // Try standalone driverPerformance collection metrics
        try
        {
            var performanceDoc = await CrossFirebaseFirestore.Current
                .GetCollection("driverPerformance")
                .GetDocument(uid)
                .GetDocumentSnapshotAsync<DriverPerformanceProxy>();

            if (performanceDoc?.Data != null)
            {
                var data = performanceDoc.Data;

                SetPropertyOnMainThread(() =>
                {
                    if (!string.IsNullOrWhiteSpace(data.PaymentReliability))
                    {
                        PaymentReliability = data.PaymentReliability.ToUpperInvariant();
                    }

                    if (data.ShiftPunctuality != null)
                    {
                        var punctuality = data.ShiftPunctuality.Value;
                        if (punctuality <= 1) punctuality *= 100;
                        ShiftPunctualityDisplay = $"{Math.Clamp(Math.Round(punctuality), 0, 100)}%";
                    }

                    if (data.DamageHistory != null)
                    {
                        DamageHistoryDisplay = ResolveDamageHistory(data.DamageHistory);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfileViewModel] DriverPerformance Collection Load Error: {ex.Message}");
        }
    }

    private void SetPropertyOnMainThread(Action action)
    {
        if (MainThread.IsMainThread)
        {
            action();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(action);
        }
    }

    /// <summary>A rating set on the profile wins; otherwise it's graded from the driver's current
    /// debt. Null (leave the display as it is) when neither is available.</summary>
    private static string? ResolvePaymentReliability(UserPerformanceProxy profile, decimal? currentDebt)
    {
        if (!string.IsNullOrWhiteSpace(profile.PaymentReliability))
            return profile.PaymentReliability.ToUpperInvariant();

        if (currentDebt is not decimal debt) return null;
        if (debt <= 0) return "EXCELLENT";
        if (debt <= 100) return "GOOD";
        if (debt <= 300) return "FAIR";

        return "POOR";
    }

    private static string ResolveShiftPunctuality(UserPerformanceProxy profile)
    {
        var punctuality = profile.ShiftPunctuality ?? profile.ShiftPunctualityRate;
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

    public class DriverProfileProxy
    {
        [FirestoreProperty("fullName")]
        public string FullName { get; set; } = string.Empty;

        [FirestoreProperty("phoneNumber")]
        public string PhoneNumber { get; set; } = string.Empty;

        [FirestoreProperty("profileImageUrl")]
        public string? ProfileImageUrl { get; set; }

        [FirestoreProperty("licenseNumber")]
        public string LicenseNumber { get; set; } = string.Empty;

        [FirestoreProperty("licenseClassification")]
        public string LicenseClassification { get; set; } = string.Empty;

        [FirestoreProperty("licenseExpiryDate")]
        public DateTimeOffset? LicenseExpiryDate { get; set; }
    }

    public class UserPerformanceProxy
    {
        [FirestoreProperty("paymentReliability")]
        public string PaymentReliability { get; set; } = string.Empty;

        [FirestoreProperty("shiftPunctuality")]
        public double? ShiftPunctuality { get; set; }

        [FirestoreProperty("shiftPunctualityRate")]
        public double? ShiftPunctualityRate { get; set; }

        [FirestoreProperty("damageHistory")]
        public int? DamageHistory { get; set; }
    }

    public class DriverPerformanceProxy
    {
        [FirestoreProperty("paymentReliability")]
        public string PaymentReliability { get; set; } = string.Empty;

        [FirestoreProperty("shiftPunctuality")]
        public double? ShiftPunctuality { get; set; }

        [FirestoreProperty("damageHistory")]
        public int? DamageHistory { get; set; }
    }
}
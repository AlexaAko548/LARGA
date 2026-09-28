using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Services;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace LARGA.MobileApp.ViewModels.Driver;

public class DriverDashboardViewModel : INotifyPropertyChanged, IQueryAttributable
{
    private readonly INotificationService _notificationService;
    private readonly IShiftManagementService _shiftService;
    private bool _isOffline = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentDate => DateTime.Now.ToString("dddd, dd MMM yyyy");
    public string StatusText => IsOffline ? "OFFLINE" : "ONLINE";
    public Color StatusColor => IsOffline ? Colors.Red : Colors.Green;

    // Dynamically updates based on offline state and last login time
    public string GpsStatusText => IsOffline ? $"GPS inactive - Last login {LastLoginTime}" : "GPS active - Tracking On";
    public bool IsOnline => !IsOffline;

    private string _lastLoginTime = "--:--";
    public string LastLoginTime
    {
        get => _lastLoginTime;
        set
        {
            _lastLoginTime = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GpsStatusText)); // Forces the UI label to refresh
        }
    }

    private string _welcomeMessage = "Welcome back, Driver.";
    public string WelcomeMessage { get => _welcomeMessage; set { _welcomeMessage = value; OnPropertyChanged(); } }

    private string _assignedUnitPlate = "Loading...";
    public string AssignedUnitPlate { get => _assignedUnitPlate; set { _assignedUnitPlate = value; OnPropertyChanged(); } }

    private string _assignedUnitDetails = "--";
    public string AssignedUnitDetails { get => _assignedUnitDetails; set { _assignedUnitDetails = value; OnPropertyChanged(); } }

    private string _maintenanceStatus = "Checking...";
    public string MaintenanceStatus { get => _maintenanceStatus; set { _maintenanceStatus = value; OnPropertyChanged(); } }

    private string _managerNote = "No notes yet.";
    public string ManagerNote { get => _managerNote; set { _managerNote = value; OnPropertyChanged(); } }

    // License-expiry alert: mirrors DriverManagementService.ComputeLicenseStatus's thresholds
    // (Expired = past due, Expiring = within 3 calendar months) so the driver sees the same
    // warning window a manager sees on the Driver & Shift Management roster - computed here
    // from the driver's own profile rather than stored anywhere.
    private string _licenseAlertMessage = string.Empty;
    public string LicenseAlertMessage { get => _licenseAlertMessage; set { _licenseAlertMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLicenseAlert)); } }

    private bool _licenseExpired;
    public bool LicenseExpired { get => _licenseExpired; set { _licenseExpired = value; OnPropertyChanged(); OnPropertyChanged(nameof(LicenseAlertColor)); } }

    public bool HasLicenseAlert => !string.IsNullOrEmpty(LicenseAlertMessage);
    public Color LicenseAlertColor => LicenseExpired ? Colors.Crimson : Colors.DarkOrange;

    // "Day X of Y" for a unit currently under maintenance - mirrors GarageService/
    // WorkOrderEntry.DayNumber's DateLogged-based computation, plus the estimated total span
    // from EstimatedCompletionDate when the manager set one.
    private string _maintenanceDayLabel = string.Empty;
    public string MaintenanceDayLabel { get => _maintenanceDayLabel; set { _maintenanceDayLabel = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMaintenanceDayLabel)); } }
    public bool HasMaintenanceDayLabel => !string.IsNullOrEmpty(MaintenanceDayLabel);

    public bool IsOffline
    {
        get => _isOffline;
        set
        {
            if (_isOffline == value) return;
            _isOffline = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOnline));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(GpsStatusText));
        }
    }

    public ICommand ToggleShiftCommand { get; }
    public ICommand ActiveShiftCommand { get; }
    public ICommand MessageManagerCommand { get; }

    public DriverDashboardViewModel(INotificationService notificationService, IShiftManagementService shiftService)
    {
        _notificationService = notificationService;
        _shiftService = shiftService;

        ToggleShiftCommand = new Command(async () => await Shell.Current.GoToAsync("pre-shift-step1"));
        ActiveShiftCommand = new Command(async () => await Shell.Current.GoToAsync("active-shift"));
        MessageManagerCommand = new Command(async () => await Shell.Current.GoToAsync("message-manager"));

        _ = InitializeDashboardDataAsync();
    }

    private async Task InitializeDashboardDataAsync()
    {
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user != null)
        {
            var firstName = string.IsNullOrWhiteSpace(user.DisplayName) ? "Driver" : user.DisplayName.Split(' ')[0];
            WelcomeMessage = $"Welcome back,\n{firstName}.";
            _ = _notificationService.RegisterPushNotificationsAsync(user.Uid);

            // FIX: Restored the missing Last Login Time assignment
            LastLoginTime = DateTime.Now.ToString("HH:mm");

            try
            {
                var userProfileDoc = await CrossFirebaseFirestore.Current
                    .GetCollection("users")
                    .GetDocument(user.Uid)
                    .GetDocumentSnapshotAsync<UserProfileProxy>(); // FIX: Use the mobile proxy

                DateTimeOffset? licenseExpiryOffset = userProfileDoc?.Data?.LicenseExpiryDate;
                EvaluateLicenseAlert(licenseExpiryOffset?.UtcDateTime);

                var dynamicTaxiId = userProfileDoc?.Data?.AssignedTaxiId;

                if (!string.IsNullOrWhiteSpace(dynamicTaxiId))
                {
                    var taxi = await _shiftService.GetTaxiUnitAsync(dynamicTaxiId);
                    if (taxi != null)
                    {
                        AssignedUnitPlate = string.IsNullOrWhiteSpace(taxi.PlateNumber) ? taxi.Model : taxi.PlateNumber.Replace("-", " • ");
                        AssignedUnitDetails = $"{taxi.YearManufactured} {taxi.Model}";
                        MaintenanceStatus = taxi.Status;

                        if (string.Equals(taxi.Status, "Under Maintenance", StringComparison.OrdinalIgnoreCase))
                        {
                            await LoadMaintenanceDayLabelAsync(dynamicTaxiId);
                        }
                        else
                        {
                            MaintenanceDayLabel = string.Empty;
                        }
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Dashboard Data Error: {ex.Message}"); }
        }
    }

    private void EvaluateLicenseAlert(DateTime? licenseExpiry)
    {
        if (!licenseExpiry.HasValue)
        {
            LicenseAlertMessage = string.Empty;
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (licenseExpiry.Value < now)
        {
            LicenseExpired = true;
            LicenseAlertMessage = $"Your driver's license expired on {licenseExpiry.Value:MMM d, yyyy}. Please renew immediately - you may be taken off shift eligibility until it's updated.";
        }
        else if (licenseExpiry.Value <= now.AddMonths(3))
        {
            LicenseExpired = false;
            LicenseAlertMessage = $"Your driver's license expires on {licenseExpiry.Value:MMM d, yyyy}. Please renew soon to avoid losing shift eligibility.";
        }
        else
        {
            LicenseAlertMessage = string.Empty;
        }
    }

    /// <summary>Single equality filter on taxiId (auto-indexed) - same index-avoidance
    /// pattern used elsewhere in this codebase - with the "InProgress" filter and picking
    /// the most recent record done client-side.</summary>
    private async Task LoadMaintenanceDayLabelAsync(string taxiId)
    {
        try
        {
            var snapshot = await CrossFirebaseFirestore.Current
                .GetCollection("maintenance_logs")
                .WhereEqualsTo("taxiId", taxiId)
                .GetDocumentsAsync<MaintenanceRecordProxy>();

            MaintenanceRecordProxy active = snapshot.Documents
                .Select(d => d.Data)
                .Where(m => m != null && string.Equals(m!.Status, "InProgress", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m!.DateLogged)
                .FirstOrDefault();

            if (active is null)
            {
                MaintenanceDayLabel = string.Empty;
                return;
            }

            int currentDay = Math.Max(1, (int)(DateTime.UtcNow.Date - active.DateLogged.UtcDateTime.Date).TotalDays + 1);

            if (active.EstimatedCompletionDate.HasValue)
            {
                int totalDays = Math.Max(currentDay, (int)(active.EstimatedCompletionDate.Value.UtcDateTime.Date - active.DateLogged.UtcDateTime.Date).TotalDays + 1);
                MaintenanceDayLabel = $"Day {Math.Min(currentDay, totalDays)} of {totalDays}";
            }
            else
            {
                MaintenanceDayLabel = $"Day {currentDay}";
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Maintenance Day Lookup Error: {ex.Message}");
        }
    }

    // Add this proxy class to the bottom of the file
    public class UserProfileProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("assignedTaxiId")]
        public string AssignedTaxiId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("licenseExpiryDate")]
        public DateTimeOffset? LicenseExpiryDate { get; set; } // Fixed
    }

    // Mobile-specific proxy (Plugin.Firebase attributes) for reading a taxi's active
    // maintenance record - same convention as SystemAlertProxy in AlertCenterViewModel.
    public class MaintenanceRecordProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("status")]
        public string Status { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("dateLogged")]
        public DateTimeOffset DateLogged { get; set; } // Fixed

        [Plugin.Firebase.Firestore.FirestoreProperty("estimatedCompletionDate")]
        public DateTimeOffset? EstimatedCompletionDate { get; set; } // Fixed
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.ContainsKey("IsOnline") && query["IsOnline"].ToString() == "true")
        {
            IsOffline = false;
        }
    }

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

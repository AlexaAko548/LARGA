using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Services;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
using System;
using System.Collections.Generic;
using System.ComponentModel;
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

                var dynamicTaxiId = userProfileDoc?.Data?.AssignedTaxiId;

                if (!string.IsNullOrWhiteSpace(dynamicTaxiId))
                {
                    var taxi = await _shiftService.GetTaxiUnitAsync(dynamicTaxiId);
                    if (taxi != null)
                    {
                        AssignedUnitPlate = string.IsNullOrWhiteSpace(taxi.PlateNumber) ? taxi.Model : taxi.PlateNumber.Replace("-", " · ");
                        AssignedUnitDetails = $"{taxi.YearManufactured} {taxi.Model}";
                        MaintenanceStatus = taxi.Status;
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Dashboard Data Error: {ex.Message}"); }
        }
    }

    // Add this proxy class to the bottom of the file
    public class UserProfileProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("assignedTaxiId")]
        public string AssignedTaxiId { get; set; }
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

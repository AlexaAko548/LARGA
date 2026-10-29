using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
using LARGA.MobileApp.Services;

namespace LARGA.MobileApp.ViewModels.Manager;

public class ManagerProfileViewModel : BindableObject
{
    private string _fullName = "Loading...";
    public string FullName
    {
        get => _fullName;
        set { _fullName = value; OnPropertyChanged(); }
    }

    private string _roleDisplay = StaffSession.IsAssistantManager ? "Assistant Manager" : "Fleet Manager";

    /// <summary>The contact number feeds the SOS auto-answer allowlist (system_configs), which only a
    /// Manager may change from the app - an Assistant Manager's number is kept in step by ManagerWeb.</summary>
    public bool CanUpdateContactNumber => !StaffSession.IsAssistantManager;
    public string RoleDisplay
    {
        get => _roleDisplay;
        set { _roleDisplay = value; OnPropertyChanged(); }
    }

    private string _driverCountDisplay = "-- drivers";
    public string DriverCountDisplay
    {
        get => _driverCountDisplay;
        set { _driverCountDisplay = value; OnPropertyChanged(); }
    }

    private string _fleetCountDisplay = "-- units assigned";
    public string FleetCountDisplay
    {
        get => _fleetCountDisplay;
        set { _fleetCountDisplay = value; OnPropertyChanged(); }
    }

    public ICommand LoadProfileCommand { get; }
    public ICommand ViewDriverManagementCommand { get; }
    public ICommand ViewFleetRegistryCommand { get; }
    public ICommand ChangePasswordCommand { get; }
    public ICommand UpdateContactNumberCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand RefreshCommand { get; }

    private bool _isRefreshing;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set { _isRefreshing = value; OnPropertyChanged(); }
    }

    public ManagerProfileViewModel()
    {
        LoadProfileCommand = new Command(async () => await LoadProfileAsync());
        RefreshCommand = new Command(async () => await RefreshAsync());
        ViewDriverManagementCommand = new Command(async () => await LARGA.MobileApp.Services.NavigationGate.GoToAsync("driver-management"));
        ViewFleetRegistryCommand = new Command(async () => await LARGA.MobileApp.Services.NavigationGate.GoToAsync("fleet-registry"));
        ChangePasswordCommand = new Command(async () => await LARGA.MobileApp.Services.NavigationGate.GoToAsync("change-password"));
        UpdateContactNumberCommand = new Command(async () => await LARGA.MobileApp.Services.NavigationGate.GoToAsync("update-contact-number"));
        LogoutCommand = new Command(async () =>
        {
            if (!await LARGA.MobileApp.Controls.ConfirmationPopup.ShowAsync(Shell.Current,
                    "Log Out?", "You will stop seeing live alerts on this phone until you log in again.",
                    confirmText: "Log Out", isDangerous: false))
            {
                return;
            }

            // Signs out and leaves nothing of this manager on the phone (ledgers, driver details in
            // the Firestore cache) - see SignOutService.
            await LARGA.MobileApp.Services.SignOutService.SignOutAsync();
            await Shell.Current.GoToAsync("//landing");
        });
    }

    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            await LoadProfileAsync();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task LoadProfileAsync()
    {
        using var _loading = LARGA.MobileApp.Services.LoadingState.Begin(); // LAR-117: app-wide loading indicator
        try
        {
            var currentUser = CrossFirebaseAuth.Current.CurrentUser;
            if (currentUser == null) return;

            var profileDoc = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(currentUser.Uid)
                .GetDocumentSnapshotAsync<ManagerProfileProxy>();

            if (profileDoc?.Data != null && !string.IsNullOrWhiteSpace(profileDoc.Data.FullName))
            {
                FullName = profileDoc.Data.FullName;
            }

            var driverSnapshot = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .WhereEqualsTo("role", "Driver")
                .GetDocumentsAsync<DriverCountProxy>();
            var driverCount = driverSnapshot.Documents.Count();
            DriverCountDisplay = $"{driverCount} driver{(driverCount == 1 ? "" : "s")}";

            var taxiSnapshot = await CrossFirebaseFirestore.Current
                .GetCollection("taxis")
                .GetDocumentsAsync<TaxiCountProxy>();
            var taxiCount = taxiSnapshot.Documents.Count();
            FleetCountDisplay = $"{taxiCount} unit{(taxiCount == 1 ? "" : "s")} assigned";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Manager Profile Load Error: {ex.Message}");
        }
    }

    private class ManagerProfileProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("fullName")]
        public string FullName { get; set; } = string.Empty;
    }

    // Only need document *count* for these two - the plugin still requires a mappable type.
    private class DriverCountProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("fullName")]
        public string FullName { get; set; } = string.Empty;
    }

    private class TaxiCountProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("plateNumber")]
        public string PlateNumber { get; set; } = string.Empty;
    }
}

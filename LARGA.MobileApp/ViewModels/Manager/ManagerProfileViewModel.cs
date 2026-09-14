using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.MobileApp.Services;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.ViewModels.Manager;

public class ManagerProfileViewModel : BindableObject
{
    private string _fullName = "Loading...";
    public string FullName
    {
        get => _fullName;
        set { _fullName = value; OnPropertyChanged(); OnPropertyChanged(nameof(Initials)); }
    }

    public string Initials => NameHelper.Initials(FullName);

    private string _roleDisplay = "Fleet Manager";
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

    public ManagerProfileViewModel()
    {
        LoadProfileCommand = new Command(async () => await LoadProfileAsync());
        ViewDriverManagementCommand = new Command(async () => await Shell.Current.GoToAsync("driver-management"));
        ViewFleetRegistryCommand = new Command(async () => await Shell.Current.GoToAsync("fleet-registry"));
        ChangePasswordCommand = new Command(async () => await Shell.Current.GoToAsync("change-password"));
        UpdateContactNumberCommand = new Command(async () => await Shell.Current.GoToAsync("update-contact-number"));
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

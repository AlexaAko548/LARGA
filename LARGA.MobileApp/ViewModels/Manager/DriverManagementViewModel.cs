using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.MobileApp.Services;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.ViewModels.Manager;

public class DriverManagementViewModel : BindableObject
{
    public ObservableCollection<DriverListItem> Drivers { get; } = new();

    public ICommand LoadDriversCommand { get; }
    public ICommand ViewDriverCommand { get; }
    public ICommand GoBackCommand { get; }

    public DriverManagementViewModel()
    {
        LoadDriversCommand = new Command(async () => await LoadDriversAsync());
        ViewDriverCommand = new Command<DriverListItem>(async (item) =>
        {
            if (item == null || string.IsNullOrEmpty(item.Id)) return;
            await Shell.Current.GoToAsync($"manager-driver-profile?id={item.Id}");
        });
        GoBackCommand = new Command(async () => await Shell.Current.GoToAsync(".."));
    }

    private async Task LoadDriversAsync()
    {
        try
        {
            var snapshot = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .WhereEqualsTo("role", "Driver")
                .GetDocumentsAsync<DriverProxy>();

            var items = snapshot.Documents
                .Where(d => d.Data != null)
                .Select(d =>
                {
                    var (statusText, statusColor) = LicenseStatusHelper.Describe(d.Data.LicenseExpiryDate);
                    return new DriverListItem
                    {
                        Id = d.Reference.Id,
                        FullName = string.IsNullOrWhiteSpace(d.Data.FullName) ? "(Unnamed driver)" : d.Data.FullName,
                        StatusText = statusText,
                        StatusColor = statusColor
                    };
                })
                .OrderBy(i => i.FullName)
                .ToList();

            Drivers.Clear();
            foreach (var item in items)
            {
                Drivers.Add(item);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Driver Management Load Error: {ex.Message}");
        }
    }

    private class DriverProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("fullName")]
        public string FullName { get; set; } = string.Empty;

        // DateTimeOffset?, not DateTime? - see LicenseStatusHelper.Describe for why.
        [Plugin.Firebase.Firestore.FirestoreProperty("licenseExpiryDate")]
        public DateTimeOffset? LicenseExpiryDate { get; set; }
    }
}

public class DriverListItem
{
    public string Id { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public Color StatusColor { get; set; } = Colors.Gray;
}

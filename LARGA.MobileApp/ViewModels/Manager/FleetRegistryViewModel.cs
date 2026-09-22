using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.ViewModels.Manager;

public class FleetRegistryViewModel : BindableObject
{
    public ObservableCollection<TaxiListItem> Taxis { get; } = new();

    public ICommand LoadTaxisCommand { get; }
    public ICommand GoBackCommand { get; }

    public FleetRegistryViewModel()
    {
        LoadTaxisCommand = new Command(async () => await LoadTaxisAsync());
        GoBackCommand = new Command(async () => await Shell.Current.GoToAsync(".."));
    }

    private async Task LoadTaxisAsync()
    {
        try
        {
            var snapshot = await CrossFirebaseFirestore.Current
                .GetCollection("taxis")
                .GetDocumentsAsync<TaxiProxy>();

            var items = snapshot.Documents
                .Where(d => d.Data != null)
                .Select(d => new TaxiListItem
                {
                    PlateNumber = string.IsNullOrWhiteSpace(d.Data.PlateNumber) ? "(No plate set)" : d.Data.PlateNumber,
                    StatusText = string.IsNullOrWhiteSpace(d.Data.Status) ? "unknown" : d.Data.Status.ToLowerInvariant(),
                    StatusColor = ColorForStatus(d.Data.Status)
                })
                .OrderBy(i => i.PlateNumber)
                .ToList();

            Taxis.Clear();
            foreach (var item in items)
            {
                Taxis.Add(item);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Fleet Registry Load Error: {ex.Message}");
        }
    }

    private static Color ColorForStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return Colors.Gray;
        var s = status.ToLowerInvariant();
        if (s.Contains("shop") || s.Contains("maintenance") || s.Contains("repair")) return Color.FromArgb("#F57C00");
        if (s.Contains("assigned") || s.Contains("available") || s.Contains("active")) return Color.FromArgb("#2E7D32");
        return Colors.Gray;
    }

    private class TaxiProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("plateNumber")]
        public string PlateNumber { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("status")]
        public string Status { get; set; } = string.Empty;
    }
}

public class TaxiListItem
{
    public string PlateNumber { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public Color StatusColor { get; set; } = Colors.Gray;
}

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
                .Select(d =>
                {
                    var (color, bgColor) = ColorsForStatus(d.Data.Status);
                    return new TaxiListItem
                    {
                        PlateNumber = string.IsNullOrWhiteSpace(d.Data.PlateNumber) ? "(No plate set)" : d.Data.PlateNumber,
                        StatusText = string.IsNullOrWhiteSpace(d.Data.Status) ? "unknown" : d.Data.Status.ToLowerInvariant(),
                        StatusColor = color,
                        StatusBgColor = bgColor
                    };
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

    private static (Color Color, Color BgColor) ColorsForStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return (Color.FromArgb("#6B808A"), Color.FromArgb("#EEF2F4"));
        var s = status.ToLowerInvariant();
        if (s.Contains("shop") || s.Contains("maintenance") || s.Contains("repair"))
            return (Color.FromArgb("#C97A1B"), Color.FromArgb("#FBF0E0"));
        if (s.Contains("assigned") || s.Contains("available") || s.Contains("active"))
            return (Color.FromArgb("#1E8E5A"), Color.FromArgb("#E3F5EC"));
        return (Color.FromArgb("#6B808A"), Color.FromArgb("#EEF2F4"));
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
    public Color StatusBgColor { get; set; } = Colors.WhiteSmoke;
}

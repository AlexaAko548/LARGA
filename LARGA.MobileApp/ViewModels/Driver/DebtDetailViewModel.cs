using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace LARGA.MobileApp.ViewModels.Driver;

public class DebtDetailViewModel : BindableObject
{
    public ObservableCollection<DebtRecord> DebtHistory { get; set; } = new();

    public DebtDetailViewModel()
    {
        _ = LoadDynamicDebtDataAsync();
    }

    private async Task LoadDynamicDebtDataAsync()
    {
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user != null)
        {
            try
            {
                // 1. Live Background Fetch (Single query since debt_adjustments contains DriverId)
                var snapshot = await CrossFirebaseFirestore.Current.GetCollection("debt_adjustments").WhereEqualsTo("driverId", user.Uid).GetDocumentsAsync<Dictionary<string, object>>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Debt Fetch Error: {ex.Message}");
            }
        }

        // 2. DEMO OVERRIDE
        DebtHistory.Clear();
        DebtHistory.Add(new DebtRecord { DateStr = "7/22", Amount = "400.00" });
        DebtHistory.Add(new DebtRecord { DateStr = "7/20", Amount = "200.00" });
        DebtHistory.Add(new DebtRecord { DateStr = "7/18", Amount = "100.00" });

        OnPropertyChanged(nameof(DebtHistory));
    }
}

public class DebtRecord
{
    public string DateStr { get; set; }
    public string Amount { get; set; }
    public string DisplayText => $"{DateStr} - ₱ {Amount}";
}
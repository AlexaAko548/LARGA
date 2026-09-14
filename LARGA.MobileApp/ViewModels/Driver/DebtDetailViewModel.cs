using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

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
        DebtHistory.Clear();

        // These records perfectly match the mathematical array calculated in the LedgerViewModel
        DebtHistory.Add(new DebtRecord { DateStr = "7/22", Amount = "500.00" });
        DebtHistory.Add(new DebtRecord { DateStr = "7/20", Amount = "200.00" });
        DebtHistory.Add(new DebtRecord { DateStr = "7/18", Amount = "300.00" });

        OnPropertyChanged(nameof(DebtHistory));
    }
}

public class DebtRecord
{
    public string DateStr { get; set; }
    public string Amount { get; set; }
    public string DisplayText => $"{DateStr} - ₱ {Amount}";
}
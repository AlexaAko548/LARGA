using System.Collections.ObjectModel;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Driver;

public class DebtDetailViewModel : BindableObject
{
    public ObservableCollection<DebtRecord> DebtHistory { get; set; }

    public DebtDetailViewModel()
    {
        // Static testing data mapping to the Debt Balance Details mockup
        DebtHistory = new ObservableCollection<DebtRecord>
        {
            new DebtRecord { DateStr = "7/22", Amount = "500.00" },
            new DebtRecord { DateStr = "7/20", Amount = "200.00" },
            new DebtRecord { DateStr = "7/18", Amount = "300.00" }
        };
    }
}

public class DebtRecord
{
    public string DateStr { get; set; }
    public string Amount { get; set; }
    public string DisplayText => $"{DateStr} - ₱ {Amount}";
}
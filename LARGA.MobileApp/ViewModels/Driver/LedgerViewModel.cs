using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Driver;

public class LedgerViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentDate => DateTime.Now.ToString("dddd, dd MMM yyyy");
    public string CurrentShiftPayment { get; set; } = "UNPAID";
    public string OutstandingDebtBalance { get; set; } = "₱ 1,000.00";

    public ObservableCollection<PaymentRecord> PaymentHistory { get; set; }

    public ICommand ViewDebtDetailsCommand { get; }
    public ICommand ViewAllHistoryCommand { get; }

    public LedgerViewModel()
    {
        // Static testing data mapping directly to your provided UI mockup
        PaymentHistory = new ObservableCollection<PaymentRecord>
        {
            new PaymentRecord { DateStr = "7/22", Amount = "500.00", Status = "(Partial)" },
            new PaymentRecord { DateStr = "7/21", Amount = "1000.00", Status = "(Full)" },
            new PaymentRecord { DateStr = "7/20", Amount = "800.00", Status = "(Partial)" },
            new PaymentRecord { DateStr = "7/19", Amount = "1000.00", Status = "(Full)" },
            new PaymentRecord { DateStr = "7/18", Amount = "700.00", Status = "(Partial)" },
            new PaymentRecord { DateStr = "7/17", Amount = "1000.00", Status = "(Full)" },
            new PaymentRecord { DateStr = "7/16", Amount = "1000.00", Status = "(Full)" }
        };

        // Placeholder routing for the sub-screens
        ViewDebtDetailsCommand = new Command(async () => await App.Current.MainPage.DisplayAlert("Details", "Routing to Debt Details...", "OK"));
        ViewAllHistoryCommand = new Command(async () => await App.Current.MainPage.DisplayAlert("History", "Routing to Full History...", "OK"));
    }

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class PaymentRecord
{
    public string DateStr { get; set; }
    public string Amount { get; set; }
    public string Status { get; set; }
    public string DisplayText => $"{DateStr} - ₱ {Amount} {Status}".Trim();
}
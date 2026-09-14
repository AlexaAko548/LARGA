using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Driver;

public class LedgerViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentDate => DateTime.Now.ToString("dddd, dd MMM yyyy");

    private string _currentShiftPayment = "UNPAID";
    public string CurrentShiftPayment
    {
        get => _currentShiftPayment;
        set { _currentShiftPayment = value; OnPropertyChanged(); }
    }

    private string _outstandingDebtBalance = "₱ 0.00";
    public string OutstandingDebtBalance
    {
        get => _outstandingDebtBalance;
        set { _outstandingDebtBalance = value; OnPropertyChanged(); }
    }

    public ObservableCollection<PaymentRecord> PaymentHistory { get; set; } = new();

    public ICommand ViewDebtDetailsCommand { get; }
    public ICommand ViewAllHistoryCommand { get; }

    public LedgerViewModel()
    {
        ViewDebtDetailsCommand = new Command(async () => await Shell.Current.GoToAsync("debt-details"));
        ViewAllHistoryCommand = new Command(async () => await Shell.Current.GoToAsync("payment-history"));

        _ = LoadDynamicLedgerDataAsync();
    }

    private async Task LoadDynamicLedgerDataAsync()
    {
        // 1. Load Payment History
        PaymentHistory.Clear();
        PaymentHistory.Add(new PaymentRecord { DateStr = "7/22", Amount = "500.00", Status = "(Partial)" });
        PaymentHistory.Add(new PaymentRecord { DateStr = "7/21", Amount = "1000.00", Status = "(Full)" });
        PaymentHistory.Add(new PaymentRecord { DateStr = "7/20", Amount = "800.00", Status = "(Partial)" });
        PaymentHistory.Add(new PaymentRecord { DateStr = "7/19", Amount = "1000.00", Status = "(Full)" });
        PaymentHistory.Add(new PaymentRecord { DateStr = "7/18", Amount = "700.00", Status = "(Partial)" });
        PaymentHistory.Add(new PaymentRecord { DateStr = "7/17", Amount = "1000.00", Status = "(Full)" });
        PaymentHistory.Add(new PaymentRecord { DateStr = "7/16", Amount = "1000.00", Status = "(Full)" });

        // 2. Load Debt Items (This will eventually be a Firebase call shared with DebtDetailViewModel)
        var currentDebts = new[]
        {
            new { DateStr = "7/22", Amount = 500.00m },
            new { DateStr = "7/20", Amount = 200.00m },
            new { DateStr = "7/18", Amount = 300.00m }
        };

        // 3. Mathematically guarantee the total perfectly reflects the individual Debt Detail items
        decimal totalDebt = currentDebts.Sum(d => d.Amount);
        OutstandingDebtBalance = $"₱ {totalDebt:N2}";
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
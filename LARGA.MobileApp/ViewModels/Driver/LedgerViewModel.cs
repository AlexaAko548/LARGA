using Microsoft.Maui.Controls;
using Plugin.Firebase.Firestore;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Plugin.Firebase.Auth;

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
    var user = CrossFirebaseAuth.Current.CurrentUser;
    if (user != null)
    {
        try
        {
                // 1. Live Background Fetch (Two-Step Workaround for Payments)
                var shiftsSnapshot = await CrossFirebaseFirestore.Current.GetCollection("shifts").WhereEqualsTo("driverId", user.Uid).GetDocumentsAsync<Dictionary<string, object>>();

                foreach (var shift in shiftsSnapshot.Documents)
            {
                if (shift.Data != null && shift.Data.ContainsKey("shiftId"))
                {
                    string shiftId = shift.Data["shiftId"]?.ToString();
                    if (!string.IsNullOrEmpty(shiftId))
                    {
                            await CrossFirebaseFirestore.Current.GetCollection("boundary_payments").WhereEqualsTo("shiftId", shiftId).GetDocumentsAsync<Dictionary<string, object>>();
                        }
                }
            }

                // Debts already have a DriverId, so they only need a standard single query
                await CrossFirebaseFirestore.Current.GetCollection("debt_adjustments").WhereEqualsTo("driverId", user.Uid).GetDocumentsAsync<Dictionary<string, object>>();
            }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Ledger Firebase Error: {ex.Message}");
        }
    }

    // 2. DEMO OVERRIDE: Inject presentation data
    PaymentHistory.Clear();
    PaymentHistory.Add(new PaymentRecord { DateStr = "7/22", Amount = "400.00", Status = "(Partial)" });
    PaymentHistory.Add(new PaymentRecord { DateStr = "7/21", Amount = "800.00", Status = "(Full)" });
    PaymentHistory.Add(new PaymentRecord { DateStr = "7/20", Amount = "600.00", Status = "(Partial)" });
    PaymentHistory.Add(new PaymentRecord { DateStr = "7/19", Amount = "800.00", Status = "(Full)" });
    PaymentHistory.Add(new PaymentRecord { DateStr = "7/18", Amount = "700.00", Status = "(Partial)" });
    PaymentHistory.Add(new PaymentRecord { DateStr = "7/17", Amount = "800.00", Status = "(Full)" });
    PaymentHistory.Add(new PaymentRecord { DateStr = "7/16", Amount = "800.00", Status = "(Full)" });

    var currentDebts = new[]
    {
        new { DateStr = "7/22", Amount = 400.00m },
        new { DateStr = "7/20", Amount = 200.00m },
        new { DateStr = "7/18", Amount = 100.00m }
    };

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
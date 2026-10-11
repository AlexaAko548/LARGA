using Microsoft.Maui.Controls;
using Plugin.Firebase.Firestore;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Plugin.Firebase.Auth;
using LARGA.MobileApp.Services;

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
    }

    private bool _isLoading;

    /// <summary>
    /// Called by LedgerPage each time the tab appears: Shell keeps the tab page alive, so loading
    /// once in the constructor would never show payments recorded after the first visit.
    /// </summary>
    public async Task LoadDynamicLedgerDataAsync()
    {
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user == null || _isLoading) return;
        _isLoading = true;

        try
        {
            PaymentHistory.Clear();

            // Same per-shift calculation as the web's Master Debt Ledger, so the driver and the
            // manager see the same balance (see DriverDebtCalculator).
            var ledger = await DriverDebtCalculator.LoadAsync(user.Uid);
            OutstandingDebtBalance = $"₱ {ledger.TotalDebt:N2}";

            var payments = ledger.Shifts
                .Where(s => s.HasPayment)
                .Select(s => new PaymentRecord
                {
                    DateStr = s.PaymentTimestampUtc is DateTime paidAt ? paidAt.ToLocalTime().ToString("M/dd") : "N/A",
                    Amount = $"{s.Paid:N2}",
                    // Exact match: "Unpaid" also contains "Paid".
                    Status = s.PaymentStatus == "Paid" ? "(Full)" : s.PaymentStatus == "Partial" ? "(Partial)" : "UNPAID",
                    RawDate = s.PaymentTimestampUtc ?? DateTime.MinValue,
                })
                .OrderByDescending(p => p.RawDate);

            foreach (var payment in payments)
            {
                PaymentHistory.Add(payment);
            }

            // Today's shift: its payment status, or UNPAID when nothing has been paid on it yet.
            var latestShift = ledger.Shifts.OrderByDescending(s => s.ShiftStartUtc).FirstOrDefault();
            CurrentShiftPayment = latestShift is null || !latestShift.HasPayment
                ? "UNPAID"
                : latestShift.PaymentStatus == "Paid" ? "FULL" : latestShift.PaymentStatus == "Partial" ? "PARTIAL" : "UNPAID";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Ledger Firebase Error: {ex.Message}");
            OutstandingDebtBalance = "₱ 0.00";
            CurrentShiftPayment = "ERROR";
        }
        finally
        {
            _isLoading = false;
        }
    }

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

// Proxy Class
public class PaymentRecord
{
    public string DateStr { get; set; }
    public string Amount { get; set; }
    public string Status { get; set; }
    public DateTime RawDate { get; set; }
    public string DisplayText => $"{DateStr} - ₱ {Amount} {Status}".Trim();
}

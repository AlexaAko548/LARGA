using LARGA.MobileApp.Services;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace LARGA.MobileApp.ViewModels.Driver;

public class PaymentHistoryViewModel : BindableObject
{
    public ObservableCollection<PaymentRecord> FullPaymentHistory { get; set; } = new();
    public ICommand ExportCommand { get; }

    public PaymentHistoryViewModel()
    {
        ExportCommand = new Command(async () => await Application.Current.MainPage.DisplayAlert("Export", "Exporting ledger data...", "OK"));

        _ = LoadDynamicPaymentDataAsync();
    }

    private async Task LoadDynamicPaymentDataAsync()
    {
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user == null) return;

        try
        {
            FullPaymentHistory.Clear();

            // Same records as the Ledger screen (DriverDebtCalculator reads them through typed
            // proxies - Plugin.Firebase returns Dictionary<string, object> reads empty).
            var ledger = await DriverDebtCalculator.LoadAsync(user.Uid);
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
                FullPaymentHistory.Add(payment);
            }

            OnPropertyChanged(nameof(FullPaymentHistory));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Payment Fetch Error: {ex.Message}");
        }
    }
}

// Note: Ensure PaymentRecord class matches the one in LedgerViewModel.cs 
// If they are in the same namespace, you only need to define PaymentRecord once.
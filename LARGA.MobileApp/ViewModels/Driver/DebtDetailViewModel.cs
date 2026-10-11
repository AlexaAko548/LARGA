using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
using LARGA.MobileApp.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
        if (user == null) return;

        try
        {
            DebtHistory.Clear();

            // What makes up the balance on the Ledger screen: each shift that still owes
            // something, and each manual adjustment (see DriverDebtCalculator).
            var ledger = await DriverDebtCalculator.LoadAsync(user.Uid);

            var records = ledger.Shifts
                .Where(s => s.Outstanding > 0)
                .Select(s => new DebtRecord
                {
                    DateStr = s.ShiftStartUtc == DateTime.MinValue ? "N/A" : s.ShiftStartUtc.ToLocalTime().ToString("M/dd"),
                    Amount = $"{s.Outstanding:N2}",
                    Label = string.IsNullOrEmpty(s.TaxiId) ? "unpaid boundary" : $"unpaid boundary, {s.TaxiId}",
                    RawDate = s.ShiftStartUtc,
                })
                .Concat(ledger.Adjustments.Select(a => new DebtRecord
                {
                    DateStr = a.TimestampUtc == DateTime.MinValue ? "N/A" : a.TimestampUtc.ToLocalTime().ToString("M/dd"),
                    Amount = $"{a.Amount:N2}",
                    Label = a.Amount >= 0 ? "penalty" : "credit",
                    RawDate = a.TimestampUtc,
                }))
                .OrderByDescending(d => d.RawDate)
                .ToList();

            if (ledger.Credit > 0)
            {
                records.Insert(0, new DebtRecord
                {
                    DateStr = "Credit",
                    Amount = $"-{ledger.Credit:N2}",
                    Label = "paid above a shift's boundary, applied to the oldest",
                    RawDate = DateTime.MaxValue,
                });
            }

            foreach (var debt in records)
            {
                DebtHistory.Add(debt);
            }

            OnPropertyChanged(nameof(DebtHistory));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Debt Fetch Error: {ex.Message}");
        }
    }
}

public class DebtRecord
{
    public string DateStr { get; set; }
    public string Amount { get; set; }
    public string Label { get; set; } = string.Empty;
    public DateTime RawDate { get; set; } // Used for sorting, not displayed
    public string DisplayText => string.IsNullOrEmpty(Label) ? $"{DateStr} - ₱ {Amount}" : $"{DateStr} - ₱ {Amount} ({Label})";
}

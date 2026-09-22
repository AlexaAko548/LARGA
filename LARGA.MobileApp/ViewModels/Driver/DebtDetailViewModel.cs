using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
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
            var tempDebts = new List<DebtRecord>();

            // Querying using camelCase "driverId" as it is standard, 
            // but you may need to change to "DriverId" if the query returns empty.
            var snapshot = await CrossFirebaseFirestore.Current
                .GetCollection("debt_adjustments")
                .WhereEqualsTo("driverId", user.Uid)
                .GetDocumentsAsync<Dictionary<string, object>>();

            foreach (var doc in snapshot.Documents)
            {
                if (doc.Data != null)
                {
                    // Safely extract Timestamp
                    string dateStr = "N/A";
                    object timeObj = doc.Data.ContainsKey("Timestamp") ? doc.Data["Timestamp"] :
                                     doc.Data.ContainsKey("timestamp") ? doc.Data["timestamp"] : null;

                    if (timeObj is DateTime dt)
                    {
                        dateStr = dt.ToLocalTime().ToString("M/dd");
                    }

                    // Safely extract Amount
                    object amountObj = doc.Data.ContainsKey("Amount") ? doc.Data["Amount"] :
                                       doc.Data.ContainsKey("amount") ? doc.Data["amount"] : null;

                    decimal amount = amountObj != null ? Convert.ToDecimal(amountObj) : 0.00m;

                    tempDebts.Add(new DebtRecord
                    {
                        DateStr = dateStr,
                        Amount = $"{amount:N2}",
                        // Store actual DateTime for sorting purposes
                        RawDate = timeObj is DateTime rawDt ? rawDt : DateTime.MinValue
                    });
                }
            }

            // Sort newest debts first
            var sortedDebts = tempDebts.OrderByDescending(d => d.RawDate).ToList();

            foreach (var debt in sortedDebts)
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
    public DateTime RawDate { get; set; } // Used for sorting, not displayed
    public string DisplayText => $"{DateStr} - ₱ {Amount}";
}

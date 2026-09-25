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
            var tempPayments = new List<PaymentRecord>();

            // 1. Fetch driver's shifts
            var shiftsSnapshot = await CrossFirebaseFirestore.Current
                .GetCollection("shifts")
                .WhereEqualsTo("driverId", user.Uid)
                .GetDocumentsAsync<Dictionary<string, object>>();

            // 2. Fetch payments linked to those shifts
            foreach (var shift in shiftsSnapshot.Documents)
            {
                object shiftIdObj = shift.Data.ContainsKey("ShiftId") ? shift.Data["ShiftId"] :
                                    shift.Data.ContainsKey("shiftId") ? shift.Data["shiftId"] : null;

                string shiftId = shiftIdObj?.ToString();

                if (!string.IsNullOrEmpty(shiftId))
                {
                    var paymentsSnapshot = await CrossFirebaseFirestore.Current
                        .GetCollection("boundary_payments")
                        .WhereEqualsTo("shiftId", shiftId) // Using camelCase based on groupmate's WhereIn query
                        .GetDocumentsAsync<Dictionary<string, object>>();

                    foreach (var paymentDoc in paymentsSnapshot.Documents)
                    {
                        if (paymentDoc.Data != null)
                        {
                            // Extract Status (Enum: 0=Waiting, 1=Partial, 2=Paid)
                            string statusText = "UNPAID";
                            object statusObj = paymentDoc.Data.ContainsKey("PaymentStatus") ? paymentDoc.Data["PaymentStatus"] :
                                               paymentDoc.Data.ContainsKey("paymentStatus") ? paymentDoc.Data["paymentStatus"] : null;

                            string statusStr = statusObj?.ToString() ?? "";
                            if (statusStr == "1" || statusStr.Contains("Partial")) statusText = "(Partial)";
                            else if (statusStr == "2" || statusStr.Contains("Paid")) statusText = "(Full)";

                            // Extract Timestamp
                            string dateStr = "N/A";
                            object timeObj = paymentDoc.Data.ContainsKey("Timestamp") ? paymentDoc.Data["Timestamp"] :
                                             paymentDoc.Data.ContainsKey("timestamp") ? paymentDoc.Data["timestamp"] : null;

                            if (timeObj is DateTime dt)
                            {
                                dateStr = dt.ToLocalTime().ToString("M/dd");
                            }

                            // Extract AmountPaid
                            object amountObj = paymentDoc.Data.ContainsKey("AmountPaid") ? paymentDoc.Data["AmountPaid"] :
                                               paymentDoc.Data.ContainsKey("amountPaid") ? paymentDoc.Data["amountPaid"] : null;

                            decimal amount = amountObj != null ? Convert.ToDecimal(amountObj) : 0.00m;

                            tempPayments.Add(new PaymentRecord
                            {
                                DateStr = dateStr,
                                Amount = $"{amount:N2}",
                                Status = statusText,
                                RawDate = timeObj is DateTime rawDt ? rawDt : DateTime.MinValue
                            });
                        }
                    }
                }
            }

            // Sort newest payments first
            var sortedPayments = tempPayments.OrderByDescending(p => p.RawDate).ToList();

            foreach (var payment in sortedPayments)
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
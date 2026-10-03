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
        if (user == null) return;

        try
        {
            PaymentHistory.Clear();
            decimal totalDebt = 0m;
            var tempPayments = new List<PaymentRecord>();

            // 1. Fetch Debts & Calculate Outstanding Balance
            var debtsSnapshot = await CrossFirebaseFirestore.Current
                .GetCollection("debt_adjustments")
                .WhereEqualsTo("DriverId", user.Uid) // Adjusted for groupmate's PascalCase
                .GetDocumentsAsync<Dictionary<string, object>>();

            // Fallback to camelCase if PascalCase returns empty
            if (!debtsSnapshot.Documents.Any())
            {
                debtsSnapshot = await CrossFirebaseFirestore.Current
                    .GetCollection("debt_adjustments")
                    .WhereEqualsTo("driverId", user.Uid)
                    .GetDocumentsAsync<Dictionary<string, object>>();
            }

            foreach (var doc in debtsSnapshot.Documents)
            {
                object amountObj = doc.Data.ContainsKey("Amount") ? doc.Data["Amount"] :
                                   doc.Data.ContainsKey("amount") ? doc.Data["amount"] : null;

                if (amountObj != null)
                {
                    totalDebt += Convert.ToDecimal(amountObj);
                }
            }
            OutstandingDebtBalance = $"₱ {Math.Max(0, totalDebt):N2}";

            // 2. Fetch Shifts to get ShiftIds
            var shiftsSnapshot = await CrossFirebaseFirestore.Current
                .GetCollection("shifts")
                .WhereEqualsTo("driverId", user.Uid)
                .GetDocumentsAsync<Dictionary<string, object>>();

            foreach (var shift in shiftsSnapshot.Documents)
            {
                object shiftIdObj = shift.Data.ContainsKey("ShiftId") ? shift.Data["ShiftId"] :
                                    shift.Data.ContainsKey("shiftId") ? shift.Data["shiftId"] : null;

                string shiftId = shiftIdObj?.ToString();

                if (!string.IsNullOrEmpty(shiftId))
                {
                    // 3. Fetch Payments linked to ShiftId
                    var paymentsSnapshot = await CrossFirebaseFirestore.Current
                        .GetCollection("boundary_payments")
                        .WhereEqualsTo("ShiftId", shiftId)
                        .GetDocumentsAsync<Dictionary<string, object>>();

                    if (!paymentsSnapshot.Documents.Any())
                    {
                        paymentsSnapshot = await CrossFirebaseFirestore.Current
                            .GetCollection("boundary_payments")
                            .WhereEqualsTo("shiftId", shiftId)
                            .GetDocumentsAsync<Dictionary<string, object>>();
                    }

                    foreach (var paymentDoc in paymentsSnapshot.Documents)
                    {
                        if (paymentDoc.Data != null)
                        {
                            // Status Evaluation (0=Waiting, 1=Partial, 2=Paid)
                            string statusText = "UNPAID";
                            object statusObj = paymentDoc.Data.ContainsKey("PaymentStatus") ? paymentDoc.Data["PaymentStatus"] :
                                               paymentDoc.Data.ContainsKey("paymentStatus") ? paymentDoc.Data["paymentStatus"] : null;

                            string statusStr = statusObj?.ToString() ?? "";
                            if (statusStr == "1" || statusStr.Contains("Partial")) statusText = "(Partial)";
                            else if (statusStr == "2" || statusStr.Contains("Paid")) statusText = "(Full)";

                            // Timestamp Evaluation
                            string dateStr = "N/A";
                            object timeObj = paymentDoc.Data.ContainsKey("Timestamp") ? paymentDoc.Data["Timestamp"] :
                                             paymentDoc.Data.ContainsKey("timestamp") ? paymentDoc.Data["timestamp"] : null;

                            if (timeObj is DateTime dt)
                            {
                                dateStr = dt.ToLocalTime().ToString("M/dd");
                            }

                            // Amount Evaluation
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

            // 4. Update the UI and Sort
            var sortedPayments = tempPayments.OrderByDescending(p => p.RawDate).ToList();

            foreach (var payment in sortedPayments)
            {
                PaymentHistory.Add(payment);
            }

            if (PaymentHistory.Any())
            {
                CurrentShiftPayment = PaymentHistory.First().Status.Replace("(", "").Replace(")", "").ToUpper();
            }
            else
            {
                CurrentShiftPayment = "UNPAID";
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Ledger Firebase Error: {ex.Message}");
            OutstandingDebtBalance = "₱ 0.00";
            CurrentShiftPayment = "ERROR";
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

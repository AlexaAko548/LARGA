using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
using System.Collections.ObjectModel;
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

        // Firebase-ready async initialization
        _ = LoadDynamicPaymentDataAsync();
    }

    private async Task LoadDynamicPaymentDataAsync()
    {
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user != null)
        {
            try
            {
                // 1. Live Background Fetch (Two-Step Workaround)
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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Payment Fetch Error: {ex.Message}");
            }
        }

        // 2. DEMO OVERRIDE
        FullPaymentHistory.Clear();
        FullPaymentHistory.Add(new PaymentRecord { DateStr = "7/22", Amount = "400.00", Status = "(Partial)" });
        FullPaymentHistory.Add(new PaymentRecord { DateStr = "7/21", Amount = "800.00", Status = "(Full)" });
        FullPaymentHistory.Add(new PaymentRecord { DateStr = "7/20", Amount = "600.00", Status = "(Partial)" });
        FullPaymentHistory.Add(new PaymentRecord { DateStr = "7/19", Amount = "800.00", Status = "(Full)" });
        FullPaymentHistory.Add(new PaymentRecord { DateStr = "7/18", Amount = "700.00", Status = "(Partial)" });
        FullPaymentHistory.Add(new PaymentRecord { DateStr = "7/17", Amount = "800.00", Status = "(Full)" });
        FullPaymentHistory.Add(new PaymentRecord { DateStr = "7/16", Amount = "800.00", Status = "(Full)" });
        FullPaymentHistory.Add(new PaymentRecord { DateStr = "7/15", Amount = "800.00", Status = "(Full)" });
        FullPaymentHistory.Add(new PaymentRecord { DateStr = "7/14", Amount = "800.00", Status = "(Full)" });

        OnPropertyChanged(nameof(FullPaymentHistory));
    }
}
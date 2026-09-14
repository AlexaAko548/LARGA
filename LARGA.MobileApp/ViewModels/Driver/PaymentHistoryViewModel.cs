using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Driver;

public class PaymentHistoryViewModel : BindableObject
{
    public ObservableCollection<PaymentRecord> FullPaymentHistory { get; set; }
    public ICommand ExportCommand { get; }

    public PaymentHistoryViewModel()
    {
        // Static testing data mapping to the Payment History mockup
        FullPaymentHistory = new ObservableCollection<PaymentRecord>
        {
            new PaymentRecord { DateStr = "7/22", Amount = "500.00", Status = "(Partial)" },
            new PaymentRecord { DateStr = "7/21", Amount = "1000.00", Status = "(Full)" },
            new PaymentRecord { DateStr = "7/20", Amount = "800.00", Status = "(Partial)" },
            new PaymentRecord { DateStr = "7/19", Amount = "1000.00", Status = "(Full)" },
            new PaymentRecord { DateStr = "7/18", Amount = "700.00", Status = "(Partial)" },
            new PaymentRecord { DateStr = "7/17", Amount = "1000.00", Status = "(Full)" },
            new PaymentRecord { DateStr = "7/16", Amount = "1000.00", Status = "(Full)" },
            new PaymentRecord { DateStr = "7/15", Amount = "1000.00", Status = "(Full)" },
            new PaymentRecord { DateStr = "7/14", Amount = "1000.00", Status = "(Full)" }
        };

        ExportCommand = new Command(async () => await Application.Current.MainPage.DisplayAlert("Export", "Exporting ledger data...", "OK"));
    }
}
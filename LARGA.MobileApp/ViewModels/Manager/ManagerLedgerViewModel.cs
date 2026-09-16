using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LARGA.MobileApp.Models;
using LARGA.SharedCore.Models.FinancialLedger;
using LARGA.SharedCore.Services;

namespace LARGA.MobileApp.ViewModels.Manager;

public partial class ManagerLedgerViewModel : ObservableObject
{
    private readonly FinancialLedgerService _ledgerService;

    [ObservableProperty]
private ObservableCollection<LedgerItemModel> pendingClearances = new();

    [ObservableProperty]
    private ObservableCollection<LedgerItemModel> completedToday = new();

    [ObservableProperty]
    private int pendingCount;

    [ObservableProperty]
    private bool isBusy;

    public RecordPaymentViewModel PaymentModalViewModel { get; }

    public ManagerLedgerViewModel(FinancialLedgerService ledgerService, RecordPaymentViewModel paymentModalViewModel)
    {
        _ledgerService = ledgerService;
        PaymentModalViewModel = paymentModalViewModel;
        PaymentModalViewModel.OnPaymentRecorded = async () => await LoadDailySettlementsAsync();
    }

    [RelayCommand]
    public async Task LoadDailySettlementsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            DailySettlementSnapshot snapshot = await _ledgerService.GetDailySettlementAsync(DateTime.UtcNow);

            PendingClearances.Clear();
            CompletedToday.Clear();

            foreach (SettlementRow row in snapshot.Rows)
            {
                var item = new LedgerItemModel
                {
                    ShiftId = row.ShiftId,
                    DriverId = row.DriverId,
                    DriverName = row.DriverName,
                    TaxiId = row.TaxiId,
                    PlateNumber = row.TaxiId,
                    ExpectedAmount = row.ExpectedTotal,
                    AmountPaid = row.AmountPaid,
                    Status = row.Status
                };

                if (row.Status == SettlementStatus.Waiting)
                {
                    PendingClearances.Add(item);
                }
                else
                {
                    CompletedToday.Add(item);
                }
            }

            PendingCount = PendingClearances.Count;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenRecordPayment(LedgerItemModel item)
    {
        if (item == null) return;
        PaymentModalViewModel.Initialize(item);
    }
}
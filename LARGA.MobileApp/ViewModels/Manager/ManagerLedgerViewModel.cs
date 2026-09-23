using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LARGA.MobileApp.Models;
using LARGA.MobileApp.Services;
using LARGA.SharedCore.Models.FinancialLedger;

namespace LARGA.MobileApp.ViewModels.Manager;

public partial class ManagerLedgerViewModel : ObservableObject
{
    private readonly MobileFinancialLedgerService _ledgerService;

    [ObservableProperty]
private ObservableCollection<LedgerItemModel> pendingClearances = new();

    [ObservableProperty]
    private ObservableCollection<LedgerItemModel> completedToday = new();

    [ObservableProperty]
    private int pendingCount;

    [ObservableProperty]
    private bool isBusy;

    public bool ShowPendingEmptyState => !IsBusy && PendingCount == 0;

    public RecordPaymentViewModel PaymentModalViewModel { get; }

    public ManagerLedgerViewModel(MobileFinancialLedgerService ledgerService, RecordPaymentViewModel paymentModalViewModel)
    {
        _ledgerService = ledgerService;
        PaymentModalViewModel = paymentModalViewModel;
        PaymentModalViewModel.OnPaymentRecorded = async () => await LoadDailySettlementsAsync();
    }

    public void EnsurePaymentModalClosed() => PaymentModalViewModel.EnsureClosed();

    [RelayCommand]
    public async Task LoadDailySettlementsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            DailySettlementSnapshot snapshot = await _ledgerService.GetDailySettlementAsync(DateTime.Now);
            DateTime nowLocal = DateTime.Now;

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
                    ShiftStart = row.ShiftStart,
                    ShiftEnd = row.ShiftEnd,
                    ExpectedAmount = row.ExpectedTotal,
                    AmountPaid = row.AmountPaid,
                    Status = row.Status
                };

                if (IsPendingDueNow(item, nowLocal))
                {
                    PendingClearances.Add(item);
                }

                if (row.Status == SettlementStatus.Cleared)
                {
                    CompletedToday.Add(item);
                }
            }

            HashSet<string> todayShiftIds = snapshot.Rows
                .Where(row => !string.IsNullOrWhiteSpace(row.ShiftId))
                .Select(row => row.ShiftId)
                .ToHashSet();

            List<LedgerItemModel> mergedExtras = await _ledgerService.GetCompletedEntriesForTodayAsync(DateTime.Now, todayShiftIds);
            foreach (LedgerItemModel entry in mergedExtras)
            {
                CompletedToday.Add(entry);
            }

            PendingCount = PendingClearances.Count;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ManagerLedger load failed: {ex}");
            PendingClearances.Clear();
            CompletedToday.Clear();
            PendingCount = 0;
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
        PaymentModalViewModel.InitializeStandard(item);
    }

    [RelayCommand]
    private async Task OpenOtherPaymentAsync()
    {
        try
        {
            List<LARGA.Shared.Models.Entities.UserProfile> drivers = await _ledgerService.GetDriversForOtherPaymentAsync();
            PaymentModalViewModel.InitializeOtherPayment(drivers);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OpenOtherPayment failed: {ex}");
            PaymentModalViewModel.InitializeOtherPayment(new List<LARGA.Shared.Models.Entities.UserProfile>());
            if (Shell.Current != null)
            {
                await Shell.Current.DisplayAlert("Error", "Unable to load drivers for Other Payment.", "OK");
            }
        }
    }

    partial void OnPendingCountChanged(int value)
    {
        OnPropertyChanged(nameof(ShowPendingEmptyState));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPendingEmptyState));
    }

    private static bool IsPendingDueNow(LedgerItemModel item, DateTime nowLocal)
    {
        if (item.Status == SettlementStatus.Cleared)
        {
            return false;
        }

        // Pending cards are only for active shifts nearing expected end.
        if (item.ShiftEnd.HasValue || !item.ShiftStart.HasValue)
        {
            return false;
        }

        DateTime shiftStart = ToLocal(item.ShiftStart) ?? nowLocal;
        DateTime expectedEnd = shiftStart.AddHours(10);
        DateTime dueWindowStart = expectedEnd.AddHours(-3);
        DateTime dueWindowEnd = expectedEnd.AddHours(2);

        return nowLocal >= dueWindowStart && nowLocal <= dueWindowEnd;
    }

    private static DateTime? ToLocal(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        DateTime date = value.Value;
        return date.Kind switch
        {
            DateTimeKind.Utc => date.ToLocalTime(),
            DateTimeKind.Local => date,
            _ => DateTime.SpecifyKind(date, DateTimeKind.Local),
        };
    }
}
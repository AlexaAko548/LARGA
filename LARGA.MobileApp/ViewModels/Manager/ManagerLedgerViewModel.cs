using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.MobileApp.Services;
using LARGA.SharedCore;
using LARGA.SharedCore.Ledger;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Manager;

public class ManagerLedgerViewModel : BindableObject
{
    private readonly QuickLedgerService _service;
    private QuickLedgerSnapshot? _snapshot;
    private bool _isLoading;
    private bool _reloadRequested;
    private string? _auditWarning;
    private string _warningText = string.Empty;
    private bool _hasError;
    private string _errorMessage = string.Empty;
    private string _pendingCountText = string.Empty;
    private string _dateText = string.Empty;

    public ObservableCollection<PendingBoundaryItem> PendingBoundaries { get; } = new();
    public ObservableCollection<PaymentDoneItem> PaymentsDoneToday { get; } = new();

    public RecordPaymentViewModel RecordPayment { get; }

    public ICommand LoadCommand { get; }
    public ICommand OpenOtherPaymentCommand { get; }
    public ICommand OpenPendingCommand { get; }

    public ManagerLedgerViewModel(QuickLedgerService service, RecordPaymentViewModel recordPayment)
    {
        _service = service;
        RecordPayment = recordPayment;
        RecordPayment.Saved += async (_, _) =>
        {
            _auditWarning = RecordPayment.AuditWarning;
            await LoadAsync();
        };

        LoadCommand = new Command(async () => await LoadAsync());
        OpenOtherPaymentCommand = new Command(() => OpenOther(null));
        OpenPendingCommand = new Command<PendingBoundaryItem>(item => OpenPending(item));
    }

    public bool HasError
    {
        get => _hasError;
        private set { _hasError = value; OnPropertyChanged(); }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); }
    }

    public string PendingCountText
    {
        get => _pendingCountText;
        private set { _pendingCountText = value; OnPropertyChanged(); }
    }

    public string WarningText
    {
        get => _warningText;
        private set
        {
            _warningText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasWarning));
        }
    }

    // The warning label takes no space when there is nothing to warn about.
    public bool HasWarning => !string.IsNullOrEmpty(_warningText);

    public string DateText
    {
        get => _dateText;
        private set { _dateText = value; OnPropertyChanged(); }
    }

    // Empty-state flags. ObservableCollection doesn't raise change notifications the view can bind to,
    // so these are set explicitly after each load.
    public bool HasPending { get; private set; }
    public bool HasPaymentsDoneToday { get; private set; }

    public async Task LoadAsync()
    {
        // OnAppearing or a saved payment can ask for a reload while one is still running. Remember the request and
        // run it afterwards, instead of dropping it, so the page never keeps rows from before the save.
        if (_isLoading)
        {
            _reloadRequested = true;
            return;
        }

        _isLoading = true;
        try
        {
            do
            {
                _reloadRequested = false;
                await LoadOnceAsync();
            }
            while (_reloadRequested);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task LoadOnceAsync()
    {
        DateText = PhilippineTime.Now.ToString("dddd, MMM d", CultureInfo.InvariantCulture);

        try
        {
            _snapshot = await _service.LoadAsync();
            QuickLedgerResult result = _snapshot.Result;

            PendingBoundaries.Clear();
            foreach (PendingBoundaryRow row in result.Pending)
            {
                PendingBoundaries.Add(new PendingBoundaryItem
                {
                    Row = row,
                    DriverName = row.DriverName,
                    PlateText = row.TaxiPlate,
                    BalanceText = Money(row.Remaining),
                    ProgressText = $"{Money(row.Paid)} paid of {Money(row.Expected)}",
                });
            }

            PaymentsDoneToday.Clear();
            foreach (PaymentDoneRow row in result.PaymentsDone)
            {
                PaymentsDoneToday.Add(new PaymentDoneItem
                {
                    DriverName = row.DriverName,
                    // One row per transaction: the exact amount of this payment, how it was paid, and when.
                    DetailText = $"{MethodLabel(row.PaymentMethod)} · {row.TimestampUtc.ToPhilippineTime().ToString("h:mm tt", CultureInfo.InvariantCulture)}",
                    StatusText = Money(row.AmountPaid),
                });
            }

            int pendingCount = result.Pending.Count;
            PendingCountText = $"{pendingCount} pending clearance{(pendingCount == 1 ? string.Empty : "s")}";
            // Records the ledger could not read are left out of the figures, so say so rather than hide them.
            var warnings = new List<string>();
            if (!string.IsNullOrEmpty(_auditWarning)) warnings.Add(_auditWarning!);
            if (_snapshot.Warnings.Count > 0)
            {
                warnings.Add($"{_snapshot.Warnings.Count} record(s) could not be read and are not included: {string.Join("; ", _snapshot.Warnings.Take(3))}");
            }
            WarningText = string.Join("  ", warnings);
            HasError = false;
            ErrorMessage = string.Empty;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Quick Ledger load error: {ex}");
            HasError = true;
            ErrorMessage = "Could not load the ledger. Reopen this tab to try again.";
        }
        finally
        {
            HasPending = PendingBoundaries.Count > 0;
            HasPaymentsDoneToday = PaymentsDoneToday.Count > 0;
            OnPropertyChanged(nameof(HasPending));
            OnPropertyChanged(nameof(HasPaymentsDoneToday));
        }
    }

    private void OpenPending(PendingBoundaryItem? item)
    {
        if (_snapshot is null || item is null) return;
        RecordPayment.OpenForBoundary(_snapshot, item.Row);
    }

    private void OpenOther(string? driverId)
    {
        if (_snapshot is null) return;
        RecordPayment.OpenForOther(_snapshot, driverId);
    }

    private static string MethodLabel(string method) =>
        string.Equals(method, QuickLedgerCalculator.EWalletMethod, StringComparison.OrdinalIgnoreCase) ? "E-WALLET" : "CASH";

    private static string Money(decimal amount) =>
        "₱ " + amount.ToString("#,##0.00", CultureInfo.InvariantCulture);
}

public class PendingBoundaryItem
{
    public PendingBoundaryRow Row { get; set; } = null!;
    public string DriverName { get; set; } = string.Empty;
    public string PlateText { get; set; } = string.Empty;
    public string BalanceText { get; set; } = string.Empty;
    public string ProgressText { get; set; } = string.Empty;
}

public class PaymentDoneItem
{
    public string DriverName { get; set; } = string.Empty;
    public string DetailText { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
}


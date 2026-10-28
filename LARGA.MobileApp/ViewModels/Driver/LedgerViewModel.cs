using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Networking;
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
using LARGA.MobileApp.Services;

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

    private bool _isShowingCachedData;
    /// <summary>The balance and list came from the phone's offline copy, so the page says they may be out of date.</summary>
    public bool IsShowingCachedData
    {
        get => _isShowingCachedData;
        private set { _isShowingCachedData = value; OnPropertyChanged(); }
    }

    public ICommand ViewDebtDetailsCommand { get; }
    public ICommand ViewAllHistoryCommand { get; }
    public ICommand RefreshCommand { get; }

    public LedgerViewModel()
    {
        ViewDebtDetailsCommand = new Command(async () => await Shell.Current.GoToAsync("debt-details"));
        ViewAllHistoryCommand = new Command(async () => await Shell.Current.GoToAsync("payment-history"));
        RefreshCommand = new Command(async () => await RefreshAsync());
    }

    // -- Live updates: the manager recording a boundary payment on Quick Ledger shows up here without the
    // driver pulling to refresh (test gap: "doesn't automatically refresh"). Two listeners (this driver's
    // shifts and boundary_payments) just trigger the same reload RefreshCommand already runs, debounced so
    // the near-simultaneous events a payment write produces collapse into one Firestore read.
    private IDisposable? _shiftsListener;
    private IDisposable? _paymentsListener;
    private DebouncedAction? _reloadDebounced;
    private bool _isListening;

    public void StartListening()
    {
        if (_isListening) return;
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user == null) return;
        _isListening = true;

        _reloadDebounced ??= new DebouncedAction(() =>
        {
            MainThread.BeginInvokeOnMainThread(async () => await LoadDynamicLedgerDataAsync(forceRefresh: true));
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(400));
        SessionEvents.SigningOut += OnSigningOut;
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;

        _shiftsListener = CrossFirebaseFirestore.Current
            .GetCollection("shifts")
            .WhereEqualsTo("driverId", user.Uid)
            .AddSnapshotListener<QuickLedgerService.ShiftProxy>(
                _ => _reloadDebounced.Trigger(),
                ex => System.Diagnostics.Debug.WriteLine($"Ledger shifts listener error: {ex.Message}"));

        _paymentsListener = CrossFirebaseFirestore.Current
            .GetCollection("boundary_payments")
            .WhereEqualsTo("driverId", user.Uid)
            .AddSnapshotListener<QuickLedgerService.BoundaryPaymentProxy>(
                _ => _reloadDebounced.Trigger(),
                ex => System.Diagnostics.Debug.WriteLine($"Ledger payments listener error: {ex.Message}"));
    }

    public void StopListening()
    {
        _isListening = false;
        SessionEvents.SigningOut -= OnSigningOut;
        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
        _shiftsListener?.Dispose();
        _paymentsListener?.Dispose();
        _shiftsListener = _paymentsListener = null;
        _reloadDebounced?.Dispose();
        _reloadDebounced = null;
    }

    private void OnSigningOut(object? sender, EventArgs e) => StopListening();

    // Back online with an offline copy on screen: reload from the server. The listeners don't fire for this when
    // nothing changed meanwhile.
    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        if (e.NetworkAccess == NetworkAccess.Internet && IsShowingCachedData) _reloadDebounced?.Trigger();
    }

    private bool _isRefreshing;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set { _isRefreshing = value; OnPropertyChanged(); }
    }

    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            await LoadDynamicLedgerDataAsync(forceRefresh: true); // a pull always reads Firestore again
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private bool _isLoading;

    // Test gap: the empty-list message ("No payments recorded yet.") showed while the fetch was still
    // running (the list is cleared before it lands), reading as if the driver truly had none.
    public string EmptyStateMessage => _isLoading ? "Loading your payment history…" : "No payments recorded yet.";

    /// <summary>
    /// Called by LedgerPage each time the tab appears: Shell keeps the tab page alive, so loading
    /// once in the constructor would never show payments recorded after the first visit.
    /// </summary>
    public async Task LoadDynamicLedgerDataAsync(bool forceRefresh = false)
    {
        // P0 timing (PERFORMANCE_REPORT.md).
        using var _perf = LARGA.SharedCore.PerfLog.Start("Driver Ledger load");
        using var _loading = LARGA.MobileApp.Services.LoadingState.Begin(); // LAR-117: app-wide loading indicator
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user == null || _isLoading) return;
        _isLoading = true;
        OnPropertyChanged(nameof(EmptyStateMessage));

        try
        {
            PaymentHistory.Clear();

            // Same per-shift calculation as the web's Master Debt Ledger, so the driver and the
            // manager see the same balance (see DriverDebtCalculator).
            var ledger = await DriverDebtCalculator.LoadAsync(user.Uid, forceRefresh);
            IsShowingCachedData = ledger.IsFromCache;
            OutstandingDebtBalance = $"₱ {ledger.TotalDebt:N2}";

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
                PaymentHistory.Add(payment);
            }

            // Today's shift: its payment status, or UNPAID when nothing has been paid on it yet.
            var latestShift = ledger.Shifts.OrderByDescending(s => s.ShiftStartUtc).FirstOrDefault();
            CurrentShiftPayment = latestShift is null || !latestShift.HasPayment
                ? "UNPAID"
                : latestShift.PaymentStatus == "Paid" ? "FULL" : latestShift.PaymentStatus == "Partial" ? "PARTIAL" : "UNPAID";
        }
        catch (Exception ex)
        {
            // Not "₱ 0.00": a failed read must not look like the driver owes nothing.
            IsShowingCachedData = false;
            OutstandingDebtBalance = "--";
            CurrentShiftPayment = "ERROR";
            await FirestoreErrors.ShowLoadFailureAsync("Driver ledger", ex, "your ledger", canPullToRefresh: true);
        }
        finally
        {
            _isLoading = false;
            OnPropertyChanged(nameof(EmptyStateMessage));
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

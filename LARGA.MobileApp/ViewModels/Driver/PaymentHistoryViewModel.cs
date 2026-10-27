using LARGA.MobileApp.Controls;
using LARGA.MobileApp.Services;
using LARGA.SharedCore;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace LARGA.MobileApp.ViewModels.Driver;

public class PaymentHistoryViewModel : BindableObject
{
    private readonly IDownloadService _downloadService;

    public ObservableCollection<PaymentRecord> FullPaymentHistory { get; set; } = new();
    public ICommand ExportCommand { get; }

    // The raw shift records behind FullPaymentHistory - Export needs the columns the list view doesn't show
    // (Shift ID, Unit, Amount Due, Payment Type, Balance), so it's kept alongside rather than re-read.
    private List<DriverDebtCalculator.ShiftEntry> _paidShifts = new();
    private bool _isExporting;

    // Test gap: the empty-list message ("No payments recorded yet.") showed while the fetch was still
    // running (the list is empty until it lands), reading as if the driver truly had none. This tells the
    // EmptyView which sentence to show instead while a load is in flight.
    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value) return;
            _isLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EmptyStateMessage));
        }
    }

    public string EmptyStateMessage => IsLoading ? "Loading your payment history…" : "No payments recorded yet.";

    public PaymentHistoryViewModel(IDownloadService downloadService)
    {
        _downloadService = downloadService;
        ExportCommand = new Command(async () => await ExportAsync());
    }

    public async Task LoadDynamicPaymentDataAsync(bool forceRefresh = false)
    {
        // P0 timing (PERFORMANCE_REPORT.md).
        using var _perf = LARGA.SharedCore.PerfLog.Start("Payment History load");
        using var _loading = LARGA.MobileApp.Services.LoadingState.Begin(); // LAR-117: app-wide loading indicator
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user == null) return;

        IsLoading = true;
        try
        {
            FullPaymentHistory.Clear();

            // Same records as the Ledger screen (DriverDebtCalculator reads them through typed
            // proxies - Plugin.Firebase returns Dictionary<string, object> reads empty).
            var ledger = await DriverDebtCalculator.LoadAsync(user.Uid, forceRefresh);
            _paidShifts = ledger.Shifts.Where(s => s.HasPayment).ToList();

            var payments = _paidShifts
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
                FullPaymentHistory.Add(payment);
            }

            OnPropertyChanged(nameof(FullPaymentHistory));
        }
        catch (Exception ex)
        {
            await FirestoreErrors.ShowLoadFailureAsync("Payment history", ex, "your payment history", canPullToRefresh: false);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // -- Export: CSV or PDF, date range defaulting to the last 7 days (same default as ManagerWeb's Generate
    // Reports). Test gap: "export is currently not working" - ExportCommand used to just show an alert.
    private async Task ExportAsync()
    {
        if (_isExporting) return;
        Page? page = Shell.Current ?? Application.Current?.MainPage;
        if (page == null) return;

        ExportOptionsResult? options = await ExportOptionsPopup.ShowAsync(page);
        if (options == null) return;

        _isExporting = true;
        try
        {
            // Whole days (no timezone shift), same as ManagerWeb's DownloadReportAsync: "To" includes that
            // entire day instead of stopping at midnight.
            DateTime fromUtc = DateTime.SpecifyKind(options.FromDate.Date, DateTimeKind.Utc);
            DateTime toUtc = DateTime.SpecifyKind(options.ToDate.Date, DateTimeKind.Utc).AddDays(1).AddTicks(-1);

            var report = PaymentHistoryExportService.BuildReport(_paidShifts, fromUtc, toUtc);
            string baseName = PaymentHistoryExportService.FileBaseName(options.FromDate, options.ToDate);

            byte[] content;
            string fileName;
            string contentType;
            if (options.IsPdf)
            {
                string generatedBy = CrossFirebaseAuth.Current.CurrentUser?.Email ?? "Driver";
                content = PaymentHistoryExportService.ToPdf(report, options.FromDate, options.ToDate, generatedBy);
                fileName = $"{baseName}.pdf";
                contentType = "application/pdf";
            }
            else
            {
                content = Encoding.UTF8.GetBytes(PaymentHistoryExportService.ToCsv(report));
                fileName = $"{baseName}.csv";
                contentType = "text/csv";
            }

            DownloadResult result = await _downloadService.SaveToDownloadsAsync(fileName, content, contentType);
            if (result.OpenAsync is not null)
            {
                bool openNow = await page.DisplayAlert("Export complete", $"Saved to {result.DisplayPath}.", "Open", "OK");
                if (openNow)
                {
                    try
                    {
                        await result.OpenAsync();
                    }
                    catch (Exception openEx)
                    {
                        await page.DisplayAlert("Couldn't open the file", openEx.Message, "OK");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            await (Shell.Current ?? Application.Current?.MainPage)!.DisplayAlert("Export failed", $"Couldn't export your payment history: {ex.Message}", "OK");
        }
        finally
        {
            _isExporting = false;
        }
    }

    // -- Live updates: a payment recorded elsewhere (Quick Ledger) shows up here without pulling to refresh,
    // same as the Ledger screen (LedgerViewModel.StartListening).
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
            MainThread.BeginInvokeOnMainThread(async () => await LoadDynamicPaymentDataAsync(forceRefresh: true));
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(400));
        SessionEvents.SigningOut += OnSigningOut;

        _shiftsListener = CrossFirebaseFirestore.Current
            .GetCollection("shifts")
            .WhereEqualsTo("driverId", user.Uid)
            .AddSnapshotListener<QuickLedgerService.ShiftProxy>(
                _ => _reloadDebounced.Trigger(),
                ex => System.Diagnostics.Debug.WriteLine($"Payment history shifts listener error: {ex.Message}"));

        _paymentsListener = CrossFirebaseFirestore.Current
            .GetCollection("boundary_payments")
            .WhereEqualsTo("driverId", user.Uid)
            .AddSnapshotListener<QuickLedgerService.BoundaryPaymentProxy>(
                _ => _reloadDebounced.Trigger(),
                ex => System.Diagnostics.Debug.WriteLine($"Payment history payments listener error: {ex.Message}"));
    }

    public void StopListening()
    {
        _isListening = false;
        SessionEvents.SigningOut -= OnSigningOut;
        _shiftsListener?.Dispose();
        _paymentsListener?.Dispose();
        _shiftsListener = _paymentsListener = null;
        _reloadDebounced?.Dispose();
        _reloadDebounced = null;
    }

    private void OnSigningOut(object? sender, EventArgs e) => StopListening();
}

// Note: Ensure PaymentRecord class matches the one in LedgerViewModel.cs
// If they are in the same namespace, you only need to define PaymentRecord once.

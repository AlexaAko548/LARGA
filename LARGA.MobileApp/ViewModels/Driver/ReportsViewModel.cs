using CommunityToolkit.Mvvm.Messaging;
using LARGA.MobileApp.Services;
using LARGA.MobileApp.Views.Driver;
using LARGA.SharedCore;
using LARGA.SharedCore.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace LARGA.MobileApp.ViewModels.Driver;

public class ReportsViewModel : BindableObject
{
    public string CurrentDate => DateTime.Now.ToString("dddd, dd MMM yyyy");

    private bool _isVehicleDefectTabSelected = true;
    public bool IsVehicleDefectTabSelected
    {
        get => _isVehicleDefectTabSelected;
        set
        {
            _isVehicleDefectTabSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFuelTabSelected));
        }
    }

    public bool IsFuelTabSelected => !IsVehicleDefectTabSelected;

    public ObservableCollection<DefectReportItem> DefectReports { get; } = new();
    public ObservableCollection<FuelReportItem> FuelReports { get; } = new();
    public ICommand AddFuelReportCommand { get; }
    public ICommand SelectVehicleDefectTabCommand { get; }
    public ICommand SelectFuelTabCommand { get; }
    public ICommand AddVehicleReportCommand { get; }
    public ICommand LoadReportsCommand { get; }
    public ICommand ViewReportDetailsCommand { get; }
    public ICommand ViewFuelReportDetailsCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand LoadMoreDefectsCommand { get; }
    public ICommand LoadMoreFuelReportsCommand { get; }

    // The lists show the newest PageSize reports, with "Load more" for older ones (PERFORMANCE_REPORT.md
    // P10) - a long history no longer builds hundreds of cards at once. Everything is still loaded, so
    // nothing is hidden for good.
    private const int PageSize = 50;
    private List<DefectReportItem> _allDefects = new();
    private List<FuelReportItem> _allFuelReports = new();

    public bool HasMoreDefects => DefectReports.Count < _allDefects.Count;
    public bool HasMoreFuelReports => FuelReports.Count < _allFuelReports.Count;

    private bool _isRefreshing;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set { _isRefreshing = value; OnPropertyChanged(); }
    }

    public ReportsViewModel()
    {
        WeakReferenceMessenger.Default.Register<ReportsViewModel, FuelReportSubmittedMessage>(this, static (recipient, _) =>
        {
            recipient.RefreshReportsAfterFuelSubmission();
        });

        SelectVehicleDefectTabCommand = new Command(() => IsVehicleDefectTabSelected = true);
        SelectFuelTabCommand = new Command(() => IsVehicleDefectTabSelected = false);
        AddVehicleReportCommand = new Command(async () => await Shell.Current.GoToAsync("vehicle-defect-page"));
        LoadReportsCommand = new Command(async () => await LoadReportsAsync());
        RefreshCommand = new Command(async () => await RefreshAsync());
        LoadMoreDefectsCommand = new Command(() => ShowMore(DefectReports, _allDefects, nameof(HasMoreDefects)));
        LoadMoreFuelReportsCommand = new Command(() => ShowMore(FuelReports, _allFuelReports, nameof(HasMoreFuelReports)));
        AddFuelReportCommand = new Command(async () => await Shell.Current.GoToAsync("fuel-report-page"));
        ViewReportDetailsCommand = new Command<DefectReportItem>(async (item) =>
        {
            if (item == null || string.IsNullOrEmpty(item.Id)) return;
            await Shell.Current.GoToAsync($"defect-report-detail?id={item.Id}");
        });
        ViewFuelReportDetailsCommand = new Command<FuelReportItem>(async (item) =>
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id)) return;
            await Shell.Current.GoToAsync($"{nameof(FuelReportDetailPage)}?id={item.Id}");
        });
    }

    private void RefreshReportsAfterFuelSubmission()
    {
        MainThread.BeginInvokeOnMainThread(async () => await LoadReportsAsync());
    }

    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            await LoadReportsAsync();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    // Adds the next PageSize items of the full (newest-first) list to what's shown.
    private void ShowMore<T>(ObservableCollection<T> shown, List<T> all, string hasMoreProperty)
    {
        foreach (T item in all.Skip(shown.Count).Take(PageSize))
        {
            shown.Add(item);
        }
        OnPropertyChanged(hasMoreProperty);
    }

    private async Task LoadReportsAsync()
    {
        // P0 timing (PERFORMANCE_REPORT.md).
        using var _perf = LARGA.SharedCore.PerfLog.Start("Driver Reports load");
        using var _loading = LARGA.MobileApp.Services.LoadingState.Begin(); // LAR-117: app-wide loading indicator
        try
        {
            var currentUser = CrossFirebaseAuth.Current.CurrentUser;
            if (currentUser == null) return;

            // Both lists are read at once (PERFORMANCE_REPORT.md P10).
            var defectTask = CrossFirebaseFirestore.Current
                .GetCollection("maintenance_logs")
                .WhereEqualsTo("reportedByDriverId", currentUser.Uid)
                .GetDocumentsAsync<DefectReportProxy>();
            var fuelTask = CrossFirebaseFirestore.Current
                .GetCollection("fuel_logs")
                .WhereEqualsTo("driverId", currentUser.Uid)
                .GetDocumentsAsync<FuelReportProxy>();
            await Task.WhenAll(defectTask, fuelTask);

            // 1. VEHICLE DEFECTS
            var defectSnapshot = defectTask.Result;
            LARGA.SharedCore.PerfLog.Reads("maintenance_logs (own)", defectSnapshot.Documents.Count());

            var tempDefects = new List<DefectReportItem>();
            foreach (var doc in defectSnapshot.Documents)
            {
                if (doc.Data == null) continue;
                var dateLogged = FirestoreDateTimeFix.Apply(doc.Data.DateLogged).ToLocalTime();
                tempDefects.Add(new DefectReportItem
                {
                    Id = doc.Reference.Id,
                    Title = doc.Data.IssueTitle,
                    DateLogged = dateLogged,
                    DateDisplay = dateLogged.ToString("MMM d, yyyy"),
                    Status = doc.Data.Status
                });
            }

            _allDefects = tempDefects.OrderByDescending(i => i.DateLogged).ToList();
            DefectReports.Clear();
            ShowMore(DefectReports, _allDefects, nameof(HasMoreDefects));

            // 2. FUEL REPORTS
            var fuelSnapshot = fuelTask.Result;
            LARGA.SharedCore.PerfLog.Reads("fuel_logs (own)", fuelSnapshot.Documents.Count());

            var tempFuels = new List<FuelReportItem>();
            foreach (var doc in fuelSnapshot.Documents)
            {
                if (doc.Data == null) continue;

                // The list shows when the report was submitted, not the date on the receipt.
                // Reports filed before submittedAt existed fall back to the receipt date.
                var receiptUtc = FirestoreDateTimeFix.ReadUtc(doc.Data.ReceiptTimestamp);
                var submittedUtc = FirestoreDateTimeFix.ReadUtc(doc.Data.SubmittedAt);

                DateTime? dateLogged = submittedUtc.HasValue
                    ? submittedUtc.Value.ToPhilippineTime()
                    : receiptUtc.HasValue ? FuelReportDates.ReceiptDate(receiptUtc.Value) : null;

                tempFuels.Add(new FuelReportItem
                {
                    Id = doc.Reference.Id,
                    DateLogged = dateLogged ?? DateTime.MinValue,
                    DateDisplay = dateLogged?.ToString("MMM d, yyyy") ?? "Unknown date",
                    Cost = ParseFuelCost(doc.Data.FuelCost).ToString("N2"),
                    Status = string.IsNullOrWhiteSpace(doc.Data.VerificationStatus) ? "Pending" : doc.Data.VerificationStatus,
                    HasDateMismatch = FuelReportDates.MismatchNote(receiptUtc, submittedUtc) is not null
                });
            }

            _allFuelReports = tempFuels.OrderByDescending(i => i.DateLogged).ToList();
            FuelReports.Clear();
            ShowMore(FuelReports, _allFuelReports, nameof(HasMoreFuelReports));
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Load Reports Error: {ex.Message}");
        }
    }

    private class DefectReportProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("issueTitle")]
        public string IssueTitle { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("dateLogged")]
        public System.DateTime DateLogged { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("status")]
        public string Status { get; set; }
    }

    public class FuelReportProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("receiptTimestamp")]
        public object? ReceiptTimestamp { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("submittedAt")]
        public object? SubmittedAt { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("fuelCost")]
        public object? FuelCost { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("verificationStatus")]
        public string? VerificationStatus { get; set; }
    }

    private static decimal ParseFuelCost(object? value)
    {
        if (value is null)
            return 0m;

        if (value is decimal d)
            return d;

        if (value is double dbl)
            return Convert.ToDecimal(dbl);

        if (value is float f)
            return Convert.ToDecimal(f);

        if (value is long l)
            return l;

        if (value is int i)
            return i;

        if (decimal.TryParse(value.ToString(), out var parsed))
            return parsed;

        return 0m;
    }
}

public class DefectReportItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public System.DateTime DateLogged { get; set; }
    public string DateDisplay { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string DisplayText => $"{DateDisplay} - {Title}";
}

public class FuelReportItem
{
    public string Id { get; set; } = string.Empty;
    public System.DateTime DateLogged { get; set; }
    public string DateDisplay { get; set; } = string.Empty;
    public string Cost { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    // The receipt is dated on a different day than the report was submitted.
    public bool HasDateMismatch { get; set; }
    public string DisplayText => $"{DateDisplay} - ₱ {Cost}";
}

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using LARGA.MobileApp.Services;
using LARGA.SharedCore.Services;
using Plugin.Firebase.Firestore;
using Plugin.Firebase.Auth;

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

    public ReportsViewModel() 
    {
        SelectVehicleDefectTabCommand = new Command(() => IsVehicleDefectTabSelected = true);
        SelectFuelTabCommand = new Command(() => IsVehicleDefectTabSelected = false);
        AddVehicleReportCommand = new Command(async () => await Shell.Current.GoToAsync("vehicle-defect-page"));
        LoadReportsCommand = new Command(async () => await LoadReportsAsync());
        AddFuelReportCommand = new Command(async () => await Shell.Current.GoToAsync("fuel-report-page"));
        ViewReportDetailsCommand = new Command<DefectReportItem>(async (item) =>
        {
            if (item == null || string.IsNullOrEmpty(item.Id)) return;
            await Shell.Current.GoToAsync($"defect-report-detail?id={item.Id}");
        });
    }

    private async Task LoadReportsAsync()
    {
        try
        {
            var currentUser = CrossFirebaseAuth.Current.CurrentUser;
            if (currentUser == null) return;

            // 1. FETCH VEHICLE DEFECTS
            var defectSnapshot = await CrossFirebaseFirestore.Current
                .GetCollection("maintenance_logs")
                .WhereEqualsTo("reportedByDriverId", currentUser.Uid)
                .GetDocumentsAsync<DefectReportProxy>();

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

            DefectReports.Clear();
            foreach (var item in tempDefects.OrderByDescending(i => i.DateLogged))
            {
                DefectReports.Add(item);
            }

            // 2. FETCH FUEL REPORTS
            var fuelSnapshot = await CrossFirebaseFirestore.Current
                .GetCollection("fuel_logs")
                .WhereEqualsTo("driverId", currentUser.Uid)
                .GetDocumentsAsync<FuelReportProxy>();

            var tempFuels = new List<FuelReportItem>();
            foreach (var doc in fuelSnapshot.Documents)
            {
                if (doc.Data == null) continue;
                var dateLogged = FirestoreDateTimeFix.Apply(doc.Data.ReceiptTimestamp).ToLocalTime();
                tempFuels.Add(new FuelReportItem
                {
                    Id = doc.Reference.Id,
                    DateLogged = dateLogged,
                    DateDisplay = dateLogged.ToString("MMM d, yyyy"),
                    Cost = doc.Data.FuelCost.ToString("N2"),
                    Status = doc.Data.VerificationStatus ?? "Pending"
                });
            }

            FuelReports.Clear();
            foreach (var item in tempFuels.OrderByDescending(i => i.DateLogged))
            {
                FuelReports.Add(item);
            }
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
        public System.DateTime ReceiptTimestamp { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("fuelCost")]
        public double FuelCost { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("verificationStatus")]
        public string VerificationStatus { get; set; }
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
    public string DisplayText => $"{DateDisplay} - ₱ {Cost}";
}
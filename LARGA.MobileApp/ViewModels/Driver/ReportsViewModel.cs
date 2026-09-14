using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
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

    public ICommand SelectVehicleDefectTabCommand { get; }
    public ICommand SelectFuelTabCommand { get; }
    public ICommand AddVehicleReportCommand { get; }
    public ICommand LoadReportsCommand { get; }

    public ReportsViewModel()
    {
        SelectVehicleDefectTabCommand = new Command(() => IsVehicleDefectTabSelected = true);
        SelectFuelTabCommand = new Command(() => IsVehicleDefectTabSelected = false);
        AddVehicleReportCommand = new Command(async () => await Shell.Current.GoToAsync("vehicle-defect-page"));
        LoadReportsCommand = new Command(async () => await LoadReportsAsync());
    }

    private async Task LoadReportsAsync()
    {
        try
        {
            var currentUser = CrossFirebaseAuth.Current.CurrentUser;
            if (currentUser == null) return;

            var snapshot = await CrossFirebaseFirestore.Current
                .GetCollection("maintenance_logs")
                .WhereEqualsTo("reportedByDriverId", currentUser.Uid)
                .GetDocumentsAsync<DefectReportProxy>();

            DefectReports.Clear();
            foreach (var doc in snapshot.Documents)
            {
                System.Diagnostics.Debug.WriteLine($"Doc found. Data null? {doc.Data == null}. Title: {doc.Data?.IssueTitle}");
                if (doc.Data == null) continue;
                DefectReports.Add(new DefectReportItem
                {
                    Title = doc.Data.IssueTitle,
                    // DateDisplay = doc.Data.DateLogged.ToString("MMM d, yyyy"),
                    Status = doc.Data.Status
                });
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

        // [Plugin.Firebase.Firestore.FirestoreProperty("dateLogged")]
        // public System.DateTime DateLogged { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("status")]
        public string Status { get; set; }
    }
}

public class DefectReportItem
{
    public string Title { get; set; } = string.Empty;
    public string DateDisplay { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string DisplayText => $"{DateDisplay} - {Title}";
}
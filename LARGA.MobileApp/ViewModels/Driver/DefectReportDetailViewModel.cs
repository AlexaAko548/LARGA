using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Firestore;
using LARGA.MobileApp.Services;

namespace LARGA.MobileApp.ViewModels.Driver;

[QueryProperty(nameof(ReportId), "id")]
public class DefectReportDetailViewModel : BindableObject
{
    private string _reportId = string.Empty;
    public string ReportId
    {
        get => _reportId;
        set
        {
            _reportId = value;
            OnPropertyChanged();
            _ = LoadReportAsync();
        }
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set { _title = value; OnPropertyChanged(); }
    }

    private string _description = string.Empty;
    public string Description
    {
        get => _description;
        set { _description = value; OnPropertyChanged(); }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    private string _priority = string.Empty;
    public string Priority
    {
        get => _priority;
        set { _priority = value; OnPropertyChanged(); }
    }

    private string _dateLoggedDisplay = string.Empty;
    public string DateLoggedDisplay
    {
        get => _dateLoggedDisplay;
        set { _dateLoggedDisplay = value; OnPropertyChanged(); }
    }

    private ImageSource? _photo;
    public ImageSource? Photo
    {
        get => _photo;
        set { _photo = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasPhoto)); }
    }

    public bool HasPhoto => Photo != null;

    public ICommand GoBackCommand { get; }

    public DefectReportDetailViewModel()
    {
        GoBackCommand = new Command(async () => await Shell.Current.GoToAsync(".."));
    }

    private async Task LoadReportAsync()
    {
        if (string.IsNullOrWhiteSpace(ReportId)) return;

        IsLoading = true;
        try
        {
            var doc = await CrossFirebaseFirestore.Current
                .GetCollection("maintenance_logs")
                .GetDocument(ReportId)
                .GetDocumentSnapshotAsync<DefectReportDetailProxy>();

            if (doc?.Data == null) return;

            Title = doc.Data.IssueTitle;
            Description = doc.Data.IssueDescription;
            Status = doc.Data.Status;
            Priority = doc.Data.PriorityLevel;
            DateLoggedDisplay = FirestoreDateTimeFix.Apply(doc.Data.DateLogged).ToLocalTime().ToString("MMM d, yyyy 'at' h:mm tt");

            if (!string.IsNullOrWhiteSpace(doc.Data.SupportingPhotoUrl))
            {
                Photo = ImageSource.FromFile(doc.Data.SupportingPhotoUrl);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Load Report Detail Error: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private class DefectReportDetailProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("issueTitle")]
        public string IssueTitle { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("issueDescription")]
        public string IssueDescription { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("status")]
        public string Status { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("priorityLevel")]
        public string PriorityLevel { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("dateLogged")]
        public DateTime DateLogged { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("supportingPhotoUrl")]
        public string? SupportingPhotoUrl { get; set; }
    }
}

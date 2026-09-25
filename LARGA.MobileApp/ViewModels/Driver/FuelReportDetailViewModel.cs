using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Firestore;
using LARGA.MobileApp.Services;
using LARGA.SharedCore.Services;

namespace LARGA.MobileApp.ViewModels.Driver;

[QueryProperty(nameof(ReportId), "id")]
public class FuelReportDetailViewModel : BindableObject
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

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    private string _fuelStation = string.Empty;
    public string FuelStation
    {
        get => _fuelStation;
        set { _fuelStation = value; OnPropertyChanged(); }
    }

    private string _orNumber = string.Empty;
    public string OrNumber
    {
        get => _orNumber;
        set { _orNumber = value; OnPropertyChanged(); }
    }

    private string _fuelCostDisplay = string.Empty;
    public string FuelCostDisplay
    {
        get => _fuelCostDisplay;
        set { _fuelCostDisplay = value; OnPropertyChanged(); }
    }

    private string _litersDisplay = string.Empty;
    public string LitersDisplay
    {
        get => _litersDisplay;
        set { _litersDisplay = value; OnPropertyChanged(); }
    }

    private string _odometerDisplay = string.Empty;
    public string OdometerDisplay
    {
        get => _odometerDisplay;
        set { _odometerDisplay = value; OnPropertyChanged(); }
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

    public FuelReportDetailViewModel()
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
                .GetCollection("fuel_logs")
                .GetDocument(ReportId)
                .GetDocumentSnapshotAsync<FuelReportDetailProxy>();

            if (doc?.Data == null) return;

            Status = string.IsNullOrWhiteSpace(doc.Data.VerificationStatus) ? "Pending" : doc.Data.VerificationStatus;
            FuelStation = string.IsNullOrWhiteSpace(doc.Data.FuelStation) ? "N/A" : doc.Data.FuelStation;
            OrNumber = string.IsNullOrWhiteSpace(doc.Data.ORNumber) ? "N/A" : doc.Data.ORNumber;

            var fuelCost = ParseDecimal(doc.Data.FuelCost);
            var liters = ParseDecimal(doc.Data.LitersRefueled);
            var odometer = ParseInt(doc.Data.OdometerReading);
            var timestamp = ParseTimestamp(doc.Data.ReceiptTimestamp);

            FuelCostDisplay = $"₱ {fuelCost:N2}";
            LitersDisplay = $"{liters:N2} L";
            OdometerDisplay = $"{odometer:N0} km";

            if (timestamp.HasValue)
            {
                DateLoggedDisplay = FirestoreDateTimeFix.Apply(timestamp.Value).ToLocalTime().ToString("MMM d, yyyy 'at' h:mm tt");
            }
            else
            {
                DateLoggedDisplay = "Unknown Date";
            }

            if (!string.IsNullOrWhiteSpace(doc.Data.ReceiptImageUrl))
            {
                Photo = ImageSource.FromUri(new Uri(doc.Data.ReceiptImageUrl));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Load Fuel Report Detail Error: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    // The proxy MUST be public and use flexible object? types to prevent deserialization crashes
    public class FuelReportDetailProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("verificationStatus")]
        public string? VerificationStatus { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("fuelStation")]
        public string? FuelStation { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("orNumber")]
        public string? ORNumber { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("fuelCost")]
        public object? FuelCost { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("litersRefueled")]
        public object? LitersRefueled { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("odometerReading")]
        public object? OdometerReading { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("receiptTimestamp")]
        public object? ReceiptTimestamp { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("receiptImageUrl")]
        public string? ReceiptImageUrl { get; set; }
    }

    // Safety Parsing Methods
    private static decimal ParseDecimal(object? value)
    {
        if (value == null) return 0m;
        if (value is decimal d) return d;
        if (value is double dbl) return Convert.ToDecimal(dbl);
        if (value is float f) return Convert.ToDecimal(f);
        if (value is long l) return l;
        if (value is int i) return i;
        if (decimal.TryParse(value.ToString(), out var parsed)) return parsed;
        return 0m;
    }

    private static int ParseInt(object? value)
    {
        if (value == null) return 0;
        if (value is int i) return i;
        if (value is long l) return Convert.ToInt32(l);
        if (value is double d) return Convert.ToInt32(d);
        if (int.TryParse(value.ToString(), out var parsed)) return parsed;
        return 0;
    }

    private static DateTime? ParseTimestamp(object? value)
    {
        if (value is null) return null;
        if (value is DateTime dateTime) return dateTime;
        if (DateTime.TryParse(value.ToString(), out var parsed)) return parsed;
        return null;
    }
}
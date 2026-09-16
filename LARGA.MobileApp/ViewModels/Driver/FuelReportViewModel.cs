using System;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using LARGA.MobileApp.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using Plugin.Firebase.Firestore;
using Plugin.Firebase.Storage;

namespace LARGA.MobileApp.ViewModels.Driver;

public class FuelReportViewModel : BindableObject
{
    private const string PendingPrefix = "FuelReport.Pending.";
    private const string PendingCostKey = PendingPrefix + "Cost";
    private const string PendingQuantityKey = PendingPrefix + "Quantity";
    private const string PendingGasolineNameKey = PendingPrefix + "GasolineName";
    private const string PendingOdometerKey = PendingPrefix + "Odometer";
    private const string PendingSelectedDateKey = PendingPrefix + "SelectedDate";
    private const string PendingReceiptDateTextKey = PendingPrefix + "ReceiptDateText";
    private const string PendingReceiptPathKey = PendingPrefix + "ReceiptPath";
    private const string PendingOdometerPathKey = PendingPrefix + "OdometerPath";

    private readonly IOcrService _ocrService;

    private string _cost = string.Empty;
    public string Cost { get => _cost; set { _cost = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSubmit)); } }

    private string _quantity = string.Empty;
    public string Quantity { get => _quantity; set { _quantity = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSubmit)); } }

    private string _gasolineName = string.Empty;
    public string GasolineName { get => _gasolineName; set { _gasolineName = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSubmit)); } }

    private DateTime? _selectedDate;
    public DateTime? SelectedDate
    {
        get => _selectedDate;
        set
        {
            _selectedDate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedDateDisplay));
            OnPropertyChanged(nameof(CanSubmit));
        }
    }
    public string SelectedDateDisplay => SelectedDate?.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture) ?? "--";

    private string _receiptDateText = string.Empty;
    public string ReceiptDateText
    {
        get => _receiptDateText;
        set
        {
            _receiptDateText = value;
            OnPropertyChanged();

            if (TryParseReceiptDateInput(value, out var parsedDate))
            {
                SelectedDate = parsedDate.Date;
            }
            else
            {
                SelectedDate = null;
            }
        }
    }

    private bool _isReceiptParsingUncertain;
    public bool IsReceiptParsingUncertain
    {
        get => _isReceiptParsingUncertain;
        set { _isReceiptParsingUncertain = value; OnPropertyChanged(); }
    }

    private string _receiptParsingWarning = string.Empty;
    public string ReceiptParsingWarning
    {
        get => _receiptParsingWarning;
        set { _receiptParsingWarning = value; OnPropertyChanged(); }
    }

    private bool _isCostEditable;
    public bool IsCostEditable { get => _isCostEditable; set { _isCostEditable = value; OnPropertyChanged(); } }

    private bool _isQuantityEditable;
    public bool IsQuantityEditable { get => _isQuantityEditable; set { _isQuantityEditable = value; OnPropertyChanged(); } }

    private bool _isGasolineNameEditable;
    public bool IsGasolineNameEditable { get => _isGasolineNameEditable; set { _isGasolineNameEditable = value; OnPropertyChanged(); } }

    private bool _isReceiptDateEditable;
    public bool IsReceiptDateEditable { get => _isReceiptDateEditable; set { _isReceiptDateEditable = value; OnPropertyChanged(); } }

    private string _odometer = string.Empty;
    public string Odometer { get => _odometer; set { _odometer = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsOdometerScanned)); OnPropertyChanged(nameof(CanSubmit)); } }
    public bool IsOdometerScanned => !string.IsNullOrWhiteSpace(Odometer);

    private ImageSource? _receiptPhoto;
    public ImageSource? ReceiptPhoto { get => _receiptPhoto; set { _receiptPhoto = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasPhoto)); } }
    public bool HasPhoto => ReceiptPhoto != null;

    // Holds raw bytes in memory so upload can be retried without forcing a rescan.
    private byte[]? _receiptBytes;
    private byte[]? _odometerPhotoBytes;
    private string? _receiptTempPath;
    private string? _odometerTempPath;

    private bool _isSubmitting;
    public bool IsSubmitting { get => _isSubmitting; set { _isSubmitting = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSubmit)); } }

    public bool CanSubmit =>
        !IsSubmitting &&
        !string.IsNullOrWhiteSpace(Cost) &&
        !string.IsNullOrWhiteSpace(Quantity) &&
        !string.IsNullOrWhiteSpace(GasolineName) &&
        SelectedDate.HasValue &&
        !string.IsNullOrWhiteSpace(Odometer) &&
        HasPhoto &&
        _receiptBytes != null &&
        _odometerPhotoBytes != null;

    public ICommand ScanOdometerCommand { get; }
    public ICommand ScanReceiptCommand { get; }
    public ICommand RedoReceiptCommand { get; }
    public ICommand SubmitReportCommand { get; }

    public FuelReportViewModel(IOcrService ocrService)
    {
        _ocrService = ocrService;

        WeakReferenceMessenger.Default.Register<FuelReportViewModel, OdometerScannedData, string>(this, "OdometerScanned", (r, data) =>
        {
            Odometer = data.OdometerText;
            _odometerPhotoBytes = data.PhotoBytes;
            _odometerTempPath = SaveImageToTempFile(data.PhotoBytes, "odometer");
            OnPropertyChanged(nameof(CanSubmit));
        });

        WeakReferenceMessenger.Default.Register<FuelReportViewModel, ReceiptExtractedData>(this, (r, data) =>
        {
            Cost = data.Amount;
            Quantity = data.Quantity;
            GasolineName = data.Vendor;
            SelectedDate = data.ReceiptDate;
            ReceiptDateText = data.ReceiptDate?.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture) ?? string.Empty;
            IsReceiptParsingUncertain = data.ParsingUncertain;
            ReceiptParsingWarning = data.ParsingWarning;
            IsCostEditable = data.IsCostUncertain || IsZeroOrEmpty(Cost);
            IsQuantityEditable = data.IsQuantityUncertain || IsZeroOrEmpty(Quantity);
            IsGasolineNameEditable = data.IsVendorUncertain || string.IsNullOrWhiteSpace(GasolineName) || GasolineName.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase);
            IsReceiptDateEditable = data.IsDateUncertain || !SelectedDate.HasValue;

            _receiptBytes = data.PhotoBytes;
            _receiptTempPath = SaveImageToTempFile(data.PhotoBytes, "receipt");
            ReceiptPhoto = ImageSource.FromStream(() => new MemoryStream(data.PhotoBytes));
            OnPropertyChanged(nameof(CanSubmit));
        });

        TryLoadPendingDraft();

        ScanOdometerCommand = new Command(async () => await ScanOdometerAsync());
        ScanReceiptCommand = new Command(async () => await Application.Current.MainPage.Navigation.PushModalAsync(new Views.Driver.ScanFuelReceiptPage()));
        RedoReceiptCommand = new Command(async () => await Application.Current.MainPage.Navigation.PushModalAsync(new Views.Driver.ScanFuelReceiptPage()));

        SubmitReportCommand = new Command(async () =>
        {
            if (!CanSubmit)
            {
                await Shell.Current.DisplayAlert("Required", "Please scan odometer and receipt, then verify all fields.", "OK");
                return;
            }

            if (IsSubmitting) return; // Prevent double taps
            IsSubmitting = true;

            var currentUser = Plugin.Firebase.Auth.CrossFirebaseAuth.Current.CurrentUser;
            if (currentUser == null)
            {
                IsSubmitting = false;
                return;
            }

            try
            {
                if (_receiptBytes == null || _odometerPhotoBytes == null)
                {
                    await Shell.Current.DisplayAlert("Required", "Images are missing. Please redo the scans.", "OK");
                    return;
                }

                if (!SelectedDate.HasValue)
                {
                    await Shell.Current.DisplayAlert("Invalid date", "Please correct the receipt date before submitting.", "OK");
                    return;
                }

                _receiptTempPath ??= SaveImageToTempFile(_receiptBytes, "receipt");
                _odometerTempPath ??= SaveImageToTempFile(_odometerPhotoBytes, "odometer");

                var shiftId = Preferences.Get("CurrentShiftId", "UNKNOWN_SHIFT");
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var baseStoragePath = $"fuel_reports/{currentUser.Uid}/{shiftId}/{timestamp}";

                var receiptImageUrl = await UploadImageAsync(_receiptTempPath, $"{baseStoragePath}/receipt.jpg");
                var odometerPhotoUrl = await UploadImageAsync(_odometerTempPath, $"{baseStoragePath}/odometer.jpg");

                var fuelData = new Dictionary<object, object>
                {
                    { "driverId", currentUser.Uid },
                    { "shiftId", shiftId },
                    { "fuelCost", ParseDecimal(Cost) },
                    { "litersRefueled", ParseDecimal(Quantity) },
                    { "fuelStation", GasolineName },
                    { "receiptTimestamp", SelectedDate?.ToUniversalTime() },
                    { "odometerReading", int.TryParse(Odometer.Replace(",", ""), out var odoVal) ? odoVal : 0 },
                    { "verificationStatus", "Pending" },
                    { "receiptImageUrl", receiptImageUrl },
                    { "odometerPhotoUrl", odometerPhotoUrl }
                };

                await CrossFirebaseFirestore.Current
                    .GetCollection("fuel_reports")
                    .AddDocumentAsync(fuelData);

                ClearPendingDraft();
                DeleteTempFile(_receiptTempPath);
                DeleteTempFile(_odometerTempPath);
                _receiptTempPath = null;
                _odometerTempPath = null;

                await Shell.Current.DisplayAlert("Success", "Fuel report submitted for verification.", "OK");
                await Shell.Current.GoToAsync("..");
            }
            catch (Exception ex)
            {
                SavePendingDraft();
                System.Diagnostics.Debug.WriteLine($"Fuel Submit Error: {ex.Message}");
                await Shell.Current.DisplayAlert("Saved locally", "Upload failed. Your draft was kept on device. Tap Submit again to retry.", "OK");
            }
            finally
            {
                IsSubmitting = false;
            }
        });
    }

    private async Task ScanOdometerAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted) return;
        }

        if (MediaPicker.Default.IsCaptureSupported)
        {
            var photo = await MediaPicker.Default.CapturePhotoAsync();

            if (photo != null)
            {
                using var stream = await photo.OpenReadAsync();
                using var memoryStream = new MemoryStream();
                await stream.CopyToAsync(memoryStream);
                byte[] imageBytes = memoryStream.ToArray();

                await Application.Current.MainPage.Navigation.PushModalAsync(
                    new Views.Driver.OdometerScanPage(_ocrService, imageBytes));
            }
        }
    }

    private static decimal ParseDecimal(string value)
    {
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var invariantParsed))
            return invariantParsed;

        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out var currentParsed))
            return currentParsed;

        return 0m;
    }

    private static string SaveImageToTempFile(byte[] bytes, string prefix)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, $"{prefix}_{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void DeleteTempFile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static async Task<string> UploadImageAsync(string? localPath, string remotePath)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            throw new FileNotFoundException("Local image file missing", localPath);
        }

        var storageRef = CrossFirebaseStorage.Current.GetRootReference().GetChild(remotePath);
        await storageRef.PutFile(localPath, null).AwaitAsync();
        return await storageRef.GetDownloadUrlAsync();
    }

    private void SavePendingDraft()
    {
        Preferences.Set(PendingCostKey, Cost);
        Preferences.Set(PendingQuantityKey, Quantity);
        Preferences.Set(PendingGasolineNameKey, GasolineName);
        Preferences.Set(PendingOdometerKey, Odometer);
        Preferences.Set(PendingSelectedDateKey, SelectedDate?.ToString("O") ?? string.Empty);
        Preferences.Set(PendingReceiptDateTextKey, ReceiptDateText);
        Preferences.Set(PendingReceiptPathKey, _receiptTempPath ?? string.Empty);
        Preferences.Set(PendingOdometerPathKey, _odometerTempPath ?? string.Empty);
    }

    private void TryLoadPendingDraft()
    {
        var hasAnyDraft =
            Preferences.ContainsKey(PendingCostKey) ||
            Preferences.ContainsKey(PendingQuantityKey) ||
            Preferences.ContainsKey(PendingGasolineNameKey) ||
            Preferences.ContainsKey(PendingOdometerKey);

        if (!hasAnyDraft)
        {
            return;
        }

        Cost = Preferences.Get(PendingCostKey, Cost);
        Quantity = Preferences.Get(PendingQuantityKey, Quantity);
        GasolineName = Preferences.Get(PendingGasolineNameKey, GasolineName);
        Odometer = Preferences.Get(PendingOdometerKey, Odometer);

        if (DateTime.TryParse(Preferences.Get(PendingSelectedDateKey, string.Empty), null, DateTimeStyles.RoundtripKind, out var parsedDate))
        {
            SelectedDate = parsedDate;
        }

        ReceiptDateText = Preferences.Get(PendingReceiptDateTextKey, SelectedDate?.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture) ?? string.Empty);

        if (IsZeroOrEmpty(Cost)) IsCostEditable = true;
        if (IsZeroOrEmpty(Quantity)) IsQuantityEditable = true;
        if (string.IsNullOrWhiteSpace(GasolineName) || GasolineName.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)) IsGasolineNameEditable = true;
        if (!SelectedDate.HasValue) IsReceiptDateEditable = true;

        _receiptTempPath = Preferences.Get(PendingReceiptPathKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(_receiptTempPath) && File.Exists(_receiptTempPath))
        {
            _receiptBytes = File.ReadAllBytes(_receiptTempPath);
            ReceiptPhoto = ImageSource.FromFile(_receiptTempPath);
        }

        _odometerTempPath = Preferences.Get(PendingOdometerPathKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(_odometerTempPath) && File.Exists(_odometerTempPath))
        {
            _odometerPhotoBytes = File.ReadAllBytes(_odometerTempPath);
        }

        OnPropertyChanged(nameof(CanSubmit));
    }

    private static void ClearPendingDraft()
    {
        Preferences.Remove(PendingCostKey);
        Preferences.Remove(PendingQuantityKey);
        Preferences.Remove(PendingGasolineNameKey);
        Preferences.Remove(PendingOdometerKey);
        Preferences.Remove(PendingSelectedDateKey);
        Preferences.Remove(PendingReceiptDateTextKey);
        Preferences.Remove(PendingReceiptPathKey);
        Preferences.Remove(PendingOdometerPathKey);
    }

    private static bool TryParseReceiptDateInput(string? input, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var formats = new[]
        {
            "MMM dd, yyyy", "MMMM dd, yyyy", "MMM d, yyyy", "MMMM d, yyyy",
            "yyyy-MM-dd", "yyyy/M/d", "MM/dd/yyyy", "M/d/yyyy", "dd/MM/yyyy", "d/M/yyyy",
            "MM-dd-yyyy", "dd-MM-yyyy"
        };

        return DateTime.TryParseExact(input.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date)
               || DateTime.TryParse(input.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static bool IsZeroOrEmpty(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var cleaned = value.Trim();
        if (cleaned == "--") return true;
        if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return parsed == 0m;
        if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.CurrentCulture, out parsed))
            return parsed == 0m;
        return false;
    }
}

public class ReceiptExtractedData
{
    public string Amount { get; set; } = string.Empty;
    public string Quantity { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public DateTime? ReceiptDate { get; set; }
    public bool ParsingUncertain { get; set; }
    public string ParsingWarning { get; set; } = string.Empty;
    public bool IsCostUncertain { get; set; }
    public bool IsQuantityUncertain { get; set; }
    public bool IsVendorUncertain { get; set; }
    public bool IsDateUncertain { get; set; }
    public byte[] PhotoBytes { get; set; } = Array.Empty<byte>();
}

public class OdometerScannedData
{
    public string OdometerText { get; set; } = string.Empty;
    public byte[] PhotoBytes { get; set; } = Array.Empty<byte>();
}

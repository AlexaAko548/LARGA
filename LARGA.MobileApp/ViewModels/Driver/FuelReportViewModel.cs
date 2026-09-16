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
    private const string PendingFuelStationKey = PendingPrefix + "FuelStation";
    private const string PendingOdometerKey = PendingPrefix + "Odometer";
    private const string PendingSelectedDateKey = PendingPrefix + "SelectedDate";
    private const string PendingReceiptDateTextKey = PendingPrefix + "ReceiptDateText";
    private const string PendingReceiptPathKey = PendingPrefix + "ReceiptPath";
    private const string PendingOdometerPathKey = PendingPrefix + "OdometerPath";
    private const string PendingCostEditedKey = PendingPrefix + "CostEdited";
    private const string PendingQuantityEditedKey = PendingPrefix + "QuantityEdited";
    private const string PendingFuelStationEditedKey = PendingPrefix + "FuelStationEdited";
    private const string PendingDateEditedKey = PendingPrefix + "DateEdited";

    private readonly IOcrService _ocrService;

    private string _cost = string.Empty;
    public string Cost
    {
        get => _cost;
        set
        {
            _cost = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSubmit));

            if (!_isApplyingScanResult)
            {
                IsCostManuallyEdited = _costWasUncertain && !NumericEquivalent(_cost, _originalCostValue);
            }
        }
    }

    private string _quantity = string.Empty;
    public string Quantity
    {
        get => _quantity;
        set
        {
            _quantity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSubmit));

            if (!_isApplyingScanResult)
            {
                IsQuantityManuallyEdited = _quantityWasUncertain && !NumericEquivalent(_quantity, _originalQuantityValue);
            }
        }
    }

    private string _fuelStation = string.Empty;
    public string FuelStation
    {
        get => _fuelStation;
        set
        {
            _fuelStation = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSubmit));

            if (!_isApplyingScanResult)
            {
                IsFuelStationManuallyEdited = _fuelStationWasUncertain &&
                    !string.Equals(NormalizeText(_fuelStation), NormalizeText(_originalFuelStationValue), StringComparison.Ordinal);
            }
        }
    }

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

            if (!_isApplyingScanResult)
            {
                IsReceiptDateManuallyEdited = _dateWasUncertain && !DateEquivalent(_receiptDateText, _originalReceiptDateText);
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

    private bool _isFuelStationEditable;
    public bool IsFuelStationEditable { get => _isFuelStationEditable; set { _isFuelStationEditable = value; OnPropertyChanged(); } }

    private bool _isReceiptDateEditable;
    public bool IsReceiptDateEditable { get => _isReceiptDateEditable; set { _isReceiptDateEditable = value; OnPropertyChanged(); } }

    private bool _isCostManuallyEdited;
    public bool IsCostManuallyEdited { get => _isCostManuallyEdited; set { _isCostManuallyEdited = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsAnyFieldManuallyEdited)); } }

    private bool _isQuantityManuallyEdited;
    public bool IsQuantityManuallyEdited { get => _isQuantityManuallyEdited; set { _isQuantityManuallyEdited = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsAnyFieldManuallyEdited)); } }

    private bool _isFuelStationManuallyEdited;
    public bool IsFuelStationManuallyEdited { get => _isFuelStationManuallyEdited; set { _isFuelStationManuallyEdited = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsAnyFieldManuallyEdited)); } }

    private bool _isReceiptDateManuallyEdited;
    public bool IsReceiptDateManuallyEdited { get => _isReceiptDateManuallyEdited; set { _isReceiptDateManuallyEdited = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsAnyFieldManuallyEdited)); } }

    public bool IsAnyFieldManuallyEdited => IsCostManuallyEdited || IsQuantityManuallyEdited || IsFuelStationManuallyEdited || IsReceiptDateManuallyEdited;

    private bool _isApplyingScanResult;
    private bool _costWasUncertain;
    private bool _quantityWasUncertain;
    private bool _fuelStationWasUncertain;
    private bool _dateWasUncertain;
    private string _originalCostValue = string.Empty;
    private string _originalQuantityValue = string.Empty;
    private string _originalFuelStationValue = string.Empty;
    private string _originalReceiptDateText = string.Empty;

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
        !string.IsNullOrWhiteSpace(FuelStation) &&
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
            _isApplyingScanResult = true;
            Cost = data.Amount;
            Quantity = data.Quantity;
            FuelStation = data.Vendor;
            SelectedDate = data.ReceiptDate;
            ReceiptDateText = data.ReceiptDate?.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture) ?? string.Empty;
            IsReceiptParsingUncertain = data.ParsingUncertain;
            ReceiptParsingWarning = data.ParsingWarning;
            IsCostEditable = data.IsCostUncertain || IsZeroOrEmpty(Cost);
            IsQuantityEditable = data.IsQuantityUncertain || IsZeroOrEmpty(Quantity);
            IsFuelStationEditable = data.IsVendorUncertain || string.IsNullOrWhiteSpace(FuelStation) || FuelStation.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase);
            IsReceiptDateEditable = data.IsDateUncertain || !SelectedDate.HasValue;

            _costWasUncertain = IsCostEditable;
            _quantityWasUncertain = IsQuantityEditable;
            _fuelStationWasUncertain = IsFuelStationEditable;
            _dateWasUncertain = IsReceiptDateEditable;

            _originalCostValue = Cost;
            _originalQuantityValue = Quantity;
            _originalFuelStationValue = FuelStation;
            _originalReceiptDateText = ReceiptDateText;

            IsCostManuallyEdited = false;
            IsQuantityManuallyEdited = false;
            IsFuelStationManuallyEdited = false;
            IsReceiptDateManuallyEdited = false;
            _isApplyingScanResult = false;

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
                var baseStoragePath = $"fuel_logs/{currentUser.Uid}/{shiftId}/{timestamp}";

                var receiptImageUrl = await UploadImageAsync(_receiptTempPath, $"{baseStoragePath}/receipt.jpg");
                var odometerPhotoUrl = await UploadImageAsync(_odometerTempPath, $"{baseStoragePath}/odometer.jpg");

                var fuelData = new Dictionary<object, object>
                {
                    { "driverId", currentUser.Uid },
                    { "shiftId", shiftId },
                    { "fuelCost", ParseDecimal(Cost) },
                    { "litersRefueled", ParseDecimal(Quantity) },
                    { "fuelStation", FuelStation },
                    { "receiptTimestamp", NormalizeReceiptTimestamp(SelectedDate.Value) },
                    { "odometerReading", int.TryParse(Odometer.Replace(",", ""), out var odoVal) ? odoVal : 0 },
                    { "verificationStatus", "Pending" },
                    { "receiptImageUrl", receiptImageUrl },
                    { "odometerPhotoUrl", odometerPhotoUrl },
                    { "isCostManuallyEdited", IsCostManuallyEdited },
                    { "isQuantityManuallyEdited", IsQuantityManuallyEdited },
                    { "isFuelStationManuallyEdited", IsFuelStationManuallyEdited },
                    { "isReceiptDateManuallyEdited", IsReceiptDateManuallyEdited },
                    { "isAnyFieldManuallyEdited", IsAnyFieldManuallyEdited }
                };

                await CrossFirebaseFirestore.Current
                    .GetCollection("fuel_logs")
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

    private static DateTime NormalizeReceiptTimestamp(DateTime selectedDate)
    {
        return DateTime.SpecifyKind(selectedDate.Date, DateTimeKind.Utc);
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
        Preferences.Set(PendingFuelStationKey, FuelStation);
        Preferences.Set(PendingOdometerKey, Odometer);
        Preferences.Set(PendingSelectedDateKey, SelectedDate?.ToString("O") ?? string.Empty);
        Preferences.Set(PendingReceiptDateTextKey, ReceiptDateText);
        Preferences.Set(PendingReceiptPathKey, _receiptTempPath ?? string.Empty);
        Preferences.Set(PendingOdometerPathKey, _odometerTempPath ?? string.Empty);
        Preferences.Set(PendingCostEditedKey, IsCostManuallyEdited);
        Preferences.Set(PendingQuantityEditedKey, IsQuantityManuallyEdited);
        Preferences.Set(PendingFuelStationEditedKey, IsFuelStationManuallyEdited);
        Preferences.Set(PendingDateEditedKey, IsReceiptDateManuallyEdited);
    }

    private void TryLoadPendingDraft()
    {
        var hasAnyDraft =
            Preferences.ContainsKey(PendingCostKey) ||
            Preferences.ContainsKey(PendingQuantityKey) ||
            Preferences.ContainsKey(PendingFuelStationKey) ||
            Preferences.ContainsKey(PendingOdometerKey);

        if (!hasAnyDraft)
        {
            return;
        }

        Cost = Preferences.Get(PendingCostKey, Cost);
        Quantity = Preferences.Get(PendingQuantityKey, Quantity);
        FuelStation = Preferences.Get(PendingFuelStationKey, FuelStation);
        Odometer = Preferences.Get(PendingOdometerKey, Odometer);

        if (DateTime.TryParse(Preferences.Get(PendingSelectedDateKey, string.Empty), null, DateTimeStyles.RoundtripKind, out var parsedDate))
        {
            SelectedDate = parsedDate;
        }

        ReceiptDateText = Preferences.Get(PendingReceiptDateTextKey, SelectedDate?.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture) ?? string.Empty);

        if (IsZeroOrEmpty(Cost)) IsCostEditable = true;
        if (IsZeroOrEmpty(Quantity)) IsQuantityEditable = true;
        if (string.IsNullOrWhiteSpace(FuelStation) || FuelStation.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)) IsFuelStationEditable = true;
        if (!SelectedDate.HasValue) IsReceiptDateEditable = true;

        _costWasUncertain = IsCostEditable;
        _quantityWasUncertain = IsQuantityEditable;
        _fuelStationWasUncertain = IsFuelStationEditable;
        _dateWasUncertain = IsReceiptDateEditable;
        _originalCostValue = Cost;
        _originalQuantityValue = Quantity;
        _originalFuelStationValue = FuelStation;
        _originalReceiptDateText = ReceiptDateText;

        IsCostManuallyEdited = Preferences.Get(PendingCostEditedKey, false);
        IsQuantityManuallyEdited = Preferences.Get(PendingQuantityEditedKey, false);
        IsFuelStationManuallyEdited = Preferences.Get(PendingFuelStationEditedKey, false);
        IsReceiptDateManuallyEdited = Preferences.Get(PendingDateEditedKey, false);

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
        Preferences.Remove(PendingFuelStationKey);
        Preferences.Remove(PendingOdometerKey);
        Preferences.Remove(PendingSelectedDateKey);
        Preferences.Remove(PendingReceiptDateTextKey);
        Preferences.Remove(PendingReceiptPathKey);
        Preferences.Remove(PendingOdometerPathKey);
        Preferences.Remove(PendingCostEditedKey);
        Preferences.Remove(PendingQuantityEditedKey);
        Preferences.Remove(PendingFuelStationEditedKey);
        Preferences.Remove(PendingDateEditedKey);
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

    private static bool NumericEquivalent(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
            return true;

        if (decimal.TryParse(left, NumberStyles.Any, CultureInfo.InvariantCulture, out var leftVal) &&
            decimal.TryParse(right, NumberStyles.Any, CultureInfo.InvariantCulture, out var rightVal))
            return leftVal == rightVal;

        if (decimal.TryParse(left, NumberStyles.Any, CultureInfo.CurrentCulture, out leftVal) &&
            decimal.TryParse(right, NumberStyles.Any, CultureInfo.CurrentCulture, out rightVal))
            return leftVal == rightVal;

        return string.Equals(NormalizeText(left), NormalizeText(right), StringComparison.Ordinal);
    }

    private static bool DateEquivalent(string? left, string? right)
    {
        if (TryParseReceiptDateInput(left, out var leftDate) && TryParseReceiptDateInput(right, out var rightDate))
            return leftDate.Date == rightDate.Date;

        return string.Equals(NormalizeText(left), NormalizeText(right), StringComparison.Ordinal);
    }

    private static string NormalizeText(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
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

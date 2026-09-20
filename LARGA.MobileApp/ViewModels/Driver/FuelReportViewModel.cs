using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using LARGA.MobileApp.Services;
using LARGA.SharedCore.Services;
using LARGA.Shared.Models.Entities;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
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
    private readonly IFuelService _fuelService;

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

    // THE FIX: Retained only the file paths. byte[] arrays have been deleted.
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
        !string.IsNullOrWhiteSpace(_receiptTempPath) &&
        !string.IsNullOrWhiteSpace(_odometerTempPath);

    public ICommand ScanOdometerCommand { get; }
    public ICommand ScanReceiptCommand { get; }
    public ICommand RedoReceiptCommand { get; }
    public ICommand SubmitReportCommand { get; }

    public FuelReportViewModel(IOcrService ocrService, IFuelService fuelService)
    {
        _ocrService = ocrService;
        _fuelService = fuelService;

        WeakReferenceMessenger.Default.Register<FuelReportViewModel, OdometerScannedData, string>(this, "OdometerScanned", (r, data) =>
        {
            Odometer = data.OdometerText;
            _odometerTempPath = data.PhotoFilePath; // THE FIX: Assign path directly
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

            _receiptTempPath = data.PhotoFilePath; // THE FIX: Assign path directly
            ReceiptPhoto = null;
            ReceiptPhoto = ImageSource.FromFile(data.PhotoFilePath); // THE FIX: Bind from file
            OnPropertyChanged(nameof(CanSubmit));
        });

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

            if (IsSubmitting) return;
            IsSubmitting = true;

            var currentUser = Plugin.Firebase.Auth.CrossFirebaseAuth.Current.CurrentUser;
            if (currentUser == null)
            {
                IsSubmitting = false;
                return;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(_receiptTempPath) || string.IsNullOrWhiteSpace(_odometerTempPath))
                {
                    await Shell.Current.DisplayAlert("Required", "Images are missing. Please redo the scans.", "OK");
                    return;
                }

                if (!SelectedDate.HasValue)
                {
                    await Shell.Current.DisplayAlert("Invalid date", "Please correct the receipt date before submitting.", "OK");
                    return;
                }

                var shiftId = Preferences.Get("CurrentShiftId", "UNKNOWN_SHIFT");
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var baseStoragePath = $"fuel_logs/{currentUser.Uid}/{shiftId}/{timestamp}";

                var receiptImageUrl = string.Empty;
                var odometerPhotoUrl = string.Empty;

                try
                {
                    // THE FIX: Upload using file paths
                    receiptImageUrl = await UploadImageAsync(_receiptTempPath, $"{baseStoragePath}/receipt.jpg");
                    odometerPhotoUrl = await UploadImageAsync(_odometerTempPath, $"{baseStoragePath}/odometer.jpg");
                }
                catch (Exception uploadEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Fuel image upload error: {uploadEx.Message}");
                    IsSubmitting = false;
                    await Shell.Current.DisplayAlert("Upload Failed", "Unable to upload photos to cloud storage. Please check your internet connection and try again.", "OK");
                    return;
                }

                var fuelRecord = new FuelLog
                {
                    DriverId = currentUser.Uid,
                    ShiftId = shiftId,
                    FuelCost = ParseDecimal(Cost),
                    LitersRefueled = ParseDecimal(Quantity),
                    FuelStation = FuelStation,
                    ReceiptTimestamp = NormalizeReceiptTimestamp(SelectedDate.Value),
                    OdometerReading = int.TryParse(Odometer.Replace(",", ""), out var odoVal) ? odoVal : 0,
                    VerificationStatus = FuelVerificationStatus.Pending,
                    ReceiptImageUrl = receiptImageUrl,
                    OdometerPhotoUrl = odometerPhotoUrl,
                    IsCostManuallyEdited = IsCostManuallyEdited,
                    IsQuantityManuallyEdited = IsQuantityManuallyEdited,
                    IsFuelStationManuallyEdited = IsFuelStationManuallyEdited,
                    IsReceiptDateManuallyEdited = IsReceiptDateManuallyEdited,
                    IsAnyFieldManuallyEdited = IsAnyFieldManuallyEdited
                };

                var recordId = string.Empty;

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    recordId = await _fuelService.SubmitFuelReportAsync(fuelRecord);
                });

                if (string.IsNullOrEmpty(recordId))
                {
                    await Shell.Current.DisplayAlert("Submission failed", "Unable to submit to Firebase right now. Please try again.", "OK");
                    return;
                }

                ClearPendingDraft();
                DeleteTempFile(_receiptTempPath);
                DeleteTempFile(_odometerTempPath);
                _receiptTempPath = null;
                _odometerTempPath = null;

                WeakReferenceMessenger.Default.Send(new FuelReportSubmittedMessage());
                ResetState();

                await Shell.Current.DisplayAlert("Success", "Fuel report submitted for verification.", "OK");
                await Shell.Current.GoToAsync("..");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Fuel Submit Error: {ex.Message}");
                await Shell.Current.DisplayAlert("Submission failed", "An unexpected error occurred. Please try again.", "OK");
            }
            finally
            {
                IsSubmitting = false;
            }
        });
    }

    public void ResetState()
    {
        _isApplyingScanResult = true;

        Cost = string.Empty;
        Quantity = string.Empty;
        FuelStation = string.Empty;
        SelectedDate = null;
        ReceiptDateText = string.Empty;
        Odometer = string.Empty;
        ReceiptPhoto = null;

        IsReceiptParsingUncertain = false;
        ReceiptParsingWarning = string.Empty;

        IsCostEditable = false;
        IsQuantityEditable = false;
        IsFuelStationEditable = false;
        IsReceiptDateEditable = false;

        IsCostManuallyEdited = false;
        IsQuantityManuallyEdited = false;
        IsFuelStationManuallyEdited = false;
        IsReceiptDateManuallyEdited = false;

        _costWasUncertain = false;
        _quantityWasUncertain = false;
        _fuelStationWasUncertain = false;
        _dateWasUncertain = false;
        _originalCostValue = string.Empty;
        _originalQuantityValue = string.Empty;
        _originalFuelStationValue = string.Empty;
        _originalReceiptDateText = string.Empty;

        DeleteTempFile(_receiptTempPath);
        DeleteTempFile(_odometerTempPath);
        _receiptTempPath = null;
        _odometerTempPath = null;

        ClearPendingDraft();
        _isApplyingScanResult = false;
        OnPropertyChanged(nameof(CanSubmit));
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
                // THE FIX: Add Guid.NewGuid() to prevent file lock crashes on rescans
                string localFilePath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid():N}_{photo.FileName}");

                using (var sourceStream = await photo.OpenReadAsync())
                using (var localFileStream = File.OpenWrite(localFilePath))
                {
                    await sourceStream.CopyToAsync(localFileStream);
                }

                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await Application.Current.MainPage.Navigation.PushModalAsync(
                        new LARGA.MobileApp.Views.Driver.OdometerScanPage(_ocrService, localFilePath));
                });
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

    private static void DeleteTempFile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // THE FIX: Modify upload method to stream from file path directly
    private static async Task<string> UploadImageAsync(string localFilePath, string remotePath)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
        {
            throw new ArgumentException("File path is invalid or does not exist", nameof(localFilePath));
        }

        var storageRef = CrossFirebaseStorage.Current.GetRootReference().GetChild(remotePath);

        // PutFile reads the bytes from disk in chunks rather than loading it all into memory
        await storageRef.PutFile(localFilePath).AwaitAsync();
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
            ReceiptPhoto = ImageSource.FromFile(_receiptTempPath); // THE FIX: Bind from file directly
        }

        _odometerTempPath = Preferences.Get(PendingOdometerPathKey, string.Empty);

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
    // THE FIX: Pass file path instead of bytes
    public string PhotoFilePath { get; set; } = string.Empty;
}

public class OdometerScannedData
{
    public string OdometerText { get; set; } = string.Empty;
    // THE FIX: Pass file path instead of bytes
    public string PhotoFilePath { get; set; } = string.Empty;
}

public sealed class FuelReportSubmittedMessage
{
}

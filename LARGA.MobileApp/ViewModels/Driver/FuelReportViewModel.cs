using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using LARGA.MobileApp.Services;
using LARGA.SharedCore;
using LARGA.SharedCore.Services;
using LARGA.Shared.Models.Entities;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;
using Plugin.Firebase.Storage;

namespace LARGA.MobileApp.ViewModels.Driver;

public class FuelReportViewModel : BindableObject
{
    // FUEL-015: the unsent report, kept across an app restart (one JSON value). The photos are
    // moved out of the cache folder into DraftPhotoFolder so Android can't clear them meanwhile.
    private const string DraftKey = "FuelReport.Draft";
    private const string LegacyPendingPrefix = "FuelReport.Pending.";
    private static readonly string[] LegacyPendingKeys =
    {
        "Cost", "Quantity", "FuelStation", "Odometer", "SelectedDate", "ReceiptDateText", "ReceiptPath",
        "OdometerPath", "CostEdited", "QuantityEdited", "FuelStationEdited", "DateEdited", "OrNumber", "OrNumberEdited",
    };
    private static string DraftPhotoFolder => Path.Combine(FileSystem.AppDataDirectory, "fuel_draft");

    // A stuck upload (no signal) fails after this instead of Firebase Storage's 10-minute retry window.
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(60);

    // Typing in a field saves the draft once the driver pauses this long, not on every character.
    private static readonly TimeSpan DraftSaveDelay = TimeSpan.FromMilliseconds(600);

    // storage.rules isPhoto(): request.resource.size < 5 MB.
    private const long MaxUploadBytes = 5 * 1024 * 1024;

    private readonly IOcrService _ocrService;
    private readonly IFuelService _fuelService;
    private readonly IShiftManagementService _shiftService;

    // REQ-4.3-3: the shift this report is for - the active shift, or one that ended within
    // FuelShiftRules.PostShiftWindow. Kept with the draft, so a report started during the shift
    // still goes to it after clock-out.
    private string? _reportShiftId;

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
                // FUEL-010: any change from what the scan read is flagged, whichever screen it was made on.
                IsCostManuallyEdited = _hasReceiptScan && !NumericEquivalent(_cost, _originalCostValue);
                ScheduleDraftSave();
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
                IsQuantityManuallyEdited = _hasReceiptScan && !NumericEquivalent(_quantity, _originalQuantityValue);
                ScheduleDraftSave();
            }
        }
    }

    // Never editable: always what the receipt scan read ("UNKNOWN" when it read no Petron/Shell).
    private string _fuelStation = string.Empty;
    public string FuelStation
    {
        get => _fuelStation;
        set
        {
            _fuelStation = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSubmit));
        }
    }

    // Optional: not part of CanSubmit - some receipts only print an SI/invoice number, and the
    // manager can still verify a report without it.
    private string _orNumber = string.Empty;
    public string OrNumber
    {
        get => _orNumber;
        set
        {
            _orNumber = value;
            OnPropertyChanged();

            if (!_isApplyingScanResult)
            {
                IsOrNumberManuallyEdited = _hasReceiptScan &&
                    !string.Equals(NormalizeText(_orNumber), NormalizeText(_originalOrNumberValue), StringComparison.Ordinal);
                ScheduleDraftSave();
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

            // FUEL-012: a typed date has to pass the same check as a scanned one (from 2020, not in
            // the future, at most three years old); anything else leaves the date unset.
            SelectedDate = TryParseReceiptDateInput(value, out var parsedDate) && FuelReceiptTextParser.IsPlausibleReceiptDate(parsedDate)
                ? parsedDate.Date
                : null;

            if (!_isApplyingScanResult)
            {
                IsReceiptDateManuallyEdited = _hasReceiptScan && !DateEquivalent(_receiptDateText, _originalReceiptDateText);
                ScheduleDraftSave();
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
    public bool IsCostEditable { get => _isCostEditable; set { _isCostEditable = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsCostReadOnly)); } }
    public bool IsCostReadOnly => !IsCostEditable;

    private bool _isQuantityEditable;
    public bool IsQuantityEditable { get => _isQuantityEditable; set { _isQuantityEditable = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsQuantityReadOnly)); } }
    public bool IsQuantityReadOnly => !IsQuantityEditable;

    private bool _isFuelStationEditable;
    public bool IsFuelStationEditable { get => _isFuelStationEditable; set { _isFuelStationEditable = value; OnPropertyChanged(); } }

    private bool _isReceiptDateEditable;
    public bool IsReceiptDateEditable { get => _isReceiptDateEditable; set { _isReceiptDateEditable = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsReceiptDateReadOnly)); } }
    public bool IsReceiptDateReadOnly => !IsReceiptDateEditable;

    private bool _isOrNumberEditable;
    public bool IsOrNumberEditable { get => _isOrNumberEditable; set { _isOrNumberEditable = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsOrNumberReadOnly)); } }
    public bool IsOrNumberReadOnly => !IsOrNumberEditable;

    private bool _isCostManuallyEdited;
    public bool IsCostManuallyEdited { get => _isCostManuallyEdited; set { _isCostManuallyEdited = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsAnyFieldManuallyEdited)); } }

    private bool _isQuantityManuallyEdited;
    public bool IsQuantityManuallyEdited { get => _isQuantityManuallyEdited; set { _isQuantityManuallyEdited = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsAnyFieldManuallyEdited)); } }

    private bool _isFuelStationManuallyEdited;
    public bool IsFuelStationManuallyEdited { get => _isFuelStationManuallyEdited; set { _isFuelStationManuallyEdited = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsAnyFieldManuallyEdited)); } }

    private bool _isReceiptDateManuallyEdited;
    public bool IsReceiptDateManuallyEdited { get => _isReceiptDateManuallyEdited; set { _isReceiptDateManuallyEdited = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsAnyFieldManuallyEdited)); } }

    private bool _isOrNumberManuallyEdited;
    public bool IsOrNumberManuallyEdited { get => _isOrNumberManuallyEdited; set { _isOrNumberManuallyEdited = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsAnyFieldManuallyEdited)); } }

    public bool IsAnyFieldManuallyEdited => IsCostManuallyEdited || IsQuantityManuallyEdited || IsFuelStationManuallyEdited || IsReceiptDateManuallyEdited || IsOrNumberManuallyEdited;

    private bool _isApplyingScanResult;
    private bool _hasReceiptScan;
    private string _originalCostValue = string.Empty;
    private string _originalQuantityValue = string.Empty;
    private string _originalFuelStationValue = string.Empty;
    private string _originalReceiptDateText = string.Empty;
    private string _originalOrNumberValue = string.Empty;

    private string _odometer = string.Empty;
    public string Odometer
    {
        get => _odometer;
        set
        {
            _odometer = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOdometerScanned));
            OnPropertyChanged(nameof(CanSubmit));
        }
    }
    public bool IsOdometerScanned => !string.IsNullOrWhiteSpace(Odometer);

    private ImageSource? _receiptPhoto;
    public ImageSource? ReceiptPhoto { get => _receiptPhoto; set { _receiptPhoto = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasPhoto)); } }
    public bool HasPhoto => ReceiptPhoto != null;

    // THE FIX: Retained only the file paths. byte[] arrays have been deleted.
    private string? _receiptTempPath;
    private string? _odometerTempPath;

    private CancellationTokenSource? _draftSaveDelay;

    private bool _isSubmitting;
    public bool IsSubmitting { get => _isSubmitting; set { _isSubmitting = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSubmit)); OnPropertyChanged(nameof(SubmitButtonText)); } }

    public string SubmitButtonText => IsSubmitting ? "Submitting…" : "Submit";

    private bool HasBothScans =>
        !string.IsNullOrWhiteSpace(Odometer) &&
        HasPhoto &&
        !string.IsNullOrWhiteSpace(_receiptTempPath) &&
        !string.IsNullOrWhiteSpace(_odometerTempPath);

    /// <summary>Everything needed to submit (the Submit button's colour); tapping Submit without it
    /// says what's missing.</summary>
    public bool CanSubmit =>
        !IsSubmitting &&
        !string.IsNullOrWhiteSpace(Cost) &&
        !string.IsNullOrWhiteSpace(Quantity) &&
        !string.IsNullOrWhiteSpace(FuelStation) &&
        SelectedDate.HasValue &&
        HasBothScans;

    public ICommand ScanOdometerCommand { get; }
    public ICommand ScanReceiptCommand { get; }
    public ICommand RedoReceiptCommand { get; }
    public ICommand SubmitReportCommand { get; }

    public FuelReportViewModel(IOcrService ocrService, IFuelService fuelService, IShiftManagementService shiftService)
    {
        _ocrService = ocrService;
        _fuelService = fuelService;
        _shiftService = shiftService;

        WeakReferenceMessenger.Default.Register<FuelReportViewModel, OdometerScannedData, string>(this, "OdometerScanned", (r, data) =>
        {
            Odometer = data.OdometerText;
            DeleteReplacedPhoto(_odometerTempPath, data.PhotoFilePath);
            _odometerTempPath = data.PhotoFilePath; // THE FIX: Assign path directly
            OnPropertyChanged(nameof(CanSubmit));
            SavePendingDraft();
        });

        WeakReferenceMessenger.Default.Register<FuelReportViewModel, ReceiptExtractedData>(this, (r, data) =>
        {
            _isApplyingScanResult = true;
            _hasReceiptScan = true;
            Cost = data.Amount;
            Quantity = data.Quantity;
            FuelStation = data.Vendor;
            ReceiptDateText = data.ReceiptDate?.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture) ?? string.Empty;
            OrNumber = data.OrNumber;
            IsReceiptParsingUncertain = data.ParsingUncertain;
            ReceiptParsingWarning = data.ParsingWarning;
            // Values the driver already corrected inline on the scan page count as edited (they
            // differ from what OCR read) and stay editable here.
            bool costEditedInline = data.OriginalAmount is not null && !NumericEquivalent(Cost, data.OriginalAmount);
            bool quantityEditedInline = data.OriginalQuantity is not null && !NumericEquivalent(Quantity, data.OriginalQuantity);
            bool orNumberEditedInline = data.OriginalOrNumber is not null
                && !string.Equals(NormalizeText(OrNumber), NormalizeText(data.OriginalOrNumber), StringComparison.Ordinal);

            IsCostEditable = data.IsCostUncertain || IsZeroOrEmpty(Cost) || costEditedInline;
            IsQuantityEditable = data.IsQuantityUncertain || IsZeroOrEmpty(Quantity) || quantityEditedInline;
            IsFuelStationEditable = false; // the station name is never editable: always what OCR read
            IsReceiptDateEditable = data.IsDateUncertain || !SelectedDate.HasValue;
            IsOrNumberEditable = data.IsOrNumberUncertain || string.IsNullOrWhiteSpace(OrNumber) || orNumberEditedInline;

            // Compared against what OCR read, so an inline correction stays flagged as edited.
            _originalCostValue = data.OriginalAmount ?? Cost;
            _originalQuantityValue = data.OriginalQuantity ?? Quantity;
            _originalFuelStationValue = data.OriginalVendor ?? FuelStation;
            _originalReceiptDateText = ReceiptDateText;
            _originalOrNumberValue = data.OriginalOrNumber ?? OrNumber;

            IsCostManuallyEdited = costEditedInline;
            IsQuantityManuallyEdited = quantityEditedInline;
            IsFuelStationManuallyEdited = false;
            IsReceiptDateManuallyEdited = false;
            IsOrNumberManuallyEdited = orNumberEditedInline;
            _isApplyingScanResult = false;

            DeleteReplacedPhoto(_receiptTempPath, data.PhotoFilePath);
            _receiptTempPath = data.PhotoFilePath; // THE FIX: Assign path directly
            ReceiptPhoto = null;
            ReceiptPhoto = ImageSource.FromFile(data.PhotoFilePath); // THE FIX: Bind from file
            OnPropertyChanged(nameof(CanSubmit));
            SavePendingDraft();
        });

        ScanOdometerCommand = new Command(async () => await ScanOdometerAsync());
        ScanReceiptCommand = new Command(async () => await Application.Current.MainPage.Navigation.PushModalAsync(new Views.Driver.ScanFuelReceiptPage()));
        RedoReceiptCommand = new Command(async () => await Application.Current.MainPage.Navigation.PushModalAsync(new Views.Driver.ScanFuelReceiptPage()));

        SubmitReportCommand = new Command(async () => await SubmitAsync());
    }

    private async Task SubmitAsync()
    {
        if (IsSubmitting) return;

        // FUEL-014 / FUEL-012: the button is always tappable, and says what's missing.
        if (!CanSubmit)
        {
            if (HasBothScans && !SelectedDate.HasValue)
            {
                await Shell.Current.DisplayAlert("Invalid date",
                    "Please correct the receipt date before submitting (e.g. Oct 24, 2026). It can't be in the future or before 2020.", "OK");
            }
            else
            {
                await Shell.Current.DisplayAlert("Required", "Please scan odometer and receipt, then verify all fields.", "OK");
            }
            return;
        }

        IsSubmitting = true;

        var currentUser = Plugin.Firebase.Auth.CrossFirebaseAuth.Current.CurrentUser;
        if (currentUser == null)
        {
            IsSubmitting = false;
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(_receiptTempPath) || string.IsNullOrWhiteSpace(_odometerTempPath)
                || !File.Exists(_receiptTempPath) || !File.Exists(_odometerTempPath))
            {
                await Shell.Current.DisplayAlert("Required", "Images are missing. Please redo the scans.", "OK");
                return;
            }

            // FUEL-015: keep everything on the phone first, so a failure below loses nothing.
            SavePendingDraft();

            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
            {
                await ShowUploadFailedAsync();
                return;
            }

            // REQ-4.3-3 / REQ-5.5-6: never an unlinked fuel log (firestore.rules refuse one too).
            string? shiftId = await _shiftService.GetFuelReportShiftIdAsync(_reportShiftId, LastShiftStore.LocalFuelReportShiftId());
            if (shiftId is null)
            {
                await Shell.Current.DisplayAlert("No shift", FuelShiftRules.NoShiftMessage, "OK");
                return;
            }
            if (shiftId != _reportShiftId)
            {
                _reportShiftId = shiftId;
                SavePendingDraft();
            }

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var baseStoragePath = $"fuel_logs/{currentUser.Uid}/{shiftId}/{timestamp}";

            string receiptImageUrl;
            string odometerPhotoUrl;
            try
            {
                // THE FIX: Upload using file paths
                receiptImageUrl = await UploadImageAsync(_receiptTempPath, $"{baseStoragePath}/receipt.jpg");
                odometerPhotoUrl = await UploadImageAsync(_odometerTempPath, $"{baseStoragePath}/odometer.jpg");
            }
            catch (Exception uploadEx)
            {
                System.Diagnostics.Debug.WriteLine($"Fuel image upload error: {uploadEx}");
                await ShowUploadFailedAsync();
                return;
            }

            // FUEL-004: a receipt from a station other than Petron or Shell still goes in, but
            // Flagged with the reason, so it lands straight in the manager's flagged queue and
            // Alert Center (the same path as any other fuel discrepancy).
            bool stationAllowed = FuelStations.IsAllowed(FuelStation);

            var fuelRecord = new FuelLog
            {
                DriverId = currentUser.Uid,
                ShiftId = shiftId,
                FuelCost = ParseDecimal(Cost),
                LitersRefueled = ParseDecimal(Quantity),
                FuelStation = FuelStation,
                ORNumber = OrNumber?.Trim() ?? string.Empty,
                ReceiptTimestamp = NormalizeReceiptTimestamp(SelectedDate!.Value),
                OdometerReading = int.TryParse(Odometer.Replace(",", ""), out var odoVal) ? odoVal : 0,
                VerificationStatus = stationAllowed ? FuelVerificationStatus.Pending : FuelVerificationStatus.Flagged,
                FuelLogDetails = stationAllowed ? null : FuelStations.NotAllowedReason,
                AutoFlagged = !stationAllowed,
                ReceiptImageUrl = receiptImageUrl,
                OdometerPhotoUrl = odometerPhotoUrl,
                IsCostManuallyEdited = IsCostManuallyEdited,
                IsQuantityManuallyEdited = IsQuantityManuallyEdited,
                IsFuelStationManuallyEdited = IsFuelStationManuallyEdited,
                IsReceiptDateManuallyEdited = IsReceiptDateManuallyEdited,
                IsOrNumberManuallyEdited = IsOrNumberManuallyEdited,
                IsAnyFieldManuallyEdited = IsAnyFieldManuallyEdited
            };

            var recordId = string.Empty;

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                recordId = await _fuelService.SubmitFuelReportAsync(fuelRecord);
            });

            if (string.IsNullOrEmpty(recordId))
            {
                await Shell.Current.DisplayAlert("Submission failed", "Unable to submit to Firebase right now. Your report is saved on this phone - please try again.", "OK");
                return;
            }

            WeakReferenceMessenger.Default.Send(new FuelReportSubmittedMessage());
            ResetState(); // clears the draft and its photos

            await Shell.Current.DisplayAlert("Success", stationAllowed
                ? "Fuel report submitted for verification."
                : $"Fuel report submitted, but flagged for the manager's review: {FuelStations.NotAllowedReason}.", "OK");
            await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Fuel Submit Error: {ex.Message}");
            await Shell.Current.DisplayAlert("Submission failed", "An unexpected error occurred. Your report is saved on this phone - please try again.", "OK");
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private static Task ShowUploadFailedAsync() =>
        Shell.Current.DisplayAlert("Upload Failed",
            "Unable to upload photos to cloud storage. Please check your internet connection and try again. Your report is saved on this phone.", "OK");

    public void ResetState()
    {
        _isApplyingScanResult = true;

        Cost = string.Empty;
        Quantity = string.Empty;
        FuelStation = string.Empty;
        SelectedDate = null;
        ReceiptDateText = string.Empty;
        OrNumber = string.Empty;
        Odometer = string.Empty;
        ReceiptPhoto = null;

        IsReceiptParsingUncertain = false;
        ReceiptParsingWarning = string.Empty;

        IsCostEditable = false;
        IsQuantityEditable = false;
        IsFuelStationEditable = false;
        IsReceiptDateEditable = false;
        IsOrNumberEditable = false;

        IsCostManuallyEdited = false;
        IsQuantityManuallyEdited = false;
        IsFuelStationManuallyEdited = false;
        IsReceiptDateManuallyEdited = false;
        IsOrNumberManuallyEdited = false;

        _hasReceiptScan = false;
        _originalCostValue = string.Empty;
        _originalQuantityValue = string.Empty;
        _originalFuelStationValue = string.Empty;
        _originalReceiptDateText = string.Empty;
        _originalOrNumberValue = string.Empty;

        DeleteTempFile(_receiptTempPath);
        DeleteTempFile(_odometerTempPath);
        _receiptTempPath = null;
        _odometerTempPath = null;

        CancelScheduledDraftSave();
        ClearPendingDraft();
        _reportShiftId = LastShiftStore.LocalFuelReportShiftId();
        _isApplyingScanResult = false;
        OnPropertyChanged(nameof(CanSubmit));
    }

    /// <summary>REQ-4.3-3: run when the Fuel Report opens. With no active shift and none that ended
    /// within FuelShiftRules.PostShiftWindow, says so and returns false (the page closes). Offline,
    /// the phone's own memory of the shift decides, and Submit checks again.</summary>
    public async Task<bool> EnsureShiftAsync()
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            if (_reportShiftId is not null) return true;
        }
        else
        {
            try
            {
                string? shiftId = await _shiftService.GetFuelReportShiftIdAsync(_reportShiftId, LastShiftStore.LocalFuelReportShiftId());
                if (shiftId is not null)
                {
                    _reportShiftId = shiftId;
                    return true;
                }
            }
            catch (Exception ex)
            {
                // Can't tell right now - let the driver fill it in; Submit checks again.
                System.Diagnostics.Debug.WriteLine($"Fuel report shift check failed: {ex.Message}");
                return true;
            }
        }

        await Shell.Current.DisplayAlert("No shift", FuelShiftRules.NoShiftMessage, "OK");
        return false;
    }

    /// <summary>FUEL-015: the Fuel Report opens on the unsent report from this shift if there is one
    /// (e.g. the upload failed and the app was closed), otherwise empty.</summary>
    public void RestoreDraftOrReset()
    {
        if (!TryLoadPendingDraft())
        {
            ResetState();
        }
    }

    private async Task ScanOdometerAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted) return;
        }

#if DEBUG
        if (DebugTestImages.Find("larga_test_odometer.jpg") is string testOdometer)
        {
            string copyPath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid():N}_larga_test_odometer.jpg");
            File.Copy(testOdometer, copyPath, overwrite: true);
            MainThread.BeginInvokeOnMainThread(async () =>
                await Application.Current.MainPage.Navigation.PushModalAsync(
                    new LARGA.MobileApp.Views.Driver.OdometerScanPage(_ocrService, copyPath)));
            return;
        }
#endif

        if (MediaPicker.Default.IsCaptureSupported)
        {
            var photo = await MediaPicker.Default.CapturePhotoAsync();

            if (photo != null)
            {
                // THE FIX: Add Guid.NewGuid() to prevent file lock crashes on rescans
                string localFilePath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid():N}_{photo.FileName}");

                // A full-resolution camera photo is often over storage.rules' 5 MB limit, which made
                // Submit fail with "Upload Failed". Same size as the pre/end-shift odometer photo.
                await PhotoResizer.ResizeToJpegAsync(photo.FullPath, localFilePath, maxDimension: 2560, quality: 85);

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
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Delete fuel photo failed: {ex.Message}");
        }
    }

    /// <summary>A rescan replaces the photo: the old one (usually the draft copy KeepPhoto made in
    /// DraftPhotoFolder) is no longer needed, so it isn't left behind until submit/reset.</summary>
    private static void DeleteReplacedPhoto(string? oldPath, string? newPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath)
            || string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        DeleteTempFile(oldPath);
    }

    // THE FIX: Modify upload method to stream from file path directly
    private static async Task<string> UploadImageAsync(string localFilePath, string remotePath)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
        {
            throw new ArgumentException("File path is invalid or does not exist", nameof(localFilePath));
        }

        // A draft saved before photos were resized at capture can still hold one over the limit.
        if (new FileInfo(localFilePath).Length >= MaxUploadBytes)
        {
            string resizedPath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid():N}_upload.jpg");
            localFilePath = await PhotoResizer.ResizeToJpegAsync(localFilePath, resizedPath, maxDimension: 2560, quality: 85);
        }

        var storageRef = CrossFirebaseStorage.Current.GetRootReference().GetChild(remotePath);

        // PutFile reads the bytes from disk in chunks rather than loading it all into memory.
        // The content type is explicit: storage.rules accept images only.
        var transfer = storageRef.PutFile(localFilePath, new StorageMetadata(contentType: "image/jpeg"));
        Task upload = transfer.AwaitAsync();
        if (await Task.WhenAny(upload, Task.Delay(UploadTimeout)) != upload)
        {
            transfer.Cancel();
            throw new TimeoutException($"Upload of {remotePath} took longer than {UploadTimeout.TotalSeconds:0} s.");
        }
        await upload;
        return await storageRef.GetDownloadUrlAsync();
    }

    // ---------------------------------------------------------------------
    // FUEL-015: unsent report kept on the phone
    // ---------------------------------------------------------------------

    private sealed class FuelDraft
    {
        public string ShiftId { get; set; } = string.Empty;
        public string Cost { get; set; } = string.Empty;
        public string Quantity { get; set; } = string.Empty;
        public string FuelStation { get; set; } = string.Empty;
        public string OrNumber { get; set; } = string.Empty;
        public string Odometer { get; set; } = string.Empty;
        public string ReceiptDateText { get; set; } = string.Empty;
        public string? ReceiptPath { get; set; }
        public string? OdometerPath { get; set; }
        public bool HasReceiptScan { get; set; }
        public string OriginalCost { get; set; } = string.Empty;
        public string OriginalQuantity { get; set; } = string.Empty;
        public string OriginalFuelStation { get; set; } = string.Empty;
        public string OriginalReceiptDateText { get; set; } = string.Empty;
        public string OriginalOrNumber { get; set; } = string.Empty;
        public bool IsCostEditable { get; set; }
        public bool IsQuantityEditable { get; set; }
        public bool IsReceiptDateEditable { get; set; }
        public bool IsOrNumberEditable { get; set; }
        public bool CostEdited { get; set; }
        public bool QuantityEdited { get; set; }
        public bool DateEdited { get; set; }
        public bool OrNumberEdited { get; set; }
        public string ParsingWarning { get; set; } = string.Empty;
        public bool ParsingUncertain { get; set; }
    }

    /// <summary>Saves the report as it stands (nothing when no scan has been made yet). The photos
    /// are moved into the app's own storage the first time.</summary>
    private void SavePendingDraft()
    {
        CancelScheduledDraftSave();
        if (_isApplyingScanResult || (!_hasReceiptScan && string.IsNullOrWhiteSpace(Odometer)))
        {
            return;
        }

        try
        {
            string? receiptPath = KeepPhoto(_receiptTempPath);
            if (receiptPath != _receiptTempPath && receiptPath != null)
            {
                _receiptTempPath = receiptPath;
                ReceiptPhoto = ImageSource.FromFile(receiptPath);
            }
            _odometerTempPath = KeepPhoto(_odometerTempPath);

            var draft = new FuelDraft
            {
                ShiftId = _reportShiftId ?? string.Empty,
                Cost = Cost,
                Quantity = Quantity,
                FuelStation = FuelStation,
                OrNumber = OrNumber,
                Odometer = Odometer,
                ReceiptDateText = ReceiptDateText,
                ReceiptPath = _receiptTempPath,
                OdometerPath = _odometerTempPath,
                HasReceiptScan = _hasReceiptScan,
                OriginalCost = _originalCostValue,
                OriginalQuantity = _originalQuantityValue,
                OriginalFuelStation = _originalFuelStationValue,
                OriginalReceiptDateText = _originalReceiptDateText,
                OriginalOrNumber = _originalOrNumberValue,
                IsCostEditable = IsCostEditable,
                IsQuantityEditable = IsQuantityEditable,
                IsReceiptDateEditable = IsReceiptDateEditable,
                IsOrNumberEditable = IsOrNumberEditable,
                CostEdited = IsCostManuallyEdited,
                QuantityEdited = IsQuantityManuallyEdited,
                DateEdited = IsReceiptDateManuallyEdited,
                OrNumberEdited = IsOrNumberManuallyEdited,
                ParsingWarning = ReceiptParsingWarning,
                ParsingUncertain = IsReceiptParsingUncertain,
            };
            Preferences.Set(DraftKey, JsonSerializer.Serialize(draft));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Save fuel draft failed: {ex.Message}");
        }
    }

    /// <summary>SavePendingDraft after DraftSaveDelay with no further change (a field being typed
    /// in); a save or reset meanwhile cancels it.</summary>
    private void ScheduleDraftSave()
    {
        CancelScheduledDraftSave();
        var delay = _draftSaveDelay = new CancellationTokenSource();
        _ = Task.Delay(DraftSaveDelay, delay.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (!delay.IsCancellationRequested)
                    {
                        SavePendingDraft();
                    }
                });
            }
        }, TaskScheduler.Default);
    }

    private void CancelScheduledDraftSave()
    {
        _draftSaveDelay?.Cancel();
        _draftSaveDelay?.Dispose();
        _draftSaveDelay = null;
    }

    /// <summary>A copy of the photo in DraftPhotoFolder (the cache file is removed).</summary>
    private static string? KeepPhoto(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return path;
        }

        Directory.CreateDirectory(DraftPhotoFolder);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) == Path.GetFullPath(DraftPhotoFolder))
        {
            return path;
        }

        string kept = Path.Combine(DraftPhotoFolder, Path.GetFileName(path));
        File.Copy(path, kept, overwrite: true);
        DeleteTempFile(path);
        return kept;
    }

    private bool TryLoadPendingDraft()
    {
        FuelDraft? draft;
        try
        {
            string json = Preferences.Get(DraftKey, string.Empty);
            draft = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<FuelDraft>(json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Read fuel draft failed: {ex.Message}");
            draft = null;
        }

        // Only a report for the current shift or the one that just ended (REQ-4.3-3), and only
        // with its photos still there.
        // A draft with no shift yet (started offline) is kept too - Submit links it.
        if (draft == null
            || (!string.IsNullOrWhiteSpace(draft.ShiftId)
                && draft.ShiftId != Preferences.Get("CurrentShiftId", string.Empty) && draft.ShiftId != LastShiftStore.RecentShiftId())
            || (draft.HasReceiptScan && (string.IsNullOrWhiteSpace(draft.ReceiptPath) || !File.Exists(draft.ReceiptPath))))
        {
            return false;
        }

        _isApplyingScanResult = true;
        _reportShiftId = string.IsNullOrWhiteSpace(draft.ShiftId) ? LastShiftStore.LocalFuelReportShiftId() : draft.ShiftId;
        _hasReceiptScan = draft.HasReceiptScan;
        _originalCostValue = draft.OriginalCost;
        _originalQuantityValue = draft.OriginalQuantity;
        _originalFuelStationValue = draft.OriginalFuelStation;
        _originalReceiptDateText = draft.OriginalReceiptDateText;
        _originalOrNumberValue = draft.OriginalOrNumber;

        Cost = draft.Cost;
        Quantity = draft.Quantity;
        FuelStation = draft.FuelStation;
        OrNumber = draft.OrNumber;
        ReceiptDateText = draft.ReceiptDateText;
        Odometer = draft.Odometer;

        IsCostEditable = draft.IsCostEditable;
        IsQuantityEditable = draft.IsQuantityEditable;
        IsFuelStationEditable = false;
        IsReceiptDateEditable = draft.IsReceiptDateEditable || !SelectedDate.HasValue;
        IsOrNumberEditable = draft.IsOrNumberEditable;

        IsCostManuallyEdited = draft.CostEdited;
        IsQuantityManuallyEdited = draft.QuantityEdited;
        IsFuelStationManuallyEdited = false;
        IsReceiptDateManuallyEdited = draft.DateEdited;
        IsOrNumberManuallyEdited = draft.OrNumberEdited;
        ReceiptParsingWarning = draft.ParsingWarning;
        IsReceiptParsingUncertain = draft.ParsingUncertain;

        _receiptTempPath = draft.ReceiptPath;
        ReceiptPhoto = !string.IsNullOrWhiteSpace(_receiptTempPath) && File.Exists(_receiptTempPath)
            ? ImageSource.FromFile(_receiptTempPath)
            : null;
        _odometerTempPath = !string.IsNullOrWhiteSpace(draft.OdometerPath) && File.Exists(draft.OdometerPath) ? draft.OdometerPath : null;
        if (_odometerTempPath == null)
        {
            Odometer = string.Empty; // photo gone: the odometer has to be scanned again
        }

        _isApplyingScanResult = false;
        OnPropertyChanged(nameof(CanSubmit));
        return true;
    }

    internal static void ClearPendingDraft()
    {
        Preferences.Remove(DraftKey);
        foreach (string key in LegacyPendingKeys)
        {
            Preferences.Remove(LegacyPendingPrefix + key);
        }

        try
        {
            if (Directory.Exists(DraftPhotoFolder))
            {
                Directory.Delete(DraftPhotoFolder, recursive: true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Clear fuel draft photos failed: {ex.Message}");
        }
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
    public string OrNumber { get; set; } = string.Empty;
    public bool IsOrNumberUncertain { get; set; }
    public bool ParsingUncertain { get; set; }
    public string ParsingWarning { get; set; } = string.Empty;
    public bool IsCostUncertain { get; set; }
    public bool IsQuantityUncertain { get; set; }
    public bool IsVendorUncertain { get; set; }
    public bool IsDateUncertain { get; set; }

    // What OCR originally read, when the driver corrected values inline on the scan page (null
    // from senders that don't edit). Lets the report flag those fields as manually edited.
    public string? OriginalAmount { get; set; }
    public string? OriginalQuantity { get; set; }
    public string? OriginalVendor { get; set; }
    public string? OriginalOrNumber { get; set; }

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

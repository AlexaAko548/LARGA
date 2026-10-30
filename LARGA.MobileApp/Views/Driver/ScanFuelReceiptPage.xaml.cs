using CommunityToolkit.Mvvm.Messaging;
using LARGA.MobileApp.Services;
using LARGA.MobileApp.ViewModels.Driver;
using LARGA.SharedCore;
using LARGA.SharedCore.Services;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LARGA.MobileApp.Views.Driver;

public partial class ScanFuelReceiptPage : ContentPage
{
    private readonly IOcrService _ocrService;
    private ReceiptScanSnapshot? _lastSuccessfulScan;
    private string? _capturedImagePath; // THE FIX: Store path, not byte[]
    private DateTime? _capturedReceiptDate;
    private bool _isCostUncertain;
    private bool _isQuantityUncertain;
    private bool _isVendorUncertain;
    private bool _isDateUncertain;
    private bool _isOrNumberUncertain;
    private int _retakeCount;
    private bool _isRetakeLocked;
    private bool _isScanned = false;

    // What OCR read, before any inline edit - sent along so the Fuel Report (and the manager's
    // verification) can tell which values the driver corrected.
    private string _ocrAmount = string.Empty;
    private string _ocrQuantity = string.Empty;
    private string _ocrVendor = string.Empty;
    private string _ocrOrNumber = string.Empty;
    private bool _isApplyingScan;

    private Camera.MAUI.CameraView? ReceiptCameraView => this.FindByName<Camera.MAUI.CameraView>("ReceiptCamera");
    private Image? ReceiptPreviewImage => this.FindByName<Image>("CapturedReceiptPreview");
    private Label? ReceiptDateLabel => this.FindByName<Label>("LblDate");

    public ScanFuelReceiptPage()
    {
        InitializeComponent();
        _ocrService = Application.Current.MainPage.Handler.MauiContext.Services.GetService<IOcrService>();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_isScanned)
        {
            await StartCameraSafelyAsync();
        }
    }

    protected override async void OnDisappearing()
    {
        _retakeCount = 0;
        await StopCameraSafelyAsync();
        base.OnDisappearing();
    }

    private async void Camera_CamerasLoaded(object sender, EventArgs e)
    {
        await StartCameraSafelyAsync();
    }

    // Never throw: these run from async void page events (appearing, cameras loaded, cancel),
    // where an exception would crash the app. A camera that won't start just leaves the preview blank.
    private async Task StartCameraSafelyAsync()
    {
        try
        {
            var camera = ReceiptCameraView;
            if (camera == null || camera.Cameras.Count == 0)
            {
                return;
            }

            camera.Camera = camera.Cameras.FirstOrDefault(c => c.Position == Camera.MAUI.CameraPosition.Back)
                            ?? camera.Cameras.FirstOrDefault();

            camera.ZoomFactor = 0f;
            await camera.StopCameraAsync();
            await camera.StartCameraAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Receipt camera start failed: {ex.Message}");
        }
    }

    private async Task StopCameraSafelyAsync()
    {
        try
        {
            var camera = ReceiptCameraView;
            if (camera != null)
            {
                await camera.StopCameraAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Receipt camera stop failed: {ex.Message}");
        }
    }

    private async void OnCaptureClicked(object sender, EventArgs e)
    {
        try
        {
            await CaptureOrConfirmAsync();
        }
        catch (Exception ex)
        {
            // Snapshot, OCR or navigation failed: let the driver try again instead of crashing.
            System.Diagnostics.Debug.WriteLine($"Receipt capture failed: {ex}");
            BtnCapture.Text = _isScanned ? "Confirm" : "Capture";
            BtnCapture.IsEnabled = true;
            try
            {
                await DisplayAlert("Capture failed", "Something went wrong reading the receipt. Please try again.", "OK");
            }
            catch (Exception alertEx)
            {
                System.Diagnostics.Debug.WriteLine($"Capture-failed alert error: {alertEx.Message}");
            }
        }
    }

    private async Task CaptureOrConfirmAsync()
    {
        if (!_isScanned)
        {
            BtnCapture.Text = "Processing...";
            BtnCapture.IsEnabled = false;

            string tempPath = Path.Combine(FileSystem.CacheDirectory, $"receipt_snap_{Guid.NewGuid():N}.jpg");
            var camera = ReceiptCameraView;
            if (camera == null)
            {
                BtnCapture.IsEnabled = true;
                BtnCapture.Text = "Capture";
                await Shell.Current.DisplayAlert("Camera unavailable", "Unable to access camera preview.", "OK");
                return;
            }

            bool snapResult;
#if DEBUG
            if (DebugTestImages.Find("larga_test_receipt.jpg") is string testReceipt)
            {
                File.Copy(testReceipt, tempPath, overwrite: true);
                snapResult = true;
            }
            else
#endif
            {
                snapResult = await camera.SaveSnapShot(Camera.MAUI.ImageFormat.JPEG, tempPath);
            }

            if (snapResult && File.Exists(tempPath))
            {
                // THE FIX: Assign path directly without reading file into RAM
                _capturedImagePath = tempPath;
                if (ReceiptPreviewImage != null)
                {
                    ReceiptPreviewImage.Source = ImageSource.FromFile(_capturedImagePath);
                    ReceiptPreviewImage.IsVisible = true;
                }

                camera.IsVisible = false;
                await StopCameraSafelyAsync();

                // OCR, then a second pass on an enhanced copy for whatever the first one missed.
                var fields = await ReadReceiptAsync(_capturedImagePath);
                var vendor = fields.Vendor;
                var amount = fields.Amount;
                var quantity = fields.Liters;
                var receiptDate = fields.Date;
                // OR Number is optional on the report, so it's left out of the parsing warning:
                // a receipt without one shouldn't push the driver into retakes.
                var parsingWarning = fields.Warning;
                var parsingUncertain = !string.IsNullOrWhiteSpace(parsingWarning);
                var currentScan = new ReceiptScanSnapshot
                {
                    Amount = amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "--",
                    Quantity = quantity?.ToString("0.###", CultureInfo.InvariantCulture) ?? "--",
                    Vendor = vendor ?? "UNKNOWN",
                    ReceiptDate = receiptDate,
                    OrNumber = fields.OrNumber ?? "--",
                    ParsingWarning = parsingWarning,
                    ParsingUncertain = parsingUncertain,
                    IsCostUncertain = !amount.HasValue || amount.Value <= 0 || fields.AmountUncertain,
                    IsQuantityUncertain = !quantity.HasValue || quantity.Value <= 0,
                    IsVendorUncertain = string.IsNullOrWhiteSpace(vendor) || vendor.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase),
                    IsDateUncertain = !receiptDate.HasValue,
                    IsOrNumberUncertain = fields.OrNumberUncertain,
                    PhotoFilePath = _capturedImagePath // THE FIX: Assign path property
                };

                if (parsingUncertain && _retakeCount >= 3 && _lastSuccessfulScan != null)
                {
                    var warningMessage = string.IsNullOrWhiteSpace(_lastSuccessfulScan.ParsingWarning)
                        ? parsingWarning
                        : _lastSuccessfulScan.ParsingWarning;
                    await Shell.Current.DisplayAlert("OCR check", warningMessage, "Use and Review");
                    ApplyScanResult(_lastSuccessfulScan);
                    _isRetakeLocked = true;
                }
                else
                {
                    ApplyScanResult(currentScan);
                    _lastSuccessfulScan = currentScan;
                }

                BtnCapture.Text = "Confirm";
                BtnRetake.IsVisible = !_isRetakeLocked;
                BtnRetake.IsEnabled = !_isRetakeLocked;
                _isScanned = true;
            }
            else
            {
                await Shell.Current.DisplayAlert("Capture failed", "Unable to capture receipt photo. Please retry.", "OK");
            }
            BtnCapture.IsEnabled = true;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(_capturedImagePath))
            {
                // The driver may have corrected the values inline: what's in the fields now is what
                // goes to the report. A malformed number keeps the page open with the reason shown.
                if (!TryReadEditedNumbers(out decimal? amount, out decimal? quantity, out string? error))
                {
                    ShowEditError(error);
                    return;
                }
                ShowEditError(null);

                string amountText = amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty;
                string quantityText = quantity?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
                // The station isn't editable: always the OCR read. "UNKNOWN" when nothing was read,
                // so the report can still be submitted (the parsing warning flags it).
                string vendor = string.IsNullOrEmpty(_ocrVendor) ? "UNKNOWN" : _ocrVendor;
                string orNumber = FieldText(EntOrNumber);

                // A guessed total stays flagged until the driver types the real one.
                bool amountStillGuessed = _isCostUncertain && SameNumber(amountText, _ocrAmount);
                var payloadWarning = FuelReceiptTextParser.BuildParsingWarning(
                    string.IsNullOrEmpty(vendor) ? null : vendor, amount, quantity, _capturedReceiptDate, amountStillGuessed);

                var payload = new ReceiptExtractedData
                {
                    Amount = amountText,
                    Quantity = quantityText,
                    Vendor = vendor,
                    ReceiptDate = _capturedReceiptDate,
                    OrNumber = orNumber,
                    OriginalAmount = _ocrAmount,
                    OriginalQuantity = _ocrQuantity,
                    OriginalVendor = vendor,
                    OriginalOrNumber = _ocrOrNumber,
                    ParsingWarning = payloadWarning,
                    ParsingUncertain = !string.IsNullOrWhiteSpace(payloadWarning),
                    IsCostUncertain = _isCostUncertain || IsZeroValue(amountText),
                    IsQuantityUncertain = _isQuantityUncertain || IsZeroValue(quantityText),
                    IsVendorUncertain = _isVendorUncertain,
                    IsDateUncertain = _isDateUncertain,
                    IsOrNumberUncertain = _isOrNumberUncertain,
                    PhotoFilePath = _capturedImagePath // THE FIX: Assign path property
                };

                CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(payload);
            }

            await Navigation.PopModalAsync();
        }
    }

    /// <summary>
    /// OCR and parse the receipt photo. When the first read misses a field, the photo is read again
    /// from an enhanced copy (upright, upscaled, high-contrast grayscale) and only the missing
    /// fields are taken from that second read. The original photo is what gets uploaded.
    /// </summary>
    private async Task<FuelReceiptTextParser.ReceiptFields> ReadReceiptAsync(string photoPath)
    {
        var first = FuelReceiptTextParser.Parse(ToParserLines(await _ocrService.ExtractTextBlocksAsync(photoPath)));
        if (string.IsNullOrEmpty(first.Warning))
        {
            return first;
        }

        string? enhancedPath = await Task.Run(() => OcrImageEnhancer.CreateEnhancedCopy(photoPath));
        if (enhancedPath == null)
        {
            return first;
        }

        try
        {
            var second = FuelReceiptTextParser.Parse(ToParserLines(await _ocrService.ExtractTextBlocksAsync(enhancedPath)));
            bool takeSecondAmount = (!first.Amount.HasValue || first.AmountUncertain) && second.Amount.HasValue && !second.AmountUncertain;
            decimal? amount = takeSecondAmount ? second.Amount : first.Amount ?? second.Amount;
            bool amountUncertain = takeSecondAmount ? false : first.Amount.HasValue ? first.AmountUncertain : second.AmountUncertain;
            string? vendor = first.Vendor ?? second.Vendor;
            decimal? liters = first.Liters ?? second.Liters;
            DateTime? date = first.Date ?? second.Date;
            bool takeSecondOr = first.OrNumber == null || (first.OrNumberUncertain && second.OrNumber != null && !second.OrNumberUncertain);
            return new FuelReceiptTextParser.ReceiptFields(
                vendor, amount, amountUncertain, liters, date,
                takeSecondOr ? second.OrNumber : first.OrNumber,
                takeSecondOr ? second.OrNumberUncertain : first.OrNumberUncertain,
                FuelReceiptTextParser.BuildParsingWarning(vendor, amount, liters, date, amountUncertain));
        }
        finally
        {
            try { File.Delete(enhancedPath); } catch { /* cache file */ }
        }
    }

    /// <summary>ML Kit lines with their boxes (or the whole block when no lines were reported).</summary>
    private static List<FuelReceiptTextParser.OcrBlock> ToParserLines(List<OcrTextBlock> blocks) =>
        blocks.SelectMany(b => b.Lines is { Count: > 0 }
                ? b.Lines.Select(l => new FuelReceiptTextParser.OcrBlock(l.Text, l.BoundingBox.X, l.BoundingBox.Y, l.BoundingBox.Width, l.BoundingBox.Height, l.Slope))
                : new[] { new FuelReceiptTextParser.OcrBlock(b.Text, b.BoundingBox.X, b.BoundingBox.Y, b.BoundingBox.Width, b.BoundingBox.Height) })
            .ToList();

    private async void OnCancelClicked(object sender, EventArgs e)
    {
        // Gated: a double tap would otherwise close this scanner and then the screen under it.
        await LARGA.MobileApp.Services.NavigationGate.RunAsync(async () =>
        {
            await StopCameraSafelyAsync();
            try
            {
                await Navigation.PopModalAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Close receipt scan failed: {ex.Message}");
            }
        });
    }

    private async void OnRetakeClicked(object sender, EventArgs e)
    {
        if (!_isScanned || _isRetakeLocked)
            return;

        _retakeCount++;
        _isScanned = false;
        _capturedImagePath = null; // THE FIX: Reset path string
        _capturedReceiptDate = null;
        _isCostUncertain = false;
        _isQuantityUncertain = false;
        _isVendorUncertain = false;
        _isDateUncertain = false;
        _isOrNumberUncertain = false;

        _isApplyingScan = true;
        foreach (Entry entry in ScannedFields)
        {
            entry.Text = string.Empty;
            entry.IsEnabled = false;
        }
        _isApplyingScan = false;
        _ocrAmount = _ocrQuantity = _ocrVendor = _ocrOrNumber = string.Empty;
        LblVendor.Text = "--";
        RefreshEditedTags();
        ShowEditError(null);
        LblEditHint.Text = "Capture the receipt, then check and correct the values below.";
        if (ReceiptDateLabel != null)
        {
            ReceiptDateLabel.Text = "--";
        }

        if (ReceiptPreviewImage != null)
        {
            ReceiptPreviewImage.IsVisible = false;
            ReceiptPreviewImage.Source = null;
        }

        var camera = ReceiptCameraView;
        if (camera != null)
        {
            camera.IsVisible = true;
        }

        BtnRetake.IsVisible = false;
        BtnRetake.IsEnabled = true;
        BtnCapture.Text = "Capture";
        BtnCapture.IsEnabled = true;

        await StartCameraSafelyAsync();
    }

    private void ApplyScanResult(ReceiptScanSnapshot scan)
    {
        _capturedImagePath = scan.PhotoFilePath;
        _capturedReceiptDate = scan.ReceiptDate;
        _isCostUncertain = scan.IsCostUncertain;
        _isQuantityUncertain = scan.IsQuantityUncertain;
        _isVendorUncertain = scan.IsVendorUncertain;
        _isDateUncertain = scan.IsDateUncertain;
        _isOrNumberUncertain = scan.IsOrNumberUncertain;

        // OCR's reads go into the editable fields ("--" = not read: left empty for the driver to fill).
        _ocrAmount = scan.Amount == "--" ? string.Empty : scan.Amount;
        _ocrQuantity = scan.Quantity == "--" ? string.Empty : scan.Quantity;
        _ocrVendor = scan.Vendor.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase) ? string.Empty : scan.Vendor;
        _ocrOrNumber = scan.OrNumber == "--" ? string.Empty : scan.OrNumber;

        _isApplyingScan = true;
        EntAmount.Text = _ocrAmount;
        EntQuantity.Text = _ocrQuantity;
        LblVendor.Text = string.IsNullOrEmpty(_ocrVendor) ? "--" : _ocrVendor;
        EntOrNumber.Text = _ocrOrNumber;
        foreach (Entry entry in ScannedFields)
        {
            entry.IsEnabled = true;
        }
        _isApplyingScan = false;
        RefreshEditedTags();
        ShowEditError(null);
        LblEditHint.Text = string.IsNullOrWhiteSpace(scan.ParsingWarning)
            ? "Tap a value to correct it, then Confirm."
            : scan.ParsingWarning;

        if (ReceiptDateLabel != null)
        {
            ReceiptDateLabel.Text = scan.ReceiptDate?.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture) ?? "--";
        }

        if (ReceiptPreviewImage != null && !string.IsNullOrWhiteSpace(scan.PhotoFilePath))
        {
            // THE FIX: Bind from file directly
            ReceiptPreviewImage.Source = ImageSource.FromFile(scan.PhotoFilePath);
            ReceiptPreviewImage.IsVisible = true;
        }
    }

    private static bool IsZeroValue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return value == 0m;

        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value))
            return value == 0m;

        return text.Trim() == "--";
    }

    private IEnumerable<Entry> ScannedFields => new Entry[] { EntAmount, EntQuantity, EntOrNumber };

    private static string FieldText(Entry entry) => (entry.Text ?? string.Empty).Trim();

    private void OnScannedFieldChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isApplyingScan)
        {
            return;
        }

        RefreshEditedTags();
        ShowEditError(null);
    }

    /// <summary>Shows "Edited" next to each value the driver changed from what OCR read.</summary>
    private void RefreshEditedTags()
    {
        TagAmountEdited.IsVisible = EntAmount.IsEnabled && !SameNumber(FieldText(EntAmount), _ocrAmount);
        TagQuantityEdited.IsVisible = EntQuantity.IsEnabled && !SameNumber(FieldText(EntQuantity), _ocrQuantity);
        TagOrNumberEdited.IsVisible = EntOrNumber.IsEnabled && !string.Equals(FieldText(EntOrNumber), _ocrOrNumber, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameNumber(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b);
        }
        return FuelReceiptTextParser.TryParseDecimal(a, out var x) && FuelReceiptTextParser.TryParseDecimal(b, out var y) ? x == y : string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>
    /// Amount and Liters as typed. Empty is allowed (the Fuel Report asks for it then, as before),
    /// but a value that isn't a positive number - or more liters than a taxi's tank could take -
    /// is refused here, before it reaches the report.
    /// </summary>
    private bool TryReadEditedNumbers(out decimal? amount, out decimal? quantity, out string? error)
    {
        amount = null;
        quantity = null;
        error = null;

        string amountText = FieldText(EntAmount);
        if (amountText.Length > 0)
        {
            if (!FuelReceiptTextParser.TryParseDecimal(amountText.Replace("₱", string.Empty).Trim(), out var a) || a <= 0m || a > 100000m)
            {
                error = "Enter the fuel cost as an amount, e.g. 1500.00.";
                return false;
            }
            amount = decimal.Round(a, 2);
        }

        string quantityText = FieldText(EntQuantity);
        if (quantityText.Length > 0)
        {
            if (!FuelReceiptTextParser.TryParseDecimal(quantityText.Replace("L", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(), out var q) || q <= 0m || q > 200m)
            {
                error = "Enter the liters as a number between 0 and 200, e.g. 25.5.";
                return false;
            }
            quantity = decimal.Round(q, 3);
        }

        return true;
    }

    private void ShowEditError(string? message)
    {
        LblEditError.Text = message ?? string.Empty;
        LblEditError.IsVisible = !string.IsNullOrEmpty(message);
    }

    private sealed class ReceiptScanSnapshot
    {
        public string Amount { get; init; } = "--";
        public string Quantity { get; init; } = "--";
        public string Vendor { get; init; } = "UNKNOWN";
        public DateTime? ReceiptDate { get; init; }
        public string OrNumber { get; init; } = "--";
        public string ParsingWarning { get; init; } = string.Empty;
        public bool ParsingUncertain { get; init; }
        public bool IsCostUncertain { get; init; }
        public bool IsQuantityUncertain { get; init; }
        public bool IsVendorUncertain { get; init; }
        public bool IsDateUncertain { get; init; }
        public bool IsOrNumberUncertain { get; init; }

        // THE FIX: Change property to accept file path instead of bytes
        public string PhotoFilePath { get; init; } = string.Empty;
    }
}
using CommunityToolkit.Mvvm.Messaging;
using LARGA.MobileApp.Services;
using LARGA.MobileApp.ViewModels.Driver;
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

            var snapResult = await camera.SaveSnapShot(Camera.MAUI.ImageFormat.JPEG, tempPath);

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

                // THE FIX: Pass string path to OCR
                var detectedBlocks = await _ocrService.ExtractTextBlocksAsync(_capturedImagePath);
                var lines = detectedBlocks
                    .Select(b => b.Text?.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t!)
                    .ToList();

                string fullText = string.Join(" ", lines);

                var vendor = TryParseVendor(fullText, lines);
                var amount = TryParseAmount(fullText, lines);
                var quantity = TryParseLiters(fullText, lines);
                var receiptDate = TryParseReceiptDate(fullText, lines);
                // OR Number is optional on the report, so it's left out of BuildParsingWarning:
                // a receipt without one shouldn't push the driver into retakes.
                var orNumber = FuelReceiptParser.TryParseOrNumber(lines, out var orNumberUncertain);
                var parsingWarning = BuildParsingWarning(vendor, amount, quantity, receiptDate);
                var parsingUncertain = !string.IsNullOrWhiteSpace(parsingWarning);
                var currentScan = new ReceiptScanSnapshot
                {
                    Amount = amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "--",
                    Quantity = quantity?.ToString("0.##", CultureInfo.InvariantCulture) ?? "--",
                    Vendor = vendor ?? "UNKNOWN",
                    ReceiptDate = receiptDate,
                    OrNumber = orNumber ?? "--",
                    ParsingWarning = parsingWarning,
                    ParsingUncertain = parsingUncertain,
                    IsCostUncertain = !amount.HasValue || amount.Value <= 0,
                    IsQuantityUncertain = !quantity.HasValue || quantity.Value <= 0,
                    IsVendorUncertain = string.IsNullOrWhiteSpace(vendor) || vendor.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase),
                    IsDateUncertain = !receiptDate.HasValue,
                    IsOrNumberUncertain = orNumberUncertain,
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
                string quantityText = quantity?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
                string vendor = FieldText(EntVendor);
                string orNumber = FieldText(EntOrNumber);

                var payloadWarning = BuildParsingWarning(
                    string.IsNullOrEmpty(vendor) ? null : vendor, amount, quantity, _capturedReceiptDate);

                var payload = new ReceiptExtractedData
                {
                    Amount = amountText,
                    Quantity = quantityText,
                    Vendor = vendor,
                    ReceiptDate = _capturedReceiptDate,
                    OrNumber = orNumber,
                    OriginalAmount = _ocrAmount,
                    OriginalQuantity = _ocrQuantity,
                    OriginalVendor = _ocrVendor,
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

    private async void OnCancelClicked(object sender, EventArgs e)
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

    private static string? TryParseVendor(string fullText, List<string> lines)
    {
        var upper = NormalizeVendorText(fullText);
        string[] knownStations = ["PETRON", "SHELL", "CALTEX", "SEAOIL", "TOTAL", "PHOENIX", "UNIOIL", "JETTI"];
        var hit = knownStations.FirstOrDefault(upper.Contains);
        if (!string.IsNullOrWhiteSpace(hit))
            return hit;

        var firstLine = lines.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
            return null;

        var cleaned = Regex.Replace(firstLine, "[^A-Za-z0-9 .&-]", "").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
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
        EntVendor.Text = _ocrVendor;
        EntOrNumber.Text = _ocrOrNumber;
        foreach (Entry entry in ScannedFields)
        {
            entry.IsEnabled = true;
        }
        _isApplyingScan = false;
        RefreshEditedTags();
        ShowEditError(null);
        LblEditHint.Text = "Tap a value to correct it, then Confirm.";

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

    private static decimal? TryParseAmount(string fullText, List<string> lines)
    {
        var candidates = new List<(decimal Value, int Score)>();

        foreach (var line in lines)
        {
            var upperLine = line.ToUpperInvariant();
            var hasAmountKeyword = upperLine.Contains("TOTAL") || upperLine.Contains("AMOUNT") || upperLine.Contains("NET") || upperLine.Contains("SALE") || upperLine.Contains("DUE") || upperLine.Contains("PAYABLE");
            var isLikelyNonTotal = upperLine.Contains("VAT") || upperLine.Contains("CHANGE") || upperLine.Contains("DISCOUNT") || upperLine.Contains("PRICE/L") || upperLine.Contains("UNIT PRICE") || upperLine.Contains("LITER") || upperLine.Contains("LTR") || upperLine.Contains("QTY");
            var score = hasAmountKeyword ? (upperLine.Contains("TOTAL") ? 3 : 2) : 0;

            if (isLikelyNonTotal)
            {
                score--;
            }

            foreach (Match match in Regex.Matches(line, @"(?:PHP|P|₱|\?)?\s*\d{1,3}(?:[\s,]\d{3})*(?:[\.,]\d{2,3})|(?:PHP|P|₱|\?)?\s*\d+[\.,]\d{2,3}"))
            {
                if (TryParseDecimal(match.Value, out var value) && value > 0)
                {
                    candidates.Add((value, score));
                }
            }
        }

        var bestKeywordAmount = candidates
            .Where(c => c.Score >= 2)
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Value)
            .FirstOrDefault();

        if (bestKeywordAmount.Value > 0)
        {
            return bestKeywordAmount.Value;
        }

        var keywordMatch = Regex.Match(fullText.ToUpperInvariant(),
            @"(?:TOTAL\s+AMOUNT|AMOUNT\s+DUE|NET\s+AMOUNT|GRAND\s+TOTAL|TOTAL|SALE)\s*[:=]?\s*(PHP|P|₱|\?)?\s*(\d{1,3}(?:[,\s]\d{3})*(?:[\.,]\d{2,3})|\d+[\.,]\d{2,3})");
        if (keywordMatch.Success && TryParseDecimal(keywordMatch.Groups[2].Value, out var keyedAmount))
        {
            return keyedAmount;
        }

        var fallback = candidates
            .Where(c => c.Value >= 10m)
            .OrderByDescending(c => c.Value)
            .FirstOrDefault();

        return fallback.Value > 0 ? fallback.Value : null;
    }

    private static decimal? TryParseLiters(string fullText, List<string> lines)
    {
        var matches = new List<decimal>();

        foreach (var line in lines)
        {
            foreach (Match match in Regex.Matches(line.ToUpperInvariant(), @"(?:QTY|QUANTITY|VOLUME|VOL|LITERS?|LITRES?|LTRS?)\s*[:=]?\s*(\d{1,3}(?:[\.,]\d{1,3})?)"))
            {
                if (TryParseDecimal(match.Groups[1].Value, out var liters) && liters > 0m && liters <= 200m)
                {
                    matches.Add(liters);
                }
            }

            foreach (Match match in Regex.Matches(line.ToUpperInvariant(), @"(\d{1,3}(?:[\.,]\d{1,3})?)\s*(?:LITERS?|LITRES?|LTRS?|L)\b"))
            {
                if (TryParseDecimal(match.Groups[1].Value, out var liters) && liters > 0m && liters <= 200m)
                {
                    matches.Add(liters);
                }
            }
        }

        if (matches.Count > 0)
        {
            return matches.OrderByDescending(v => v).FirstOrDefault();
        }

        var fullTextMatch = Regex.Match(fullText.ToUpperInvariant(),
            @"(?:QTY|QUANTITY|VOLUME|VOL)\s*[:=]?\s*(\d{1,3}(?:[\.,]\d{1,3})?)|(\d{1,3}(?:[\.,]\d{1,3})?)\s*(?:LITERS?|LITRES?|LTRS?|L)\b");

        if (!fullTextMatch.Success)
        {
            return null;
        }

        var token = !string.IsNullOrWhiteSpace(fullTextMatch.Groups[1].Value)
            ? fullTextMatch.Groups[1].Value
            : fullTextMatch.Groups[2].Value;

        return TryParseDecimal(token, out var parsedLiters) && parsedLiters > 0m ? parsedLiters : null;
    }

    private static DateTime? TryParseReceiptDate(string fullText, List<string> lines)
    {
        var now = DateTime.Now.Date;

        var keywordDates = new List<DateTime>();
        foreach (var line in lines)
        {
            var upperLine = line.ToUpperInvariant();
            if (upperLine.Contains("DATE") || upperLine.Contains("TRANS") || upperLine.Contains("INVOICE"))
            {
                keywordDates.AddRange(ExtractDatesFromText(line));
            }
        }

        var bestKeywordDate = keywordDates
            .Where(IsPlausibleReceiptDate)
            .OrderBy(d => Math.Abs((d.Date - now).TotalDays))
            .FirstOrDefault();

        if (bestKeywordDate != default)
        {
            return bestKeywordDate.Date;
        }

        var patterns = new[]
        {
            @"\b\d{4}[-/]\d{1,2}[-/]\d{1,2}\b",
            @"\b\d{1,2}[-/]\d{1,2}[-/]\d{2,4}\b",
            @"\b(?:JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|SEPT|OCT|NOV|DEC)[A-Z]*\.?\s+\d{1,2},?\s+\d{2,4}\b"
        };

        var fallbackDates = new List<DateTime>();

        foreach (var pattern in patterns)
        {
            var matches = Regex.Matches(fullText.ToUpperInvariant(), pattern);
            foreach (Match match in matches)
            {
                if (TryParseDateFlexible(match.Value, out var dt))
                {
                    fallbackDates.Add(dt.Date);
                }
            }
        }

        return fallbackDates
            .Where(IsPlausibleReceiptDate)
            .OrderBy(d => Math.Abs((d.Date - now).TotalDays))
            .FirstOrDefault();
    }

    private static bool TryParseDecimal(string token, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var cleaned = Regex.Replace(token, @"[^\d,\.\-]", string.Empty);
        if (string.IsNullOrWhiteSpace(cleaned))
            return false;

        var lastDot = cleaned.LastIndexOf('.');
        var lastComma = cleaned.LastIndexOf(',');
        var decimalSeparatorIndex = Math.Max(lastDot, lastComma);

        if (decimalSeparatorIndex >= 0)
        {
            var integerPart = Regex.Replace(cleaned[..decimalSeparatorIndex], @"[^\d\-]", string.Empty);
            var decimalPart = Regex.Replace(cleaned[(decimalSeparatorIndex + 1)..], @"[^\d]", string.Empty);
            cleaned = string.IsNullOrWhiteSpace(decimalPart) ? integerPart : $"{integerPart}.{decimalPart}";
        }
        else
        {
            cleaned = Regex.Replace(cleaned, @"[^\d\-]", string.Empty);
        }

        return decimal.TryParse(cleaned, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseDateFlexible(string token, out DateTime date)
    {
        var formats = new[]
        {
            "yyyy-MM-dd", "yyyy/M/d", "yyyy/MM/dd",
            "M/d/yyyy", "MM/dd/yyyy", "d/M/yyyy", "dd/MM/yyyy",
            "M/d/yy", "MM/dd/yy", "d/M/yy", "dd/MM/yy",
            "MMM d yyyy", "MMM d, yyyy", "MMMM d yyyy", "MMMM d, yyyy",
            "MMM dd yyyy", "MMM dd, yyyy", "MMMM dd yyyy", "MMMM dd, yyyy"
        };

        return DateTime.TryParseExact(token.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date)
               || DateTime.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static IEnumerable<DateTime> ExtractDatesFromText(string text)
    {
        var patterns = new[]
        {
            @"\b\d{4}[-/]\d{1,2}[-/]\d{1,2}\b",
            @"\b\d{1,2}[-/]\d{1,2}[-/]\d{2,4}\b",
            @"\b(?:JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|SEPT|OCT|NOV|DEC)[A-Z]*\.?\s+\d{1,2},?\s+\d{2,4}\b"
        };

        foreach (var pattern in patterns)
        {
            var matches = Regex.Matches(text.ToUpperInvariant(), pattern);
            foreach (Match match in matches)
            {
                if (TryParseDateFlexible(match.Value, out var parsed))
                {
                    yield return parsed.Date;
                }
            }
        }
    }

    private static bool IsPlausibleReceiptDate(DateTime date)
    {
        var today = DateTime.Today;
        return date.Year >= 2020 && date.Date <= today.AddDays(1) && date.Date >= today.AddYears(-3);
    }

    private static string NormalizeVendorText(string text)
    {
        return text
            .ToUpperInvariant()
            .Replace('0', 'O')
            .Replace('1', 'I')
            .Replace('5', 'S');
    }

    private static string BuildParsingWarning(string? vendor, decimal? amount, decimal? liters, DateTime? date)
    {
        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(vendor) || vendor.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
            issues.Add("Station name may be incorrect.");

        if (!amount.HasValue || amount.Value <= 0)
            issues.Add("Total amount was not confidently detected.");

        if (!liters.HasValue || liters.Value <= 0)
            issues.Add("Liters/quantity was not confidently detected.");

        if (!date.HasValue)
            issues.Add("Receipt date was not confidently detected.");

        return issues.Count == 0 ? string.Empty : "OCR uncertain: " + string.Join(" ", issues) + " Please review/edit before submit.";
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

    private IEnumerable<Entry> ScannedFields => new Entry[] { EntAmount, EntQuantity, EntVendor, EntOrNumber };

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
        TagVendorEdited.IsVisible = EntVendor.IsEnabled && !string.Equals(FieldText(EntVendor), _ocrVendor, StringComparison.OrdinalIgnoreCase);
        TagOrNumberEdited.IsVisible = EntOrNumber.IsEnabled && !string.Equals(FieldText(EntOrNumber), _ocrOrNumber, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameNumber(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b);
        }
        return TryParseDecimal(a, out var x) && TryParseDecimal(b, out var y) ? x == y : string.Equals(a, b, StringComparison.Ordinal);
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
            if (!TryParseDecimal(amountText.Replace("₱", string.Empty).Trim(), out var a) || a <= 0m || a > 100000m)
            {
                error = "Enter the fuel cost as an amount, e.g. 1500.00.";
                return false;
            }
            amount = decimal.Round(a, 2);
        }

        string quantityText = FieldText(EntQuantity);
        if (quantityText.Length > 0)
        {
            if (!TryParseDecimal(quantityText.Replace("L", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(), out var q) || q <= 0m || q > 200m)
            {
                error = "Enter the liters as a number between 0 and 200, e.g. 25.5.";
                return false;
            }
            quantity = decimal.Round(q, 2);
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
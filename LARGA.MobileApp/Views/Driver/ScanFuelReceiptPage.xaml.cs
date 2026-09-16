using CommunityToolkit.Mvvm.Messaging;
using LARGA.MobileApp.Services;
using LARGA.MobileApp.ViewModels.Driver;
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
    private byte[]? _capturedImageBytes;
    private DateTime? _capturedReceiptDate;
    private bool _isCostUncertain;
    private bool _isQuantityUncertain;
    private bool _isVendorUncertain;
    private bool _isDateUncertain;
    private bool _isScanned = false;

    private Camera.MAUI.CameraView? ReceiptCameraView => this.FindByName<Camera.MAUI.CameraView>("ReceiptCamera");
    private Image? ReceiptPreviewImage => this.FindByName<Image>("CapturedReceiptPreview");
    private Label? ReceiptDateLabel => this.FindByName<Label>("LblDate");

    public ScanFuelReceiptPage()
    {
        InitializeComponent();

        // Resolve the service manually if not using DI in code-behind
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
        await StopCameraSafelyAsync();
        base.OnDisappearing();
    }

    private async void Camera_CamerasLoaded(object sender, EventArgs e)
    {
        await StartCameraSafelyAsync();
    }

    private async Task StartCameraSafelyAsync()
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

    private async Task StopCameraSafelyAsync()
    {
        var camera = ReceiptCameraView;
        if (camera != null)
        {
            await camera.StopCameraAsync();
        }
    }

    private async void OnCaptureClicked(object sender, EventArgs e)
    {
        if (!_isScanned)
        {
            // 1. CAPTURE PHASE
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
                _capturedImageBytes = File.ReadAllBytes(tempPath);
                if (ReceiptPreviewImage != null)
                {
                    ReceiptPreviewImage.Source = ImageSource.FromFile(tempPath);
                    ReceiptPreviewImage.IsVisible = true;
                }

                camera.IsVisible = false;
                await StopCameraSafelyAsync();

                var detectedBlocks = await _ocrService.ExtractTextBlocksAsync(_capturedImageBytes);
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
                var parsingWarning = BuildParsingWarning(vendor, amount, quantity, receiptDate);
                var parsingUncertain = !string.IsNullOrWhiteSpace(parsingWarning);
                _isCostUncertain = !amount.HasValue || amount.Value <= 0;
                _isQuantityUncertain = !quantity.HasValue || quantity.Value <= 0;
                _isVendorUncertain = string.IsNullOrWhiteSpace(vendor) || vendor.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase);
                _isDateUncertain = !receiptDate.HasValue;

                LblVendor.Text = vendor ?? "UNKNOWN";
                LblAmount.Text = amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "--";
                LblQuantity.Text = quantity?.ToString("0.##", CultureInfo.InvariantCulture) ?? "--";
                if (ReceiptDateLabel != null)
                {
                    ReceiptDateLabel.Text = receiptDate?.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture) ?? "--";
                }
                _capturedReceiptDate = receiptDate;
                if (parsingUncertain)
                {
                    await Shell.Current.DisplayAlert("OCR check", parsingWarning, "Use and Review");
                }

                BtnCapture.Text = "Confirm";
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
            if (_capturedImageBytes != null)
            {
                var payloadWarning = BuildParsingWarning(
                    LblVendor.Text == "--" ? null : LblVendor.Text,
                    decimal.TryParse(LblAmount.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedAmount) ? parsedAmount : null,
                    decimal.TryParse(LblQuantity.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedQuantity) ? parsedQuantity : null,
                    _capturedReceiptDate);

                var payload = new ReceiptExtractedData
                {
                    Amount = LblAmount.Text == "--" ? string.Empty : LblAmount.Text,
                    Quantity = LblQuantity.Text == "--" ? string.Empty : LblQuantity.Text,
                    Vendor = LblVendor.Text == "--" ? string.Empty : LblVendor.Text,
                    ReceiptDate = _capturedReceiptDate,
                    ParsingWarning = payloadWarning,
                    ParsingUncertain = !string.IsNullOrWhiteSpace(payloadWarning),
                    IsCostUncertain = _isCostUncertain || IsZeroValue(LblAmount.Text),
                    IsQuantityUncertain = _isQuantityUncertain || IsZeroValue(LblQuantity.Text),
                    IsVendorUncertain = _isVendorUncertain,
                    IsDateUncertain = _isDateUncertain,
                    PhotoBytes = _capturedImageBytes
                };

                CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(payload);
            }

            await Navigation.PopModalAsync();
        }
    }

    private async void OnCancelClicked(object sender, EventArgs e)
    {
        await StopCameraSafelyAsync();
        await Navigation.PopModalAsync();
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

    private static decimal? TryParseAmount(string fullText, List<string> lines)
    {
        var upper = fullText.ToUpperInvariant();

        var amountKeywordLine = lines
            .Select(l => l.ToUpperInvariant())
            .FirstOrDefault(l => l.Contains("TOTAL") || l.Contains("AMOUNT") || l.Contains("AMT") || l.Contains("NET"));

        if (!string.IsNullOrWhiteSpace(amountKeywordLine))
        {
            var keywordLineAmounts = Regex.Matches(amountKeywordLine, @"\b\d{1,3}(?:[\s,]\d{3})*(?:\.\d{2})\b|\b\d+\.\d{2}\b")
                .Select(m => m.Value)
                .ToList();

            decimal? lineMax = null;
            foreach (var token in keywordLineAmounts)
            {
                if (TryParseDecimal(token, out var value))
                {
                    lineMax = lineMax == null ? value : Math.Max(lineMax.Value, value);
                }
            }

            if (lineMax.HasValue)
                return lineMax;
        }

        var keywordMatch = Regex.Match(upper,
            @"(?:TOTAL\s+AMOUNT|AMOUNT\s+DUE|AMOUNT|TOTAL|SALE)\s*[:=]?\s*(\d{1,3}(?:[,\s]\d{3})*(?:\.\d{2})|\d+\.\d{2})");
        if (keywordMatch.Success && TryParseDecimal(keywordMatch.Groups[1].Value, out var keyedAmount))
            return keyedAmount;

        var allAmounts = Regex.Matches(upper, @"\b\d{1,3}(?:[,\s]\d{3})*(?:\.\d{2})\b|\b\d+\.\d{2}\b")
            .Select(m => m.Value)
            .ToList();

        decimal? max = null;
        foreach (var token in allAmounts)
        {
            if (TryParseDecimal(token, out var value))
            {
                max = max == null ? value : Math.Max(max.Value, value);
            }
        }

        return max;
    }

    private static decimal? TryParseLiters(string fullText, List<string> lines)
    {
        var upper = fullText.ToUpperInvariant();

        var quantityLine = lines
            .Select(l => l.ToUpperInvariant())
            .FirstOrDefault(l => l.Contains("LITER") || l.Contains("LITRE") || l.Contains("LTR") || l.Contains("QTY"));

        if (!string.IsNullOrWhiteSpace(quantityLine))
        {
            var fromLine = Regex.Match(quantityLine, @"(\d{1,3}(?:[\.,]\d{1,3})?)\s*(?:LITERS|LITER|LITRE|LTRS?|L)\b");
            if (fromLine.Success && TryParseDecimal(fromLine.Groups[1].Value.Replace(',', '.'), out var litersFromLine))
            {
                return litersFromLine;
            }
        }

        var litersMatch = Regex.Match(upper,
            @"(\d{1,3}(?:[\.,]\d{1,3})?)\s*(?:LITERS|LITER|LITRE|LTRS?|L)\b");

        if (!litersMatch.Success)
        {
            litersMatch = Regex.Match(upper, @"(?:QTY|QUANTITY)\s*[:=]?\s*(\d{1,3}(?:[\.,]\d{1,3})?)");
        }

        if (!litersMatch.Success)
            return null;

        return TryParseDecimal(litersMatch.Groups[1].Value, out var liters) ? liters : null;
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
        var normalized = token.Replace(" ", string.Empty).Replace(",", string.Empty);
        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
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
}
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.ApplicationModel;
using LARGA.MobileApp.Services;
using LARGA.MobileApp.ViewModels.Driver;
using LARGA.SharedCore.Services;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LARGA.MobileApp.Views.Driver;

public partial class OdometerScanPage : ContentPage
{
    private readonly IOcrService _ocrService;
    private readonly string _localFilePath;
    private readonly string _messageToken;
    private List<OcrTextBlock>? _pendingBlocks;
    private bool _isDrawn = false;

    private enum ScanMode { Ocr, Lcd }
    private ScanMode _mode = ScanMode.Ocr;

    // Crop box state: an initial AbsoluteLayout rect, moved/resized visually via
    // TranslationX/Y (pan) and Scale (pinch, anchored at the box's own center by default) -
    // the standard MAUI drag/resize pattern. Combined back into an effective on-screen rect
    // at "Read Digits" time.
    private Rect _cropBoxInitialBounds;
    private double _cropTranslateX, _cropTranslateY;
    private double _cropScaleAtPinchStart = 1.0;
    private bool _cropBoxInitialized;

    public OdometerScanPage(IOcrService ocrService, string localFilePath, string messageToken = "OdometerScanned")
    {
        InitializeComponent();
        _ocrService = ocrService;
        _localFilePath = localFilePath;
        _messageToken = messageToken;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        CapturedImage.Source = ImageSource.FromFile(_localFilePath);
        TextOverlayLayout.SizeChanged += OnLayoutSizeChanged;
        CropOverlayLayout.SizeChanged += OnCropLayoutSizeChanged;

        try
        {
            _pendingBlocks = await _ocrService.ExtractTextBlocksAsync(_localFilePath);
            DrawOcrBoxes();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OCR Failed: {ex.Message}");
        }
    }

    // FIX 3: Explicitly tear down resources and unhook events to prevent silent memory ballooning
    // each time the driver enters and exits the scanning page.
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        TextOverlayLayout.SizeChanged -= OnLayoutSizeChanged;
        CropOverlayLayout.SizeChanged -= OnCropLayoutSizeChanged;
        CapturedImage.Source = null;
    }

    private void OnLayoutSizeChanged(object? sender, EventArgs e)
    {
        DrawOcrBoxes();
    }

    private void DrawOcrBoxes()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_isDrawn || _pendingBlocks == null) return;

            double layoutWidth = TextOverlayLayout.Width;
            double layoutHeight = TextOverlayLayout.Height;

            if (layoutWidth <= 0 || layoutHeight <= 0) return;

            _isDrawn = true;
            TextOverlayLayout.SizeChanged -= OnLayoutSizeChanged;
            TextOverlayLayout.Children.Clear();

            var seenCandidates = new HashSet<string>();
            foreach (var block in _pendingBlocks)
            {
                var candidate = NormalizeOdometerCandidate(block.Text);
                if (string.IsNullOrWhiteSpace(candidate) || !seenCandidates.Add(candidate)) continue;

                var textBtn = new Button
                {
                    Text = candidate,
                    BackgroundColor = Colors.Green.WithAlpha(0.4f),
                    TextColor = Colors.White,
                    Padding = new Thickness(0),
                    FontSize = 12,
                    CornerRadius = 4,
                    LineBreakMode = LineBreakMode.NoWrap
                };

                double exactX = block.BoundingBox.X * layoutWidth;
                double exactY = block.BoundingBox.Y * layoutHeight;
                double exactWidth = block.BoundingBox.Width * layoutWidth;
                double exactHeight = block.BoundingBox.Height * layoutHeight;

                var preciseBounds = new Rect(exactX - 8, exactY - 8, exactWidth + 16, exactHeight + 16);

                AbsoluteLayout.SetLayoutBounds(textBtn, preciseBounds);
                AbsoluteLayout.SetLayoutFlags(textBtn, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.None);

                textBtn.Clicked += async (s, args) =>
                {
                    if (s is Button btn) btn.IsEnabled = false;

                    CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send<OdometerScannedData, string>(new OdometerScannedData
                    {
                        OdometerText = candidate,
                        PhotoFilePath = _localFilePath
                    }, _messageToken);

                    await Navigation.PopModalAsync();
                };

                TextOverlayLayout.Children.Add(textBtn);
            }
        });
    }

    private async void OnCancelClicked(object sender, EventArgs e)
    {
        if (sender is Button btn) btn.IsEnabled = false;
        await Navigation.PopModalAsync();
    }

    // Seven-segment LCD odometer digits are a known hard case for general-purpose OCR (ML
    // Kit's text model is trained on normal printed/handwritten glyphs, not segmented-display
    // numerals) - when detection genuinely finds nothing usable, retaking the photo over and
    // over won't fix that. Always offer a way to type the reading in directly instead of
    // leaving the driver stuck.
    private async void OnEnterManuallyClicked(object sender, EventArgs e)
    {
        string? typed = await DisplayPromptAsync(
            "Enter odometer reading",
            "Type the number shown on the dashboard.",
            accept: "Use this",
            cancel: "Back",
            keyboard: Keyboard.Numeric,
            maxLength: 7);

        var digitsOnly = Regex.Replace(typed ?? string.Empty, "[^0-9]", string.Empty);
        if (string.IsNullOrWhiteSpace(digitsOnly))
        {
            return;
        }

        SendResultAndClose(digitsOnly);
    }

    private void OnOcrModeClicked(object sender, EventArgs e) => SetMode(ScanMode.Ocr);

    private void OnLcdModeClicked(object sender, EventArgs e) => SetMode(ScanMode.Lcd);

    private void SetMode(ScanMode mode)
    {
        _mode = mode;
        TextOverlayLayout.IsVisible = mode == ScanMode.Ocr;
        CropOverlayLayout.IsVisible = mode == ScanMode.Lcd;
        ReadDigitsButton.IsVisible = mode == ScanMode.Lcd;
        InstructionLabel.Text = mode == ScanMode.Ocr
            ? "Tap the highlighted numbers above to select."
            : "Drag the box over the digits, pinch to resize, then tap Read Digits.";

        OcrModeButton.BackgroundColor = mode == ScanMode.Ocr ? Color.FromArgb("#1E5C7A") : Color.FromArgb("#333333");
        LcdModeButton.BackgroundColor = mode == ScanMode.Lcd ? Color.FromArgb("#1E5C7A") : Color.FromArgb("#333333");

        if (mode == ScanMode.Lcd)
        {
            InitializeCropBoxIfNeeded();
        }
    }

    private void OnCropLayoutSizeChanged(object? sender, EventArgs e)
    {
        if (_mode == ScanMode.Lcd)
        {
            InitializeCropBoxIfNeeded();
        }
    }

    // Default the box to roughly where a dashboard's digital readout usually sits - centered
    // horizontally, lower-middle vertically - so most drivers only need to nudge/resize it
    // rather than build it from nothing.
    private void InitializeCropBoxIfNeeded()
    {
        double layoutWidth = CropOverlayLayout.Width;
        double layoutHeight = CropOverlayLayout.Height;
        if (layoutWidth <= 0 || layoutHeight <= 0 || _cropBoxInitialized) return;

        _cropBoxInitialized = true;
        _cropBoxInitialBounds = new Rect(
            layoutWidth * 0.25, layoutHeight * 0.55,
            layoutWidth * 0.50, layoutHeight * 0.12);

        AbsoluteLayout.SetLayoutBounds(CropBox, _cropBoxInitialBounds);
        AbsoluteLayout.SetLayoutFlags(CropBox, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.None);
        CropBox.TranslationX = 0;
        CropBox.TranslationY = 0;
        CropBox.Scale = 1.0;
        _cropTranslateX = 0;
        _cropTranslateY = 0;
    }

    private void OnCropBoxPanUpdated(object sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Running:
                CropBox.TranslationX = _cropTranslateX + e.TotalX;
                CropBox.TranslationY = _cropTranslateY + e.TotalY;
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _cropTranslateX = CropBox.TranslationX;
                _cropTranslateY = CropBox.TranslationY;
                break;
        }
    }

    private void OnCropBoxPinchUpdated(object sender, PinchGestureUpdatedEventArgs e)
    {
        switch (e.Status)
        {
            case GestureStatus.Started:
                _cropScaleAtPinchStart = CropBox.Scale;
                break;
            case GestureStatus.Running:
                double newScale = _cropScaleAtPinchStart * e.Scale;
                CropBox.Scale = Math.Clamp(newScale, 0.4, 3.0);
                break;
        }
    }

    private async void OnReadDigitsClicked(object sender, EventArgs e)
    {
        try
        {
            // Combine the box's initial layout bounds with the pan/pinch transforms applied
            // since (Scale anchors at the box's own center by default) to get its effective
            // on-screen rect, then map that linearly onto the underlying bitmap's actual
            // pixel dimensions - valid because CapturedImage uses Aspect="Fill" (no
            // letterboxing), same assumption DrawOcrBoxes already relies on for its boxes.
            double centerX = _cropBoxInitialBounds.X + _cropBoxInitialBounds.Width / 2 + CropBox.TranslationX;
            double centerY = _cropBoxInitialBounds.Y + _cropBoxInitialBounds.Height / 2 + CropBox.TranslationY;
            double effectiveWidth = _cropBoxInitialBounds.Width * CropBox.Scale;
            double effectiveHeight = _cropBoxInitialBounds.Height * CropBox.Scale;

            double layoutWidth = CropOverlayLayout.Width;
            double layoutHeight = CropOverlayLayout.Height;
            if (layoutWidth <= 0 || layoutHeight <= 0) return;

            using var stream = File.OpenRead(_localFilePath);
            using var bitmap = SKBitmap.Decode(stream);
            if (bitmap == null) return;

            double scaleX = bitmap.Width / layoutWidth;
            double scaleY = bitmap.Height / layoutHeight;

            int px = (int)((centerX - effectiveWidth / 2) * scaleX);
            int py = (int)((centerY - effectiveHeight / 2) * scaleY);
            int pw = (int)(effectiveWidth * scaleX);
            int ph = (int)(effectiveHeight * scaleY);

            px = Math.Clamp(px, 0, bitmap.Width - 1);
            py = Math.Clamp(py, 0, bitmap.Height - 1);
            pw = Math.Clamp(pw, 1, bitmap.Width - px);
            ph = Math.Clamp(ph, 1, bitmap.Height - py);

            using var crop = new SKBitmap(pw, ph);
            using (var canvas = new SKCanvas(crop))
            {
                canvas.DrawBitmap(bitmap, new SKRectI(px, py, px + pw, py + ph), new SKRect(0, 0, pw, ph));
            }

            var result = SevenSegmentDecoder.Decode(crop, expectedDigitCount: 6);

            if (result.Digits.Count == 0)
            {
                await DisplayAlert("Nothing found", "No digits were detected in that box. Try repositioning it tighter around just the readout, or use manual entry instead.", "OK");
                return;
            }

            await ConfirmDecodedResultAsync(result);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LCD Decode Error: {ex.Message}");
            await DisplayAlert("Error", "Something went wrong reading that region. Please try again or enter the number manually.", "OK");
        }
    }

    // Shows the decoded digits for confirmation before using them - low-confidence digits
    // (see SevenSegmentDecoder.DigitResult.IsConfident) are called out explicitly rather than
    // silently trusted, since this is a deterministic-but-imperfect classical decode, not a
    // guaranteed-correct read. Seven-segment LCD decoding won't catch every photo (some
    // readings just won't come through cleanly) - manual entry is always one tap away rather
    // than trying to force a perfect automatic read.
    private async Task ConfirmDecodedResultAsync(SevenSegmentDecoder.DecodeResult result)
    {
        string text = result.Text;
        string message = result.AllConfident
            ? $"Read: {text}"
            : $"Read: {text}\n\nNote: some digits were uncertain (marked with a low-confidence read) - double check against the photo before confirming.";

        bool useIt = await DisplayAlert("Confirm reading", message, "Use this", "Edit / Retry");
        if (useIt && !text.Contains('?'))
        {
            SendResultAndClose(text);
            return;
        }

        // Either flagged as unreliable ('?' present) or the driver wants to correct it -
        // let them type the final value directly rather than forcing another frame attempt.
        string? typed = await DisplayPromptAsync(
            "Enter odometer reading",
            "Type the correct number shown on the dashboard.",
            accept: "Use this",
            cancel: "Cancel",
            initialValue: text.Replace("?", string.Empty),
            keyboard: Keyboard.Numeric,
            maxLength: 7);

        var digitsOnly = Regex.Replace(typed ?? string.Empty, "[^0-9]", string.Empty);
        if (!string.IsNullOrWhiteSpace(digitsOnly))
        {
            SendResultAndClose(digitsOnly);
        }
    }

    private void SendResultAndClose(string odometerText)
    {
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send<OdometerScannedData, string>(new OdometerScannedData
        {
            OdometerText = odometerText,
            PhotoFilePath = _localFilePath
        }, _messageToken);

        _ = Navigation.PopModalAsync();
    }

    private static string? NormalizeOdometerCandidate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var cleaned = text.ToUpperInvariant()
            .Replace('O', '0').Replace('D', '0')
            .Replace('I', '1').Replace('L', '1')
            .Replace('S', '5').Replace('B', '8');

        cleaned = Regex.Replace(cleaned, "[^0-9]", string.Empty);

        // A car odometer is always 5-6 digits (this app's spec examples show a fixed 6-digit
        // display, e.g. "091308" - leading zeros included, not trimmed). A dashboard's own
        // printed dial numbers (10, 20 ... 120, 140) are never more than 3 digits, so this
        // range alone rules out effectively every false-positive candidate the speedometer/
        // tachometer/gauge markings would otherwise produce - previously 3-7 digits, which let
        // every 3-digit dial number (100, 110, 120...) through as a "candidate" alongside the
        // real reading.
        if (cleaned.Length < 5 || cleaned.Length > 6) return null;
        if (cleaned.All(c => c == cleaned[0])) return null;

        return cleaned;
    }
}

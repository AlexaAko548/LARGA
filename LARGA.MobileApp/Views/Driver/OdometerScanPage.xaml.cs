using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
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
            if (!HasReading(_pendingBlocks))
            {
                // Nothing that looks like an odometer: read an enhanced copy (upscaled, high
                // contrast) once more before sending the driver to manual entry.
                string? enhanced = await Task.Run(() => OcrImageEnhancer.CreateEnhancedCopy(_localFilePath));
                if (enhanced != null)
                {
                    try
                    {
                        var second = await _ocrService.ExtractTextBlocksAsync(enhanced);
                        if (HasReading(second))
                        {
                            _pendingBlocks = second;
                        }
                    }
                    finally
                    {
                        try { File.Delete(enhanced); } catch { /* cache file */ }
                    }
                }
            }

            if (!HasReading(_pendingBlocks))
            {
                InstructionLabel.Text = "No odometer reading found. Try Frame the Digits, or enter it manually.";
            }
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
            foreach (var (text, box) in ReadingRegions(_pendingBlocks))
            {
                var candidate = OdometerReadingParser.Candidates(text).FirstOrDefault(c => !seenCandidates.Contains(c));
                if (candidate == null) continue;
                seenCandidates.Add(candidate);

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

                double exactX = box.X * layoutWidth;
                double exactY = box.Y * layoutHeight;
                double exactWidth = box.Width * layoutWidth;
                double exactHeight = box.Height * layoutHeight;

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
        try
        {
            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Close odometer scan failed: {ex.Message}");
            if (sender is Button again) again.IsEnabled = true;
        }
    }

    // Seven-segment LCD odometer digits are a known hard case for general-purpose OCR (ML
    // Kit's text model is trained on normal printed/handwritten glyphs, not segmented-display
    // numerals) - when detection genuinely finds nothing usable, retaking the photo over and
    // over won't fix that. Always offer a way to type the reading in directly instead of
    // leaving the driver stuck.
    private async void OnEnterManuallyClicked(object sender, EventArgs e)
    {
        try
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Manual odometer entry failed: {ex.Message}");
        }
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
        ReadDigitsButton.IsEnabled = false;
        string? cropPath = null;
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

            string localFilePath = _localFilePath;
            using var crop = await Task.Run(() => CropFrame(localFilePath, centerX, centerY, effectiveWidth, effectiveHeight, layoutWidth, layoutHeight));
            if (crop == null) return;

            // Most odometers (mechanical drums, white/amber backlit displays, digital clusters)
            // show ordinary printed digits, which ML Kit reads well once it only sees the framed
            // readout instead of the whole dashboard. The seven-segment decoder is the fallback
            // for the green segmented LCDs ML Kit can't read.
            cropPath = await Task.Run(() => SaveFramedCrop(crop));
            string? ocrReading = cropPath == null ? null : await ReadFramedDigitsWithOcrAsync(cropPath);
            if (ocrReading != null)
            {
                await ConfirmReadingAsync(ocrReading, allConfident: true);
                return;
            }

            var result = await Task.Run(() => SevenSegmentDecoder.Decode(crop, expectedDigitCount: 6));

            if (result.Digits.Count == 0)
            {
                await DisplayAlert("Nothing found", "No digits were detected in that box. Try repositioning it tighter around just the readout, or use manual entry instead.", "OK");
                return;
            }

            await ConfirmReadingAsync(result.Text, result.AllConfident);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LCD Decode Error: {ex.Message}");
            await DisplayAlert("Error", "Something went wrong reading that region. Please try again or enter the number manually.", "OK");
        }
        finally
        {
            ReadDigitsButton.IsEnabled = true;
            if (cropPath != null)
            {
                try { File.Delete(cropPath); } catch { /* cache file */ }
            }
        }
    }

    private static SKBitmap? CropFrame(string photoPath, double centerX, double centerY, double effectiveWidth, double effectiveHeight, double layoutWidth, double layoutHeight)
    {
        using var stream = File.OpenRead(photoPath);
        using var bitmap = SKBitmap.Decode(stream);
        if (bitmap == null) return null;

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

        var crop = new SKBitmap(pw, ph);
        using (var canvas = new SKCanvas(crop))
        {
            canvas.DrawBitmap(bitmap, new SKRectI(px, py, px + pw, py + ph), new SKRect(0, 0, pw, ph));
        }
        return crop;
    }

    /// <summary>The framed region as a cache JPEG for ML Kit, with a margin in the region's own
    /// average color - text recognition misses glyphs that touch the image edge, and a driver
    /// frames the digits tightly.</summary>
    private static string? SaveFramedCrop(SKBitmap crop)
    {
        try
        {
            long r = 0, g = 0, b = 0, n = 0;
            int step = Math.Max(1, Math.Min(crop.Width, crop.Height) / 50);
            for (int y = 0; y < crop.Height; y += step)
            {
                for (int x = 0; x < crop.Width; x += step)
                {
                    SKColor c = crop.GetPixel(x, y);
                    r += c.Red; g += c.Green; b += c.Blue; n++;
                }
            }
            var fill = n == 0 ? SKColors.Black : new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n));

            int margin = Math.Max(8, crop.Height / 4);
            using var padded = new SKBitmap(crop.Width + margin * 2, crop.Height + margin * 2);
            using (var canvas = new SKCanvas(padded))
            {
                canvas.Clear(fill);
                canvas.DrawBitmap(crop, margin, margin);
            }

            string target = Path.Combine(FileSystem.CacheDirectory, $"odometer_frame_{Guid.NewGuid():N}.jpg");
            using var image = SKImage.FromBitmap(padded);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
            using (var output = File.OpenWrite(target))
            {
                data.SaveTo(output);
            }
            return target;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Save framed crop failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>The reading ML Kit finds in the framed crop - the enhanced (upscaled, high
    /// contrast) copy first since a framed readout is small - or null when it finds none.</summary>
    private async Task<string?> ReadFramedDigitsWithOcrAsync(string cropPath)
    {
        string? enhanced = await Task.Run(() => OcrImageEnhancer.CreateEnhancedCopy(cropPath));
        try
        {
            foreach (string? path in new[] { enhanced, cropPath })
            {
                if (path == null) continue;
                string? reading = FramedReading(await _ocrService.ExtractTextBlocksAsync(path));
                if (reading != null) return reading;
            }
            return null;
        }
        finally
        {
            if (enhanced != null)
            {
                try { File.Delete(enhanced); } catch { /* cache file */ }
            }
        }
    }

    private static string? FramedReading(List<OcrTextBlock>? blocks)
    {
        if (blocks == null || blocks.Count == 0) return null;

        var lines = ReadingRegions(blocks).Select(r => r.Text).ToList();
        string? best = OdometerReadingParser.Candidates(lines).FirstOrDefault();
        if (best != null) return best;

        // The driver framed just the readout, so a shorter number (a low-mileage unit, or a
        // drum partly hidden behind the needle) is still what they pointed at; the confirm
        // dialog lets them correct it.
        string digits = lines
            .Select(l => new string((l ?? string.Empty).Where(char.IsDigit).ToArray()))
            .OrderByDescending(d => d.Length)
            .FirstOrDefault() ?? string.Empty;
        return digits.Length is >= 3 and <= 7 ? digits : null;
    }

    // Shows the read digits for confirmation before using them - low-confidence digits
    // (see SevenSegmentDecoder.DigitResult.IsConfident) are called out explicitly rather than
    // silently trusted, since neither ML Kit nor the classical decode is a guaranteed-correct
    // read. Some readings just won't come through cleanly - manual entry is always one tap
    // away rather than trying to force a perfect automatic read.
    private async Task ConfirmReadingAsync(string text, bool allConfident)
    {
        string message = allConfident
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

    private static bool HasReading(List<OcrTextBlock>? blocks) =>
        blocks != null && ReadingRegions(blocks).Any(r => OdometerReadingParser.Candidates(r.Text).Count > 0);

    /// <summary>Each OCR line with its own box (a block when the engine reported no lines), so a
    /// candidate button sits on the reading itself rather than over a whole block.</summary>
    private static IEnumerable<(string Text, Rect Box)> ReadingRegions(List<OcrTextBlock> blocks) =>
        blocks.SelectMany(b => b.Lines is { Count: > 0 }
            ? b.Lines.Select(l => (l.Text, l.BoundingBox))
            : new[] { (b.Text ?? string.Empty, b.BoundingBox) });
}

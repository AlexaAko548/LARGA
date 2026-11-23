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
    private List<OdometerPhotoReader.Found>? _pendingReadings;
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
            _pendingReadings = await FindReadingsAsync(_localFilePath);
            if (_pendingReadings.Count == 0)
            {
                // Nothing that looks like an odometer: read an enhanced copy (upscaled, high
                // contrast) once more before sending the driver to manual entry.
                string? enhanced = await Task.Run(() => OcrImageEnhancer.CreateEnhancedCopy(_localFilePath));
                if (enhanced != null)
                {
                    try
                    {
                        var second = await FindReadingsAsync(enhanced);
                        if (second.Count > 0)
                        {
                            _pendingReadings = second;
                        }
                    }
                    finally
                    {
                        try { File.Delete(enhanced); } catch { /* cache file */ }
                    }
                }
            }

            if (_pendingReadings.Count == 0)
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
            if (_isDrawn || _pendingReadings == null) return;

            double layoutWidth = TextOverlayLayout.Width;
            double layoutHeight = TextOverlayLayout.Height;

            if (layoutWidth <= 0 || layoutHeight <= 0) return;

            _isDrawn = true;
            TextOverlayLayout.SizeChanged -= OnLayoutSizeChanged;
            TextOverlayLayout.Children.Clear();

            foreach (var found in _pendingReadings)
            {
                var reading = found.Reading;
                var box = found.Region;

                // Orange: ML Kit and the segment decoder disagreed (or only one read it), so a
                // tap asks the driver to confirm instead of taking it straight away.
                var textBtn = new Button
                {
                    Text = reading.Text,
                    BackgroundColor = (reading.Confident ? Colors.Green : Colors.Orange).WithAlpha(0.4f),
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
                    if (s is not Button btn) return;
                    btn.IsEnabled = false;

                    if (reading.Confident)
                    {
                        SendResultAndClose(reading.Text);
                        return;
                    }

                    await ConfirmReadingAsync(reading.Text, allConfident: false);
                    btn.IsEnabled = true;
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

            // ML Kit reads the framed readout well once it doesn't see the whole dashboard, but
            // on a segmented LCD it reads a 6 as a 5; the seven-segment decoder checks it digit
            // by digit, and reads green LCDs ML Kit can't read at all.
            cropPath = await Task.Run(() => SaveFramedCrop(crop));
            var passes = cropPath == null ? new List<List<string?>>() : await ReadFramedLinesAsync(cropPath);
            var (reading, decoded) = await Task.Run(() => OdometerPhotoReader.ReadFramed(crop, passes));

            if (reading is { } read)
            {
                await ConfirmReadingAsync(read.Text, read.Confident);
                return;
            }

            if (decoded.Digits.Count == 0)
            {
                await DisplayAlert("Nothing found", "No digits were detected in that box. Try repositioning it tighter around just the readout, or use manual entry instead.", "OK");
                return;
            }

            await ConfirmReadingAsync(decoded.Text, decoded.AllConfident);
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

    /// <summary>ML Kit's lines on the framed crop, one list per pass: the enhanced (upscaled, high
    /// contrast) copy first since a framed readout is small, then the crop itself unless the
    /// first pass already holds a reading (see OdometerReadingParser.FramedReading).</summary>
    private async Task<List<List<string?>>> ReadFramedLinesAsync(string cropPath)
    {
        var passes = new List<List<string?>>();
        string? enhanced = await Task.Run(() => OcrImageEnhancer.CreateEnhancedCopy(cropPath));
        try
        {
            foreach (string? path in new[] { enhanced, cropPath })
            {
                if (path == null) continue;
                var lines = ReadingRegions(await _ocrService.ExtractTextBlocksAsync(path)).Select(r => (string?)r.Text).ToList();
                passes.Add(lines);
                if (OdometerReadingParser.Candidates(lines).Count > 0) break;
            }
            return passes;
        }
        finally
        {
            if (enhanced != null)
            {
                try { File.Delete(enhanced); } catch { /* cache file */ }
            }
        }
    }

    /// <summary>The readings ML Kit finds on <paramref name="ocrPath"/> (the photo, or its
    /// enhanced copy - same layout), each checked by the seven-segment decoder on the photo.</summary>
    private async Task<List<OdometerPhotoReader.Found>> FindReadingsAsync(string ocrPath)
    {
        var blocks = await _ocrService.ExtractTextBlocksAsync(ocrPath);
        var regions = ReadingRegions(blocks)
            .Select(r => new OdometerPhotoReader.Region(r.Text, r.Box.X, r.Box.Y, r.Box.Width, r.Box.Height))
            .ToList();
        return await Task.Run(() =>
        {
            using var photo = OcrImageEnhancer.LoadUpright(_localFilePath);
            return OdometerPhotoReader.Read(photo, regions);
        });
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

    /// <summary>Each OCR line with its own box (a block when the engine reported no lines), so a
    /// candidate button sits on the reading itself rather than over a whole block.</summary>
    private static IEnumerable<(string Text, Rect Box)> ReadingRegions(List<OcrTextBlock> blocks) =>
        blocks.SelectMany(b => b.Lines is { Count: > 0 }
            ? b.Lines.Select(l => (l.Text, l.BoundingBox))
            : new[] { (b.Text ?? string.Empty, b.BoundingBox) });
}

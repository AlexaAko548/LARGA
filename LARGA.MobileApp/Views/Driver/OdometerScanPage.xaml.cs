using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.ApplicationModel;
using LARGA.MobileApp.Services;
using LARGA.MobileApp.ViewModels.Driver;
using System;
using System.Collections.Generic;
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

        // FIX 1: Hook into SizeChanged to guarantee the layout is fully measured before drawing
        TextOverlayLayout.SizeChanged += OnLayoutSizeChanged;

        try
        {
            _pendingBlocks = await _ocrService.ExtractTextBlocksAsync(_localFilePath);
            DrawOcrBoxes(); // Attempt draw if layout is somehow already ready
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OCR Failed: {ex.Message}");
        }
    }

    private void OnLayoutSizeChanged(object? sender, EventArgs e)
    {
        DrawOcrBoxes();
    }

    private void DrawOcrBoxes()
    {
        // FIX 2: Ensure all UI modifications happen strictly on the Main Thread
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_isDrawn || _pendingBlocks == null) return;

            double layoutWidth = TextOverlayLayout.Width;
            double layoutHeight = TextOverlayLayout.Height;

            // FIX 3: Prevent negative bounds crash by validating layout measurement
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
        // FIX 4: Prevent double-tap fatal crash on the Cancel button
        if (sender is Button btn) btn.IsEnabled = false;
        await Navigation.PopModalAsync();
    }

    private static string? NormalizeOdometerCandidate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var cleaned = text.ToUpperInvariant()
            .Replace('O', '0').Replace('D', '0')
            .Replace('I', '1').Replace('L', '1')
            .Replace('S', '5').Replace('B', '8');

        cleaned = Regex.Replace(cleaned, "[^0-9]", string.Empty);
        if (cleaned.Length < 3 || cleaned.Length > 7) return null;
        if (cleaned.All(c => c == cleaned[0])) return null;

        return cleaned;
    }
}
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using LARGA.MobileApp.Services;
using LARGA.MobileApp.ViewModels.Driver;
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
    private readonly byte[] _imageBytes;

    public OdometerScanPage(IOcrService ocrService, byte[] imageBytes)
    {
        InitializeComponent();
        _ocrService = ocrService;
        _imageBytes = imageBytes;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        CapturedImage.Source = ImageSource.FromStream(() => new MemoryStream(_imageBytes));
        await Task.Delay(300);

        var detectedBlocks = await _ocrService.ExtractTextBlocksAsync(_imageBytes);

        double layoutWidth = TextOverlayLayout.Width;
        double layoutHeight = TextOverlayLayout.Height;

        TextOverlayLayout.Children.Clear();
        var seenCandidates = new HashSet<string>();
        foreach (var block in detectedBlocks)
        {
            var candidate = NormalizeOdometerCandidate(block.Text);
            if (string.IsNullOrWhiteSpace(candidate) || !seenCandidates.Add(candidate))
            {
                continue;
            }

            var textBtn = new Button
            {
                Text = candidate,
                BackgroundColor = Colors.Green.WithAlpha(0.4f),
                TextColor = Colors.White,
                Padding = new Thickness(0),
                FontSize = 12,
                CornerRadius = 4,
                // THE FIX: Prevent the text from being cut off or separated into multiple lines
                LineBreakMode = LineBreakMode.NoWrap
            };

            double exactX = block.BoundingBox.X * layoutWidth;
            double exactY = block.BoundingBox.Y * layoutHeight;
            double exactWidth = block.BoundingBox.Width * layoutWidth;
            double exactHeight = block.BoundingBox.Height * layoutHeight;

            var preciseBounds = new Rect(
                exactX - 8,
                exactY - 8,
                exactWidth + 16,
                exactHeight + 16
            );

            AbsoluteLayout.SetLayoutBounds(textBtn, preciseBounds);
            AbsoluteLayout.SetLayoutFlags(textBtn, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.None);

            textBtn.Clicked += async (s, args) =>
            {
                CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send<OdometerScannedData, string>(new OdometerScannedData
                {
                    OdometerText = candidate,
                    PhotoBytes = _imageBytes
                }, "OdometerScanned");
                await Navigation.PopModalAsync();
            };

            TextOverlayLayout.Children.Add(textBtn);
        }
    }

    private async void OnCancelClicked(object sender, EventArgs e)
    {
        await Navigation.PopModalAsync();
    }

    private static string? NormalizeOdometerCandidate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var cleaned = text.ToUpperInvariant()
            .Replace('O', '0')
            .Replace('D', '0')
            .Replace('I', '1')
            .Replace('L', '1')
            .Replace('S', '5')
            .Replace('B', '8');

        cleaned = Regex.Replace(cleaned, "[^0-9]", string.Empty);
        if (cleaned.Length < 3 || cleaned.Length > 7)
            return null;

        if (cleaned.All(c => c == cleaned[0]))
            return null;

        return cleaned;
    }
}
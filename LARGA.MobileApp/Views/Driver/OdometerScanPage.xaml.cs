using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using LARGA.MobileApp.Services;
using LARGA.SharedCore.Services;
using Plugin.Firebase.Auth;
using System;
using System.IO;
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
        foreach (var block in detectedBlocks)
        {
            var textBtn = new Button
            {
                Text = block.Text,
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
                CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(block.Text, "OdometerScanned");
                await Navigation.PopModalAsync();
            };

            TextOverlayLayout.Children.Add(textBtn);
        }
    }

    private async void OnCancelClicked(object sender, EventArgs e)
    {
        await Navigation.PopModalAsync();
    }
}
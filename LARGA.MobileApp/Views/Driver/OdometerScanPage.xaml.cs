using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;
using LARGA.MobileApp.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LARGA.MobileApp.Views.Driver;

public partial class OdometerScanPage : ContentPage
{
    private readonly IOcrService _ocrService;
    private bool _isScanning = false;

    public OdometerScanPage(IOcrService ocrService)
    {
        InitializeComponent();
        _ocrService = ocrService;
    }

    private void Camera_CamerasLoaded(object sender, EventArgs e)
    {
        LiveCamera.Camera = LiveCamera.Cameras.FirstOrDefault();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await LiveCamera.StartCameraAsync();
            _isScanning = true;
            StartLiveOcrLoop(); // Start the background extraction loop
        });
    }

    private async void StartLiveOcrLoop()
    {
        while (_isScanning)
        {
            // 1.5-second interval to prevent freezing the UI or overloading memory
            await Task.Delay(1500);

            try
            {
                // 1. Create a temporary path for the live camera snapshot
                string tempFilePath = Path.Combine(FileSystem.CacheDirectory, "live_frame.jpg");

                // 2. Silently pull the current frame from the camera stream
                var snapResult = await LiveCamera.SaveSnapShot(Camera.MAUI.ImageFormat.JPEG, tempFilePath);

                if (snapResult && File.Exists(tempFilePath))
                {
                    // 3. Extract the image bytes
                    byte[] imageBytes = File.ReadAllBytes(tempFilePath);

                    // 4. Feed real-life data into the ML Kit wrapper
                    var detectedBlocks = await _ocrService.ExtractTextBlocksAsync(imageBytes);

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        TextOverlayLayout.Children.Clear();

                        foreach (var block in detectedBlocks)
                        {
                            var textBtn = new Button
                            {
                                Text = block.Text,
                                BackgroundColor = Colors.Green.WithAlpha(0.5f),
                                TextColor = Colors.White,
                                Padding = 0
                            };

                            AbsoluteLayout.SetLayoutBounds(textBtn, block.BoundingBox);

                            textBtn.Clicked += async (s, args) => {
                                _isScanning = false;
                                await LiveCamera.StopCameraAsync();
                                await Shell.Current.GoToAsync("..", new Dictionary<string, object> {
                                    { "ScannedOdometer", block.Text }
                                });
                            };

                            TextOverlayLayout.Children.Add(textBtn);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OCR Loop Error: {ex.Message}");
            }
        }
    }

    private async void OnCancelClicked(object sender, EventArgs e)
    {
        _isScanning = false;
        await LiveCamera.StopCameraAsync();
        await Shell.Current.GoToAsync("..");
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _isScanning = false; // Prevent memory leaks when navigating away
    }
}
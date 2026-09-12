using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;
using Camera.MAUI;
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
    private CameraView _liveCamera;
    private bool _isScanning = false;

    public OdometerScanPage(IOcrService ocrService)
    {
        InitializeComponent();
        _ocrService = ocrService;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // 1. Dynamically generate the camera to force a pristine hardware surface
        _liveCamera = new CameraView
        {
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };

        _liveCamera.CamerasLoaded += Camera_CamerasLoaded;
        CameraContainer.Children.Insert(0, _liveCamera);
    }

    private void Camera_CamerasLoaded(object sender, EventArgs e)
    {
        if (_liveCamera.Cameras == null || _liveCamera.Cameras.Count == 0) return;

        // 2. Multi-Lens Fix: Force the primary rear HD camera, bypassing blurry macro lenses
        var backCameras = _liveCamera.Cameras.Where(c => c.Position == Camera.MAUI.CameraPosition.Back).ToList();
        _liveCamera.Camera = backCameras
            .OrderByDescending(c => c.AvailableResolutions?.Max(r => r.Width * r.Height) ?? 0)
            .FirstOrDefault() ?? _liveCamera.Cameras.FirstOrDefault();

        if (_liveCamera.Camera == null) return;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            // 3. Resolution Fix: Request a standard 16:9 HD preview buffer
            await _liveCamera.StartCameraAsync(new Size(1280, 720));

            await Task.Delay(800); // Allow physical lens to stabilize
            _liveCamera.ForceAutoFocus();

            _isScanning = true;
            StartLiveOcrLoop();
        });
    }

    private async void StartLiveOcrLoop()
    {
        while (_isScanning)
        {
            await Task.Delay(1500);

            try
            {
                string tempFilePath = Path.Combine(FileSystem.CacheDirectory, "live_frame.jpg");
                var snapResult = await _liveCamera.SaveSnapShot(Camera.MAUI.ImageFormat.JPEG, tempFilePath);

                if (snapResult && File.Exists(tempFilePath))
                {
                    byte[] imageBytes = File.ReadAllBytes(tempFilePath);
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

                            textBtn.Clicked += async (s, args) =>
                            {
                                await StopCameraSafelyAsync();
                                // Send data back to ViewModel via Messenger since we use Modals
                                CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(block.Text, "OdometerScanned");
                                await Navigation.PopModalAsync();
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
        await StopCameraSafelyAsync();
        await Navigation.PopModalAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _ = StopCameraSafelyAsync();
    }

    private async Task StopCameraSafelyAsync()
    {
        _isScanning = false;

        if (_liveCamera != null)
        {
            try
            {
                await _liveCamera.StopCameraAsync();
                _liveCamera.CamerasLoaded -= Camera_CamerasLoaded;

                // 4. Burn the surface cache to the ground when closing
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    _liveCamera.Handler?.DisconnectHandler();
                    CameraContainer.Children.Remove(_liveCamera);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to stop camera: {ex.Message}");
            }
            finally
            {
                _liveCamera = null;
            }
        }
    }
}
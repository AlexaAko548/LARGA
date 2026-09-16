using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;
using LARGA.MobileApp.Services;
using LARGA.SharedCore.Services;
using Plugin.Firebase.Auth;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LARGA.MobileApp.Views.Driver;

public partial class OdometerScanPage : ContentPage
{
    private readonly IOcrService _ocrService;
    private readonly IPhotoStorageService _photoStorageService;
    private bool _isScanning = false;

    // Guards against a double-tap on two overlapping detected-number buttons firing this
    // block twice (two concurrent uploads racing to navigate away).
    private bool _isSubmittingSelection = false;

    public OdometerScanPage(IOcrService ocrService, IPhotoStorageService photoStorageService)
    {
        InitializeComponent();
        _ocrService = ocrService;
        _photoStorageService = photoStorageService;
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
                // Camera.MAUI's SaveSnapShot() just grabs whatever frame is currently on the
                // sensor - it does not itself trigger a refocus. The camera only auto-focuses
                // once, when the preview starts, so every snapshot after the first keeps using
                // that same locked focus distance. The moment the phone is repositioned to
                // reframe the odometer (i.e. a "retake"), the lens is now focused on the wrong
                // distance and stays that way for the rest of the session - which is exactly
                // why the first capture is always sharp but later ones come out blurry.
                // ForceAutoFocus() re-triggers autofocus; give it a moment to settle before
                // the snapshot is actually taken.
                LiveCamera.ForceAutoFocus();
                await Task.Delay(300);

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
                                if (_isSubmittingSelection) return;
                                _isSubmittingSelection = true;

                                _isScanning = false;
                                await LiveCamera.StopCameraAsync();

                                // Upload only the one frame the driver actually confirmed (the
                                // frame OCR read this number from), not every throwaway frame
                                // the loop grabs every 1.5s - imageBytes here is that same frame.
                                var navParams = new Dictionary<string, object> { { "ScannedOdometer", block.Text } };
                                string driverId = CrossFirebaseAuth.Current.CurrentUser?.Uid ?? "unknown_driver";
                                string path = $"odometer_photos/{driverId}/{DateTime.UtcNow:yyyyMMddHHmmss}.jpg";
                                string? photoUrl = await _photoStorageService.UploadPhotoAsync(path, imageBytes);
                                if (!string.IsNullOrEmpty(photoUrl))
                                {
                                    navParams["OdometerPhotoUrl"] = photoUrl;
                                }

                                await Shell.Current.GoToAsync("..", navParams);
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

        // Cancel and "tap a detected number" already call StopCameraAsync explicitly
        // before navigating away, but the system back button / swipe-back gesture
        // bypasses both and only triggers OnDisappearing. Without stopping the camera
        // here too, this page's native camera session is never released - so the next
        // time odometer-scan is opened, the new CameraView has to contend with the
        // still-open orphaned session for the camera hardware, and the preview/capture
        // comes back degraded. That's why the first scan is always sharp but a retake
        // is blurry.
        _ = StopCameraSafelyAsync();
    }

    private async Task StopCameraSafelyAsync()
    {
        try
        {
            await LiveCamera.StopCameraAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to stop camera on disappearing: {ex.Message}");
        }
    }
}
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using LARGA.MobileApp.Services;
using LARGA.SharedCore;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace LARGA.MobileApp.Views.Manager;

/// <summary>
/// Captures a GCash e-receipt, reads it with OCR, and shows the amount, date and reference number as read-only
/// details. Nothing is sent back to the payment form until every field is read and the manager confirms.
/// </summary>
public partial class ScanEReceiptPage : ContentPage
{
    private readonly EReceiptOcrService _ocr;
    private string? _photoPath;
    private EReceiptData? _scanned;

    public ScanEReceiptPage(EReceiptOcrService ocr)
    {
        InitializeComponent();
        _ocr = ocr;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_scanned is null)
        {
            await StartCameraAsync();
        }
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await StopCameraAsync();
    }

    private async void Camera_CamerasLoaded(object? sender, EventArgs e)
    {
        if (_scanned is null)
        {
            await StartCameraAsync();
        }
    }

    private async Task StartCameraAsync()
    {
        if (ReceiptCamera.Cameras.Count == 0) return;

        ReceiptCamera.Camera = ReceiptCamera.Cameras.FirstOrDefault(c => c.Position == Camera.MAUI.CameraPosition.Back)
                               ?? ReceiptCamera.Cameras.FirstOrDefault();
        ReceiptCamera.ZoomFactor = 0f;
        await ReceiptCamera.StopCameraAsync();
        await ReceiptCamera.StartCameraAsync();
    }

    private async Task StopCameraAsync()
    {
        if (ReceiptCamera.Cameras.Count > 0)
        {
            await ReceiptCamera.StopCameraAsync();
        }
    }

    private async void OnCaptureClicked(object? sender, EventArgs e)
    {
        // Once the receipt has been read, the same button confirms it.
        if (_scanned is { IsComplete: true } complete)
        {
            await SendResultAsync(complete);
            return;
        }

        BtnCapture.IsEnabled = false;
        BtnCapture.Text = "READING RECEIPT...";

        string path = Path.Combine(FileSystem.CacheDirectory, $"ereceipt_{Guid.NewGuid():N}.jpg");
        bool saved = await ReceiptCamera.SaveSnapShot(Camera.MAUI.ImageFormat.JPEG, path);
        if (!saved || !File.Exists(path))
        {
            ResetCapture("Capture failed. Please try again.");
            return;
        }

        _photoPath = path;
        CapturedReceiptPreview.Source = ImageSource.FromFile(path);
        CapturedReceiptPreview.IsVisible = true;
        ReceiptCamera.IsVisible = false;
        await StopCameraAsync();

        _scanned = await _ocr.ReadAsync(path, PhilippineTime.Now);
        ShowScan(_scanned);
    }

    private async void OnRetakeClicked(object? sender, EventArgs e)
    {
        _scanned = null;
        _photoPath = null;
        CapturedReceiptPreview.IsVisible = false;
        CapturedReceiptPreview.Source = null;
        ReceiptCamera.IsVisible = true;

        LblAmount.Text = "---";
        LblDate.Text = "---";
        LblReference.Text = "---";
        BtnRetake.IsVisible = false;
        ResetCapture("Line up the receipt and capture it.");

        await StartCameraAsync();
    }

    private async void OnCancelClicked(object? sender, TappedEventArgs e)
    {
        await StopCameraAsync();
        await Navigation.PopModalAsync();
    }

    private void ShowScan(EReceiptData data)
    {
        LblAmount.Text = data.Amount?.ToString("#,##0.00") ?? "---";
        LblDate.Text = data.Date?.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture) ?? "---";
        LblReference.Text = data.ReferenceNumber ?? "---";

        BtnRetake.IsVisible = true;
        BtnRetake.IsEnabled = true;

        if (data.IsComplete)
        {
            ScanStatusLabel.Text = "Receipt read. Check the details, then confirm.";
            BtnCapture.Text = "CONFIRM";
            BtnCapture.IsEnabled = true;
        }
        else
        {
            ScanStatusLabel.Text = "Some details weren't read. Retake the photo.";
            BtnCapture.Text = "NOT ALL DETAILS FOUND";
            BtnCapture.IsEnabled = false;
        }
    }

    private void ResetCapture(string message)
    {
        BtnCapture.Text = "CAPTURE";
        BtnCapture.IsEnabled = true;
        ScanStatusLabel.Text = message;
    }

    private async Task SendResultAsync(EReceiptData data)
    {
        WeakReferenceMessenger.Default.Send(new EReceiptScanResult(
            Amount: data.Amount!.Value,
            Date: data.Date!.Value,
            ReferenceNumber: data.ReferenceNumber!,
            PhotoFilePath: _photoPath ?? string.Empty));

        await StopCameraAsync();
        await Navigation.PopModalAsync();
    }
}

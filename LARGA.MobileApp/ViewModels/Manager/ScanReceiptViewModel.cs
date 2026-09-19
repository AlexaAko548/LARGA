using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using LARGA.MobileApp.Models;
using LARGA.MobileApp.Services;

namespace LARGA.MobileApp.ViewModels.Manager;

public partial class ScanReceiptViewModel : ObservableObject
{
    private readonly IOcrService _ocrService;
    private decimal? _parsedAmount;
    private DateTime? _parsedDate;
    private string _parsedReference = string.Empty;

    [ObservableProperty]
    private ImageSource? capturedImageSource;

    [ObservableProperty]
    private decimal scannedAmount;

    [ObservableProperty]
    private DateTime scannedDate = DateTime.Today;

    [ObservableProperty]
    private string scannedReference = string.Empty;

    [ObservableProperty]
    private bool isScanning = true;

    [ObservableProperty]
    private bool isProcessing;

    public bool HasCaptured => CapturedImageSource != null;

    public ScanReceiptViewModel(IOcrService ocrService)
    {
        _ocrService = ocrService;
    }

    [RelayCommand]
    public async Task CaptureAndProcessReceiptAsync()
    {
        bool hadExistingCapture = HasCaptured;
        try
        {
            FileResult? photo = await MediaPicker.Default.CapturePhotoAsync();
            if (photo == null)
            {
                IsScanning = !hadExistingCapture;
                return;
            }

            IsProcessing = true;
            IsScanning = true;

            // Reset displayed OCR values before processing a fresh capture.
            _parsedAmount = null;
            _parsedDate = null;
            _parsedReference = string.Empty;
            ScannedAmount = 0;
            ScannedDate = DateTime.Today;
            ScannedReference = string.Empty;

            using Stream stream = await photo.OpenReadAsync();

            CapturedImageSource = ImageSource.FromFile(photo.FullPath);

            byte[] imageBytes;
            using (var memory = new MemoryStream())
            {
                await stream.CopyToAsync(memory);
                imageBytes = memory.ToArray();
            }

            // Execute ML Kit text extraction then flatten to plain OCR text for the parser.
            List<OcrTextBlock> blocks = await _ocrService.ExtractTextBlocksAsync(imageBytes);
            string extractedText = string.Join(Environment.NewLine, blocks.Select(block => block.Text));
            ReceiptScanResult parsed = ReceiptOcrParser.Parse(extractedText);

            _parsedAmount = parsed.Amount;
            _parsedDate = parsed.Date;
            _parsedReference = parsed.ReferenceNumber ?? string.Empty;

            if (_parsedAmount.HasValue) ScannedAmount = _parsedAmount.Value;
            if (_parsedDate.HasValue) ScannedDate = _parsedDate.Value;
            if (!string.IsNullOrEmpty(_parsedReference)) ScannedReference = _parsedReference;

            IsScanning = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OCR Error] {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private async Task CaptureOrRetryAsync()
    {
        await CaptureAndProcessReceiptAsync();
    }

    [RelayCommand]
    private async Task ConfirmReceiptAsync()
    {
        WeakReferenceMessenger.Default.Send(new ReceiptScanPayload
        {
            Amount = _parsedAmount,
            Date = _parsedDate,
            ReferenceNumber = _parsedReference,
        }, "ReceiptScanned");

        await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        await Shell.Current.GoToAsync("..");
    }

    partial void OnCapturedImageSourceChanged(ImageSource? value)
    {
        OnPropertyChanged(nameof(HasCaptured));
    }
}
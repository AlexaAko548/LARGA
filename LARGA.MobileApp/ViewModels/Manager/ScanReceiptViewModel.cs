using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LARGA.MobileApp.Services;

namespace LARGA.MobileApp.ViewModels.Manager;

public partial class ScanReceiptViewModel : ObservableObject
{
    private readonly IOcrService _ocrService;

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

    public ScanReceiptViewModel(IOcrService ocrService)
    {
        _ocrService = ocrService;
    }

    [RelayCommand]
    public async Task CaptureAndProcessReceiptAsync()
    {
        try
        {
            FileResult? photo = await MediaPicker.Default.CapturePhotoAsync();
            if (photo == null) return;

            IsProcessing = true;
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

            if (parsed.Amount.HasValue) ScannedAmount = parsed.Amount.Value;
            if (parsed.Date.HasValue) ScannedDate = parsed.Date.Value;
            if (!string.IsNullOrEmpty(parsed.ReferenceNumber)) ScannedReference = parsed.ReferenceNumber;

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
    private async Task ConfirmReceiptAsync()
    {
        // Pass scanned fields back via Shell navigation parameters
        var navParams = new Dictionary<string, object>
        {
            { "Amount", ScannedAmount },
            { "Date", ScannedDate },
            { "Reference", ScannedReference }
        };

        await Shell.Current.GoToAsync("..", navParams);
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        await Shell.Current.GoToAsync("..");
    }
}
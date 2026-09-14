using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.MobileApp.Services;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.ViewModels.Driver;

/// <summary>
/// Drives both steps of the license scan flow in one page/viewmodel (Review captured photo,
/// then Verify OCR'd fields) rather than chaining two modal pages - simpler state to reason
/// about than juggling a two-deep modal stack, and mirrors the tab-toggle pattern ReportsPage
/// already uses (IsVehicleDefectTabSelected / IsFuelTabSelected) for "one page, two views".
/// </summary>
public class ScanDriverLicenseViewModel : BindableObject
{
    private readonly IOcrService _ocrService;
    private readonly string _targetUserId;
    private byte[] _imageBytes;
    private DriverLicenseTextParser.ParsedLicense? _parsed;

    private ImageSource? _capturedImage;
    public ImageSource? CapturedImage
    {
        get => _capturedImage;
        set { _capturedImage = value; OnPropertyChanged(); }
    }

    private bool _isReviewStep = true;
    public bool IsReviewStep
    {
        get => _isReviewStep;
        set { _isReviewStep = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsVerifyStep)); }
    }

    public bool IsVerifyStep => !IsReviewStep;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotBusy)); }
    }

    public bool IsNotBusy => !IsBusy;

    private string _nameDisplay = "--";
    public string NameDisplay { get => _nameDisplay; set { _nameDisplay = value; OnPropertyChanged(); } }

    private string _sexDisplay = "--";
    public string SexDisplay { get => _sexDisplay; set { _sexDisplay = value; OnPropertyChanged(); } }

    private string _dlCodesDisplay = "--";
    public string DlCodesDisplay { get => _dlCodesDisplay; set { _dlCodesDisplay = value; OnPropertyChanged(); } }

    private string _licenseNumberDisplay = "--";
    public string LicenseNumberDisplay { get => _licenseNumberDisplay; set { _licenseNumberDisplay = value; OnPropertyChanged(); } }

    private string _expiryDateDisplay = "--";
    public string ExpiryDateDisplay { get => _expiryDateDisplay; set { _expiryDateDisplay = value; OnPropertyChanged(); } }

    public ICommand RetakeCommand { get; }
    public ICommand ConfirmScanCommand { get; }
    public ICommand ConfirmAndSaveCommand { get; }
    public ICommand CancelCommand { get; }

    public ScanDriverLicenseViewModel(IOcrService ocrService, byte[] imageBytes, string targetUserId)
    {
        _ocrService = ocrService;
        _imageBytes = imageBytes;
        _targetUserId = targetUserId;
        CapturedImage = ImageSource.FromStream(() => new MemoryStream(_imageBytes));

        RetakeCommand = new Command(async () => await RetakeAsync());
        ConfirmScanCommand = new Command(async () => await RunOcrAsync());
        ConfirmAndSaveCommand = new Command(async () => await SaveAsync());
        CancelCommand = new Command(async () => await ClosePageAsync());
    }

    private async Task RetakeAsync()
    {
        try
        {
            if (!MediaPicker.Default.IsCaptureSupported) return;
            var photo = await MediaPicker.Default.CapturePhotoAsync();
            if (photo == null) return;

            using var stream = await photo.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            _imageBytes = buffer.ToArray();
            CapturedImage = ImageSource.FromStream(() => new MemoryStream(_imageBytes));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Retake License Photo Error: {ex.Message}");
        }
    }

    private async Task RunOcrAsync()
    {
        IsBusy = true;
        try
        {
            var blocks = await _ocrService.ExtractTextBlocksAsync(_imageBytes);
            _parsed = DriverLicenseTextParser.Parse(blocks.Select(b => b.Text));

            if (!_parsed.HasMinimumData)
            {
                await Shell.Current.DisplayAlert(
                    "Couldn't read that clearly",
                    "We couldn't make out the license number or expiry date. Try retaking the photo with better lighting and make sure it fills the frame.",
                    "OK");
                return;
            }

            NameDisplay = _parsed.FullName ?? "--";
            SexDisplay = _parsed.Sex ?? "--";
            DlCodesDisplay = _parsed.DlCodes ?? "--";
            LicenseNumberDisplay = _parsed.LicenseNumber ?? "--";
            ExpiryDateDisplay = _parsed.ExpiryDate?.ToString("MMM d, yyyy") ?? "--";

            IsReviewStep = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"License OCR Error: {ex.Message}");
            await Shell.Current.DisplayAlert("Error", "Something went wrong reading the photo. Please try again.", "OK");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync()
    {
        if (_parsed == null) return;

        if (string.IsNullOrWhiteSpace(_targetUserId)) return;

        IsBusy = true;
        try
        {
            var updates = new Dictionary<object, object>
            {
                ["licenseNumber"] = _parsed.LicenseNumber ?? string.Empty,
                ["licenseClassification"] = _parsed.DlCodes ?? string.Empty
            };
            if (_parsed.ExpiryDate != null)
            {
                updates["licenseExpiryDate"] = _parsed.ExpiryDate.Value;
            }

            await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(_targetUserId)
                .UpdateDataAsync(updates);

            await ClosePageAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Save License Error: {ex.Message}");
            await Shell.Current.DisplayAlert("Error", "Failed to save your license details. Please try again.", "OK");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static async Task ClosePageAsync()
    {
        if (Application.Current?.MainPage?.Navigation is { } navigation)
        {
            await navigation.PopModalAsync();
        }
    }
}

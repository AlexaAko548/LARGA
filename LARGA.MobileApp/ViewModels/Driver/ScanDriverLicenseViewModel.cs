using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.MobileApp.Services;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
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
    private string _localFilePath; // THE FIX: Store path instead of byte[]
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

    private string _nameDisplay = string.Empty;
    public string NameDisplay { get => _nameDisplay; set { _nameDisplay = value; OnPropertyChanged(); } }

    private string _sexDisplay = string.Empty;
    public string SexDisplay { get => _sexDisplay; set { _sexDisplay = value; OnPropertyChanged(); } }

    private string _dlCodesDisplay = string.Empty;
    public string DlCodesDisplay { get => _dlCodesDisplay; set { _dlCodesDisplay = value; OnPropertyChanged(); } }

    private string _licenseNumberDisplay = string.Empty;
    public string LicenseNumberDisplay { get => _licenseNumberDisplay; set { _licenseNumberDisplay = value; OnPropertyChanged(); } }

    private DateTime _expiryDate = DateTime.Today;
    public DateTime ExpiryDate { get => _expiryDate; set { _expiryDate = value; OnPropertyChanged(); } }

    public ICommand RetakeCommand { get; }
    public ICommand ConfirmScanCommand { get; }
    public ICommand ConfirmAndSaveCommand { get; }
    public ICommand CancelCommand { get; }

    // THE FIX: Constructor now accepts string localFilePath
    public ScanDriverLicenseViewModel(IOcrService ocrService, string localFilePath, string targetUserId)
    {
        _ocrService = ocrService;
        _localFilePath = localFilePath;
        _targetUserId = targetUserId;

        // Bind natively from file
        CapturedImage = ImageSource.FromFile(_localFilePath);

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

            string newLocalFilePath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid():N}_{photo.FileName}");

            using (var stream = await photo.OpenReadAsync())
            using (var localFileStream = File.OpenWrite(newLocalFilePath))
            {
                await stream.CopyToAsync(localFileStream);
            }

            TryDeleteCachedFile(_localFilePath);

            // THE FIX: Save retaken photo directly to disk
            _localFilePath = newLocalFilePath;

            CapturedImage = ImageSource.FromFile(_localFilePath);
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
            // THE FIX: Pass file path to OCR
            var blocks = await _ocrService.ExtractTextBlocksAsync(_localFilePath);
            _parsed = DriverLicenseTextParser.Parse(blocks.Select(b => b.Text));

            if (!_parsed.HasMinimumData)
            {
                await Shell.Current.DisplayAlert(
                    "Couldn't read that clearly",
                    "We couldn't make out the license number or expiry date. Try retaking the photo with better lighting and make sure it fills the frame.",
                    "OK");
                return;
            }

            var registeredName = await GetRegisteredNameAsync();
            if (!DriverLicenseTextParser.NamesLikelyMatch(registeredName, _parsed.FullName))
            {
                await Shell.Current.DisplayAlert(
                    "Name doesn't match",
                    $"This license appears to belong to \"{_parsed.FullName}\", but this account is registered as \"{registeredName}\". Please scan the license that belongs to this driver.",
                    "OK");
                return;
            }

            NameDisplay = _parsed.FullName ?? string.Empty;
            SexDisplay = _parsed.Sex ?? string.Empty;
            DlCodesDisplay = _parsed.DlCodes ?? string.Empty;
            LicenseNumberDisplay = _parsed.LicenseNumber ?? string.Empty;
            ExpiryDate = _parsed.ExpiryDate ?? DateTime.Today;

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

    private async Task<string?> GetRegisteredNameAsync()
    {
        if (string.IsNullOrWhiteSpace(_targetUserId)) return null;

        try
        {
            var doc = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(_targetUserId)
                .GetDocumentSnapshotAsync<DriverNameProxy>();
            return doc?.Data?.FullName;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Registered Name Lookup Error: {ex.Message}");
            return null;
        }
    }

    private class DriverNameProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("fullName")]
        public string FullName { get; set; } = string.Empty;
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_targetUserId)) return;

        IsBusy = true;
        try
        {
            var updates = new Dictionary<object, object>
            {
                ["licenseNumber"] = LicenseNumberDisplay,
                ["licenseClassification"] = DlCodesDisplay,
                ["licenseExpiryDate"] = ExpiryDate
            };

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

    private async Task ClosePageAsync()
    {
        TryDeleteCachedFile(_localFilePath);

        if (Application.Current?.MainPage?.Navigation is { } navigation)
        {
            await navigation.PopModalAsync();
        }
    }

    private static void TryDeleteCachedFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to delete cached file '{filePath}': {ex.Message}");
        }
    }
}
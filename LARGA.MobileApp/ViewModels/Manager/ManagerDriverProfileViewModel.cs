using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.MobileApp.Services;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Media;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.ViewModels.Manager;

[QueryProperty(nameof(DriverId), "id")]
public class ManagerDriverProfileViewModel : BindableObject
{
    private readonly IOcrService _ocrService;

    private string _driverId = string.Empty;
    public string DriverId
    {
        get => _driverId;
        set
        {
            _driverId = value;
            OnPropertyChanged();
            _ = LoadDriverAsync();
        }
    }

    private string _fullName = string.Empty;
    public string FullName
    {
        get => _fullName;
        set { _fullName = value; OnPropertyChanged(); }
    }

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private Color _statusColor = Colors.Gray;
    public Color StatusColor
    {
        get => _statusColor;
        set { _statusColor = value; OnPropertyChanged(); }
    }

    private bool _hasCredentials;
    public bool HasCredentials
    {
        get => _hasCredentials;
        set { _hasCredentials = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasNoCredentials)); }
    }

    public bool HasNoCredentials => !HasCredentials;

    private string _licenseNumberDisplay = string.Empty;
    public string LicenseNumberDisplay
    {
        get => _licenseNumberDisplay;
        set { _licenseNumberDisplay = value; OnPropertyChanged(); }
    }

    private string _dlCodesDisplay = string.Empty;
    public string DlCodesDisplay
    {
        get => _dlCodesDisplay;
        set { _dlCodesDisplay = value; OnPropertyChanged(); }
    }

    private string _expiryDateDisplay = string.Empty;
    public string ExpiryDateDisplay
    {
        get => _expiryDateDisplay;
        set { _expiryDateDisplay = value; OnPropertyChanged(); }
    }

    public ICommand GoBackCommand { get; }
    public ICommand UploadLicensePhotoCommand { get; }
    public ICommand ReloadCommand { get; }

    public ManagerDriverProfileViewModel(IOcrService ocrService)
    {
        _ocrService = ocrService;

        GoBackCommand = new Command(async () => await Shell.Current.GoToAsync(".."));
        UploadLicensePhotoCommand = new Command(async () => await UploadLicensePhotoAsync());
        ReloadCommand = new Command(async () => await LoadDriverAsync());
    }

    private async Task UploadLicensePhotoAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(DriverId)) return;
            if (!MediaPicker.Default.IsCaptureSupported) return;

            var photo = await MediaPicker.Default.CapturePhotoAsync();
            if (photo == null) return;

            using var stream = await photo.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var imageBytes = buffer.ToArray();

            if (Application.Current?.MainPage?.Navigation is { } navigation)
            {
                await navigation.PushModalAsync(
                    new Views.Driver.ScanDriverLicensePage(_ocrService, imageBytes, DriverId));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Manager Upload License Photo Error: {ex.Message}");
        }
    }

    private async Task LoadDriverAsync()
    {
        if (string.IsNullOrWhiteSpace(DriverId)) return;

        try
        {
            var doc = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(DriverId)
                .GetDocumentSnapshotAsync<DriverDetailProxy>();

            if (doc?.Data == null) return;

            FullName = string.IsNullOrWhiteSpace(doc.Data.FullName) ? "(Unnamed driver)" : doc.Data.FullName;
            (StatusText, StatusColor) = LicenseStatusHelper.Describe(doc.Data.LicenseExpiryDate);

            LicenseNumberDisplay = doc.Data.LicenseNumber ?? string.Empty;
            DlCodesDisplay = doc.Data.LicenseClassification ?? string.Empty;
            ExpiryDateDisplay = doc.Data.LicenseExpiryDate != null
                ? FirestoreDateTimeFix.Apply(doc.Data.LicenseExpiryDate.Value.UtcDateTime).ToLocalTime().ToString("MMM d, yyyy").ToUpperInvariant()
                : string.Empty;

            HasCredentials = !string.IsNullOrWhiteSpace(LicenseNumberDisplay)
                || !string.IsNullOrWhiteSpace(DlCodesDisplay)
                || !string.IsNullOrWhiteSpace(ExpiryDateDisplay);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Manager Driver Profile Load Error: {ex.Message}");
        }
    }

    private class DriverDetailProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("fullName")]
        public string FullName { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("licenseNumber")]
        public string LicenseNumber { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("licenseClassification")]
        public string LicenseClassification { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("licenseRestrictionCode")]
        public string LicenseRestrictionCode { get; set; } = string.Empty;

        // DateTimeOffset?, not DateTime? - see LicenseStatusHelper.Describe for why.
        [Plugin.Firebase.Firestore.FirestoreProperty("licenseExpiryDate")]
        public DateTimeOffset? LicenseExpiryDate { get; set; }
    }
}

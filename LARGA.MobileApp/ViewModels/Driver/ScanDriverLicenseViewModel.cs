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
using LARGA.SharedCore;
using LARGA.SharedCore.Services;
using Plugin.Firebase.Firestore;
using Plugin.Firebase.Storage;

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
    private readonly IFaceCropService _faceCropService;
    private readonly string _targetUserId;
    private string _localFilePath; // THE FIX: Store path instead of byte[]
    private LtoLicenseParser.Result? _parsed;

    // OCR text from an earlier photo of the same card, kept when glare hid the license number
    // and the user retook the photo at another angle: both photos are voted on together.
    private IReadOnlyList<string> _earlierTexts = Array.Empty<string>();

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

    private bool _licenseNumberNeedsCheck;
    /// <summary>The number may be misread (glare over it, passes disagreed) - shown with a warning.</summary>
    public bool LicenseNumberNeedsCheck { get => _licenseNumberNeedsCheck; set { _licenseNumberNeedsCheck = value; OnPropertyChanged(); } }

    private bool _expiryNeedsCheck;
    public bool ExpiryNeedsCheck { get => _expiryNeedsCheck; set { _expiryNeedsCheck = value; OnPropertyChanged(); } }

    private DateTime _expiryDate = DateTime.Today;
    public DateTime ExpiryDate { get => _expiryDate; set { _expiryDate = value; OnPropertyChanged(); } }

    public ICommand RetakeCommand { get; }
    public ICommand ConfirmScanCommand { get; }
    // One save at a time - a second tap while the first is still validating or saving is ignored.
    private readonly LARGA.MobileApp.Services.BusyGate _saveGate = new();

    public ICommand ConfirmAndSaveCommand { get; }
    public ICommand CancelCommand { get; }

    // THE FIX: Constructor now accepts string localFilePath
    public ScanDriverLicenseViewModel(IOcrService ocrService, IFaceCropService faceCropService, string localFilePath, string targetUserId)
    {
        _ocrService = ocrService;
        _faceCropService = faceCropService;
        _localFilePath = localFilePath;
        _targetUserId = targetUserId;

        // Bind natively from file
        CapturedImage = ImageSource.FromFile(_localFilePath);

        RetakeCommand = new Command(async () => await LARGA.MobileApp.Services.NavigationGate.RunAsync(async () =>
        {
            // A fresh photo from the review step starts over; only the glare retake keeps the old reads.
            _earlierTexts = Array.Empty<string>();
            await RetakeAsync();
        }));
        ConfirmScanCommand = new Command(async () => await RunOcrAsync());
        ConfirmAndSaveCommand = new Command(async () => await _saveGate.RunAsync(SaveAsync, showLoading: false));
        CancelCommand = new Command(async () => await LARGA.MobileApp.Services.NavigationGate.RunAsync(ClosePageAsync));
    }

    /// <summary>True when a new photo replaced the current one.</summary>
    private async Task<bool> RetakeAsync()
    {
        try
        {
            // DEBUG: a photo queued for the emulator stands in for the camera.
            if (DebugCameraSource.TakeQueued() is string queued)
            {
                TryDeleteCachedFile(_localFilePath);
                _localFilePath = queued;
                CapturedImage = ImageSource.FromFile(_localFilePath);
                return true;
            }

            if (!MediaPicker.Default.IsCaptureSupported) return false;
            var photo = await MediaPicker.Default.CapturePhotoAsync();
            if (photo == null) return false;

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
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Retake License Photo Error: {ex.Message}");
            return false;
        }
    }

    private async Task RunOcrAsync()
    {
        IsBusy = true;
        try
        {
            // Several ML Kit passes (glare-filtered, flattened, the License No. row enlarged)
            // until the fields agree - LicenseScanPipeline.
            var outcome = await LicenseScanPipeline.RunAsync(_ocrService, _localFilePath, _earlierTexts);
            _parsed = outcome.Result;

            if (_parsed.LicenseNumber is null && _parsed.ExpiryDate is null)
            {
                _earlierTexts = Array.Empty<string>();
                await Shell.Current.DisplayAlert(
                    "Couldn't read that clearly",
                    "We couldn't make out the license number or expiry date. Retake the photo with the card filling the frame, in even light, and hold the phone still.",
                    "OK");
                return;
            }

            // Same rule as ManagerWeb's license scan: the driver's first and last name must be
            // on the card (LtoLicenseParser.NameMatches).
            var registeredName = await GetRegisteredNameAsync();
            if (!string.IsNullOrWhiteSpace(registeredName))
            {
                if (string.IsNullOrWhiteSpace(_parsed.FullName))
                {
                    await Shell.Current.DisplayAlert(
                        "Couldn't read the name",
                        "We couldn't read the name on this license. Retake the photo so the name is clearly visible.",
                        "OK");
                    return;
                }

                if (!LtoLicenseParser.NameMatches(registeredName, _parsed.FullName))
                {
                    _earlierTexts = Array.Empty<string>();
                    await Shell.Current.DisplayAlert(
                        "Name doesn't match",
                        $"This license appears to belong to \"{DisplayName(_parsed.FullName)}\", but this account is registered as \"{registeredName}\". The driver's first and last name must be on the license.",
                        "OK");
                    return;
                }
            }

            // The shiny seal sits over the License No. row - glare there hides or garbles digits.
            // Glare moves when the card is tilted, so a second photo usually reads it; the two
            // photos' reads are combined.
            if (_parsed.LicenseNumber is null || _parsed.LicenseNumberUncertain)
            {
                bool retake = await Shell.Current.DisplayAlert(
                    _parsed.LicenseNumber is null ? "License number hidden by glare" : "Check the license number",
                    (_parsed.LicenseNumber is null
                        ? "The shiny seal or a reflection is covering the License No."
                        : $"Glare over the seal made the License No. hard to read - we got {_parsed.LicenseNumber}.")
                    + " Tilt the card slightly so the shine moves off the number and retake, or continue and check it against the card yourself.",
                    "Retake", "Continue");
                if (retake)
                {
                    var keep = outcome.Texts.Concat(_earlierTexts).ToList();
                    if (await RetakeAsync())
                    {
                        _earlierTexts = keep;
                        await RunOcrAsync();
                        return;
                    }
                }
            }

            _earlierTexts = Array.Empty<string>();
            NameDisplay = DisplayName(_parsed.FullName);
            SexDisplay = _parsed.Sex ?? string.Empty;
            DlCodesDisplay = _parsed.DlCodes ?? string.Empty;
            LicenseNumberDisplay = _parsed.LicenseNumber ?? string.Empty;
            LicenseNumberNeedsCheck = _parsed.LicenseNumber is null || _parsed.LicenseNumberUncertain;
            ExpiryDate = _parsed.ExpiryDate ?? DateTime.Today;
            ExpiryNeedsCheck = _parsed.ExpiryDate is null || _parsed.ExpiryUncertain;

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

    /// <summary>The card prints "LAST, FIRST MIDDLE"; the screen shows "First Middle Last".</summary>
    private static string DisplayName(string? cardName)
    {
        if (string.IsNullOrWhiteSpace(cardName)) return string.Empty;

        string[] parts = cardName.Split(',', 2);
        if (parts.Length < 2) return ToTitleCase(cardName.Trim());

        return $"{ToTitleCase(parts[1].Trim())} {ToTitleCase(parts[0].Trim())}".Trim();
    }

    private static string ToTitleCase(string value) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());

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

    /// <summary>The same checks ManagerWeb's license scan applies before it saves (InputValidator /
    /// LtoLicenseParser): a real LTO license number and DL codes, and a license that isn't
    /// expired or expiring within 3 months. Null when everything is fine.</summary>
    private string? ValidateBeforeSave()
    {
        DateTime today = PhilippineTime.Now.Date;
        string? error = InputValidator.ValidateLicenseNumber(LicenseNumberDisplay, required: true)
            ?? InputValidator.ValidateLicenseClassification(DlCodesDisplay);
        if (error is not null) return error;

        if (ExpiryDate.Date < today) return $"This license expired on {ExpiryDate:MMM d, yyyy} - it can't be saved.";
        if (ExpiryDate.Date <= today.AddMonths(3)) return $"This license expires on {ExpiryDate:MMM d, yyyy} - within 3 months. The driver needs to renew it first.";
        return InputValidator.ValidateLicenseExpiry(ExpiryDate, today);
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_targetUserId)) return;

        if (ValidateBeforeSave() is string error)
        {
            await Shell.Current.DisplayAlert("Check the license details", error, "OK");
            return;
        }

        IsBusy = true;
        try
        {
            // The photo goes with the details - ManagerWeb's driver profile shows it under
            // License Credentials (users/{id}.ltoIdPhotoUrl, same lto_ids/{driverId}/ folder the
            // web's own license scan uploads to). Without it the web showed a VALID license
            // with no photo on file.
            string photoUrl = await UploadLicensePhotoAsync();

            // The face crop becomes the profile avatar. Optional: if no face is found the save
            // still goes through, and the profile keeps whatever avatar it already had.
            string? profileImageUrl = await UploadProfileFaceAsync();

            var updates = new Dictionary<object, object>
            {
                ["licenseNumber"] = InputValidator.NormalizeLicenseNumber(LicenseNumberDisplay)!,
                ["licenseClassification"] = InputValidator.NormalizeLicenseClassification(DlCodesDisplay),
                // Calendar date at UTC midnight - how ManagerWeb stores it, so both read the same day.
                ["licenseExpiryDate"] = DateTime.SpecifyKind(ExpiryDate.Date, DateTimeKind.Utc),
                ["ltoIdPhotoUrl"] = photoUrl,
            };

            if (profileImageUrl is not null)
            {
                updates["profileImageUrl"] = profileImageUrl;
            }

            var userDoc = CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(_targetUserId);

            // Note the photos this scan replaces, so they can be deleted after the save instead of
            // piling up in Storage (same cleanup as ManagerWeb's UpdateDriverProfileAsync).
            StoredPhotosProxy? previous = null;
            try
            {
                previous = (await userDoc.GetDocumentSnapshotAsync<StoredPhotosProxy>())?.Data;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Previous Photos Read Error: {ex.Message}");
            }

            await userDoc.UpdateDataAsync(updates);

            await DeleteReplacedPhotoAsync(previous?.LtoIdPhotoUrl, photoUrl);
            await DeleteReplacedPhotoAsync(previous?.ProfileImageUrl, profileImageUrl);

            await ClosePageAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Save License Error: {ex.Message}");
            await Shell.Current.DisplayAlert("Error", "Failed to save the license details and photo. Check the connection and try again.", "OK");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<string> UploadLicensePhotoAsync()
    {
        string extension = Path.GetExtension(_localFilePath);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".jpg";

        var storageRef = CrossFirebaseStorage.Current.GetRootReference()
            .GetChild($"lto_ids/{_targetUserId}/{DateTime.UtcNow:yyyyMMddHHmmss}{extension.ToLowerInvariant()}");
        // The content type is explicit: storage.rules accept images only.
        string contentType = extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };
        await storageRef.PutFile(_localFilePath, new StorageMetadata(contentType: contentType)).AwaitAsync();
        return await storageRef.GetDownloadUrlAsync();
    }

    /// <summary>Crops the face from the license photo and uploads it next to the license photo
    /// (same lto_ids/{driverId}/ folder). Returns null, rather than throwing, when there's no
    /// face or the upload fails, so a missing avatar never blocks saving the license.</summary>
    private async Task<string?> UploadProfileFaceAsync()
    {
        string? facePath = null;
        try
        {
            facePath = await _faceCropService.CropDriverFaceAsync(_localFilePath);
            if (facePath is null) return null;

            var storageRef = CrossFirebaseStorage.Current.GetRootReference()
                .GetChild($"lto_ids/{_targetUserId}/face_{DateTime.UtcNow:yyyyMMddHHmmss}.jpg");
            await storageRef.PutFile(facePath, new StorageMetadata(contentType: "image/jpeg")).AwaitAsync();
            return await storageRef.GetDownloadUrlAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Profile Face Upload Error: {ex.Message}");
            return null;
        }
        finally
        {
            TryDeleteCachedFile(facePath);
        }
    }

    /// <summary>Deletes a photo this scan replaced. Only files in this driver's own
    /// lto_ids/{driverId}/ folder are touched, and a failure is ignored: the license is already
    /// saved, so a failed delete only leaves an unused file behind.</summary>
    private async Task DeleteReplacedPhotoAsync(string? oldUrl, string? newUrl)
    {
        if (string.IsNullOrWhiteSpace(oldUrl) || string.IsNullOrWhiteSpace(newUrl) || oldUrl == newUrl) return;
        if (!oldUrl.Contains($"/o/{Uri.EscapeDataString($"lto_ids/{_targetUserId}/")}", StringComparison.Ordinal)) return;

        try
        {
            await CrossFirebaseStorage.Current.GetReferenceFromUrl(oldUrl).DeleteAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Replaced Photo Delete Error: {ex.Message}");
        }
    }

    private class StoredPhotosProxy
    {
        [FirestoreProperty("ltoIdPhotoUrl")]
        public string? LtoIdPhotoUrl { get; set; }

        [FirestoreProperty("profileImageUrl")]
        public string? ProfileImageUrl { get; set; }
    }

    private async Task ClosePageAsync()
    {
        TryDeleteCachedFile(_localFilePath);

        if (Shell.Current?.Navigation is { } navigation)
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
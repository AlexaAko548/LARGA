using LARGA.MobileApp.Services;
using LARGA.SharedCore.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace LARGA.MobileApp.ViewModels.Driver;

public class PreShiftStep2ViewModel : BindableObject
{
    private bool _areStep1InspectionsComplete = true;

    private readonly IShiftManagementService _shiftService;
    private readonly IOcrService _ocrService;
    private string _assignedTaxiId = string.Empty;
    private string? _odometerPhotoLocalPath;
    private string? _fuelPhotoLocalPath;

    private string _assignedUnitPlate = "Loading...";
    public string AssignedUnitPlate
    {
        get => _assignedUnitPlate;
        private set { _assignedUnitPlate = value; OnPropertyChanged(); }
    }

    public bool AreStep1InspectionsComplete
    {
        get => _areStep1InspectionsComplete;
        set { _areStep1InspectionsComplete = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanStartShift)); }
    }

    private string _startingOdometer = string.Empty;
    public string StartingOdometer
    {
        get => _startingOdometer;
        set
        {
            _startingOdometer = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStartShift));
            OnPropertyChanged(nameof(IsOdometerScanned));
            OnPropertyChanged(nameof(OdometerButtonText));
        }
    }

    public bool IsOdometerScanned => !string.IsNullOrWhiteSpace(StartingOdometer);
    public string OdometerButtonText => IsOdometerScanned ? $"📷 {StartingOdometer} km" : "📷 Scan odometer dashboard";

    private ImageSource? _fuelPhoto;
    public ImageSource? FuelPhoto
    {
        get => _fuelPhoto;
        set
        {
            _fuelPhoto = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPhoto));
            OnPropertyChanged(nameof(CanStartShift));
        }
    }

    public bool HasPhoto => FuelPhoto != null;

    private bool _isHalfTankSelected;
    public bool IsHalfTankSelected
    {
        get => _isHalfTankSelected;
        set
        {
            _isHalfTankSelected = value;
            if (value) IsBelowHalfTankSelected = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStartShift));
        }
    }

    private bool _isBelowHalfTankSelected;
    public bool IsBelowHalfTankSelected
    {
        get => _isBelowHalfTankSelected;
        set
        {
            if (_isBelowHalfTankSelected == value) return;
            _isBelowHalfTankSelected = value;
            if (value) IsHalfTankSelected = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PenaltyNoteVisible));
            OnPropertyChanged(nameof(CanStartShift));
        }
    }

    public bool PenaltyNoteVisible => IsBelowHalfTankSelected;

    public bool CanStartShift =>
        !string.IsNullOrWhiteSpace(StartingOdometer) &&
        HasPhoto &&
        (IsHalfTankSelected || IsBelowHalfTankSelected);

    public ICommand ScanOdometerCommand { get; }
    public ICommand AttachPhotoCommand { get; }
    public ICommand ConfirmStartShiftCommand { get; }

    public PreShiftStep2ViewModel(IShiftManagementService shiftService, IOcrService ocrService)
    {
        _shiftService = shiftService;
        _ocrService = ocrService;

        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Register<PreShiftStep2ViewModel, OdometerScannedData, string>(this, "PreShiftOdometerScanned", (r, data) =>
        {
            r.StartingOdometer = data.OdometerText;
        });

        _ = LoadAssignedUnitAsync();

        ScanOdometerCommand = new Command(async () => await ScanOdometerAsync());
        AttachPhotoCommand = new Command(async () => await AttachPhotoAsync());
        ConfirmStartShiftCommand = new Command(async () => await ConfirmStartShiftAsync());
    }

    private async Task LoadAssignedUnitAsync()
    {
        try
        {
            var taxi = await _shiftService.GetCurrentUserAssignedTaxiAsync();
            if (taxi != null)
            {
                _assignedTaxiId = taxi.TaxiId;
                AssignedUnitPlate = string.IsNullOrWhiteSpace(taxi.PlateNumber)
                    ? taxi.Model
                    : taxi.PlateNumber.Replace("-", " · ");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Assigned Unit Error: {ex.Message}");
        }
    }

    private async Task AttachPhotoAsync()
    {
        try
        {
            if (MediaPicker.Default.IsCaptureSupported)
            {
                var photo = await MediaPicker.Default.CapturePhotoAsync();
                if (photo != null)
                {
                    string localFilePath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid():N}_{photo.FileName}");

                    await ProcessAndOrientPhotoAsync(photo.FullPath, localFilePath, maxDimension: 1024, quality: 75);

                    string? oldFilePath = _fuelPhotoLocalPath;
                    _fuelPhotoLocalPath = localFilePath;

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        FuelPhoto = null;
                        FuelPhoto = ImageSource.FromFile(localFilePath);
                    });

                    TryDeleteCachedFile(oldFilePath);
                }
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Camera failed: {ex.Message}", "OK");
        }
    }

    private async Task ScanOdometerAsync()
    {
        try
        {
            if (MediaPicker.Default.IsCaptureSupported)
            {
                var photo = await MediaPicker.Default.CapturePhotoAsync();
                if (photo != null)
                {
                    string localFilePath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid():N}_{photo.FileName}");

                    await ProcessAndOrientPhotoAsync(photo.FullPath, localFilePath, maxDimension: 1280, quality: 85);

                    string? oldFilePath = _odometerPhotoLocalPath;
                    _odometerPhotoLocalPath = localFilePath;

                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        var navigation = Shell.Current?.Navigation ?? Application.Current?.MainPage?.Navigation;
                        if (navigation != null)
                        {
                            await navigation.PushModalAsync(
                                new LARGA.MobileApp.Views.Driver.OdometerScanPage(_ocrService, localFilePath, "PreShiftOdometerScanned"));
                        }
                    });

                    TryDeleteCachedFile(oldFilePath);
                }
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Camera failed: {ex.Message}", "OK");
        }
    }

    private async Task ConfirmStartShiftAsync()
    {
        if (!CanStartShift)
        {
            await Shell.Current.DisplayAlert("Required", "Please complete all fields (Odometer, Fuel Level, Fuel Photo).", "OK");
            return;
        }

        if (string.IsNullOrWhiteSpace(_assignedTaxiId))
        {
            await Shell.Current.DisplayAlert("Missing assignment", "No assigned taxi was found for your account. Please contact the manager.", "OK");
            return;
        }

        try
        {
            string digitsOnly = new string(StartingOdometer.Where(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(digitsOnly) || !int.TryParse(digitsOnly, out int startMileage) || startMileage <= 0)
            {
                await Shell.Current.DisplayAlert("Invalid odometer reading", "Please rescan or enter a valid starting odometer reading before starting the shift.", "OK");
                return;
            }

            string newDocumentId = await _shiftService.ClockInAsync(_assignedTaxiId, startMileage);
            if (string.IsNullOrWhiteSpace(newDocumentId))
            {
                await Shell.Current.DisplayAlert("Error", "Failed to start shift. Please try again.", "OK");
                return;
            }

            await SecureStorage.SetAsync("ActiveShiftDocumentId", newDocumentId);
            Preferences.Set("IsShiftActive", true);
            Preferences.Set("ShiftStartTime", DateTime.Now.ToString("o"));

            StartingOdometer = string.Empty;
            FuelPhoto = null;
            IsHalfTankSelected = false;
            IsBelowHalfTankSelected = false;

            TryDeleteCachedFile(_odometerPhotoLocalPath);
            TryDeleteCachedFile(_fuelPhotoLocalPath);
            _odometerPhotoLocalPath = null;
            _fuelPhotoLocalPath = null;

            await Shell.Current.GoToAsync("active-shift");
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Failed to start shift: {ex.Message}", "OK");
        }
    }

    private static Task<string> ProcessAndOrientPhotoAsync(string sourcePath, string targetPath, int maxDimension = 1280, int quality = 80)
    {
        return Task.Run(() =>
        {
#if ANDROID
            Android.Graphics.Bitmap? sampledBitmap = null;
            Android.Graphics.Bitmap? finalBitmap = null;
            Android.Graphics.Matrix? matrix = null;

            try
            {
                int rotationDegrees = 0;
                try
                {
                    var exif = new Android.Media.ExifInterface(sourcePath);
                    int orientation = exif.GetAttributeInt(Android.Media.ExifInterface.TagOrientation, 1);
                    rotationDegrees = orientation switch
                    {
                        6 => 90,
                        3 => 180,
                        8 => 270,
                        _ => 0
                    };
                }
                catch { }

                var options = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
                using (var boundsStream = File.OpenRead(sourcePath))
                {
                    Android.Graphics.BitmapFactory.DecodeStream(boundsStream, null, options);
                }

                int sampleSize = 1;
                int maxSourceDim = Math.Max(options.OutWidth, options.OutHeight);
                while (maxSourceDim / sampleSize > maxDimension)
                {
                    sampleSize *= 2;
                }

                options.InJustDecodeBounds = false;
                options.InSampleSize = sampleSize;

                using (var decodeStream = File.OpenRead(sourcePath))
                {
                    sampledBitmap = Android.Graphics.BitmapFactory.DecodeStream(decodeStream, null, options);
                }

                if (sampledBitmap == null) throw new Exception("Native stream decode failed.");

                finalBitmap = sampledBitmap;
                if (rotationDegrees != 0)
                {
                    matrix = new Android.Graphics.Matrix();
                    matrix.PostRotate(rotationDegrees);
                    finalBitmap = Android.Graphics.Bitmap.CreateBitmap(
                        sampledBitmap, 0, 0, sampledBitmap.Width, sampledBitmap.Height, matrix, true);
                }

                using (var fileStream = File.Create(targetPath))
                {
                    finalBitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, quality, fileStream);
                }

                return targetPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Native image processing error: {ex.Message}");
                using var stream = File.OpenRead(sourcePath);
                using var image = Microsoft.Maui.Graphics.Platform.PlatformImage.FromStream(stream);
                using var downsized = image.Downsize(maxDimension, true);
                using var outStream = File.Create(targetPath);
                downsized.Save(outStream, Microsoft.Maui.Graphics.ImageFormat.Jpeg, quality / 100f);

                return targetPath;
            }
            finally
            {
                if (finalBitmap != null && finalBitmap != sampledBitmap)
                {
                    finalBitmap.Recycle();
                    finalBitmap.Dispose();
                }
                if (sampledBitmap != null)
                {
                    sampledBitmap.Recycle();
                    sampledBitmap.Dispose();
                }
                matrix?.Dispose();
            }
#else
            using var stream = File.OpenRead(sourcePath);
            using var image = Microsoft.Maui.Graphics.Platform.PlatformImage.FromStream(stream);
            using var downsized = image.Downsize(maxDimension, true);
            using var outStream = File.Create(targetPath);
            downsized.Save(outStream, Microsoft.Maui.Graphics.ImageFormat.Jpeg, quality / 100f);
            return targetPath;
#endif
        });
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
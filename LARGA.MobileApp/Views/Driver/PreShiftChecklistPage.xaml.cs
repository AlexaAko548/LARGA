using LARGA.Shared.Models;
using LARGA.MobileApp.Services;

namespace LARGA.MobileApp.Views.Driver;

public partial class PreShiftChecklistPage : ContentPage
{
    private readonly CameraPermissionService _cameraPermissionService = new();
    private int _currentStep = 1;
    private string? _odometerPhotoPath;
    private string? _fuelPhotoPath;
    private string? _scratchesPhotoPath;

    public PreShiftChecklistPage()
    {
        InitializeComponent();
        UpdateProgress();
    }

    private void OnChecklistItemChanged(object sender, CheckedChangedEventArgs e)
    {
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        int completed = 0;
        if (TireConditionCheck.IsChecked) completed++;
        if (UnderHoodCheck.IsChecked) completed++;
        if (LightsElectronicsCheck.IsChecked) completed++;
        if (InteriorComfortCheck.IsChecked) completed++;
        if (ExteriorScratchesCheck.IsChecked) completed++;

        CompletionCountLabel.Text = $"{completed} / 5 completed";
        StepProgressBar.Progress = completed / 5.0;
    }

    private void OnReportDamageClicked(object sender, EventArgs e)
    {
        // TODO: open a form/dialog to log damage description + photo,
        // which will create a MaintenanceRecord if the driver flags an issue
    }

    private async void OnNextClicked(object sender, EventArgs e)
    {
        _currentStep = 2;
        Step1Panel.IsVisible = false;
        Step2Panel.IsVisible = true;
        StepProgressBar.Progress = 0.5;
        CompletionCountLabel.Text = "1 / 2 completed";
    }

    private async Task<FileResult?> CapturePhotoWithPermissionAsync()
    {
        bool granted = await _cameraPermissionService.CheckAndRequestCameraPermissionAsync();
        if (!granted)
        {
            await DisplayAlert("Camera Permission Needed",
                "LARGA needs camera access to scan and verify vehicle condition. Please enable it in your device settings.",
                "OK");
            return null;
        }

        if (!MediaPicker.Default.IsCaptureSupported)
        {
            await DisplayAlert("Camera Unavailable", "This device does not support camera capture.", "OK");
            return null;
        }

        return await MediaPicker.Default.CapturePhotoAsync();
    }

    private async void OnScanOdometerClicked(object sender, EventArgs e)
    {
        var photo = await CapturePhotoWithPermissionAsync();
        if (photo != null)
        {
            _odometerPhotoPath = photo.FullPath;
            // TODO: run OCR on this photo to extract odometer reading (LAR-12 integration point)
        }
    }

    private void OnBelowHalfTankChecked(object sender, CheckedChangedEventArgs e)
    {
        PenaltyNoteLabel.IsVisible = e.Value;
    }

    private async void OnAttachFuelPhotoClicked(object sender, EventArgs e)
    {
        var photo = await CapturePhotoWithPermissionAsync();
        if (photo != null)
        {
            _fuelPhotoPath = photo.FullPath;
        }
    }

    private async void OnConfirmStartShiftClicked(object sender, EventArgs e)
    {
        var checklist = new HandoverChecklist
        {
            ChecklistType = ChecklistType.PreShift,
            TireCondition = TireConditionCheck.IsChecked,
            OilLevel = UnderHoodCheck.IsChecked,
            CoolantLevel = UnderHoodCheck.IsChecked,
            InteriorCleanliness = InteriorComfortCheck.IsChecked,
            ExteriorScratches = ExteriorScratchesCheck.IsChecked,
            FuelVerification = BelowHalfTankRadio.IsChecked
                ? FuelVerificationLevel.BelowHalfTank
                : FuelVerificationLevel.HalfTank,
            FuelDashboardUrl = _fuelPhotoPath,
            Timestamp = DateTime.UtcNow
        };

        // TODO: save checklist to Firestore, update ShiftLog with StartMileage from OCR,
        // then navigate to the active shift/home screen
    }
}
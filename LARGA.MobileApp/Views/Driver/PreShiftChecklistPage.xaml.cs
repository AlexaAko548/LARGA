using LARGA.Shared.Models;

namespace LARGA.MobileApp.Views.Driver;

public partial class PreShiftChecklistPage : ContentPage
{
    public PreShiftChecklistPage()
    {
        InitializeComponent();
    }

    private async void OnUploadScratchesPhotoClicked(object sender, EventArgs e)
    {
        var photo = await MediaPicker.Default.CapturePhotoAsync();
        if (photo != null)
        {
            // TODO: upload to Firebase Storage, store URL
        }
    }

    private async void OnUploadFuelDashboardPhotoClicked(object sender, EventArgs e)
    {
        var photo = await MediaPicker.Default.CapturePhotoAsync();
        if (photo != null)
        {
            // TODO: upload to Firebase Storage, store URL
        }
    }

    private void OnSubmitClicked(object sender, EventArgs e)
    {
        var checklist = new HandoverChecklist
        {
            ChecklistType = ChecklistType.PreShift,
            TireCondition = TireConditionCheckBox.IsChecked,
            OilLevel = OilLevelCheckBox.IsChecked,
            CoolantLevel = CoolantLevelCheckBox.IsChecked,
            InteriorCleanliness = InteriorCleanlinessCheckBox.IsChecked,
            ExteriorScratches = ExteriorScratchesCheckBox.IsChecked,
            FuelVerification = FuelVerificationPicker.SelectedItem?.ToString() == "Half-tank"
                ? FuelVerificationLevel.HalfTank
                : FuelVerificationLevel.BelowHalfTank,
            Timestamp = DateTime.UtcNow
        };

    }
}
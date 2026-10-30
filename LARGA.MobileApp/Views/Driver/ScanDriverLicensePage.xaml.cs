using LARGA.MobileApp.Services;
using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Views.Driver;

public partial class ScanDriverLicensePage : ContentPage
{
    // THE FIX: Accept string localFilePath instead of byte[] imageBytes
    public ScanDriverLicensePage(IOcrService ocrService, IFaceCropService faceCropService, string localFilePath, string targetUserId)
    {
        InitializeComponent();

        // THE FIX: Pass the string directly to the updated ViewModel
        BindingContext = new ScanDriverLicenseViewModel(ocrService, faceCropService, localFilePath, targetUserId);
    }

    // The phone's back button closes it the same way as the back arrow (which also deletes the cached photo).
    protected override bool OnBackButtonPressed()
    {
        if (BindingContext is ScanDriverLicenseViewModel vm)
        {
            vm.CancelCommand.Execute(null);
            return true;
        }

        return base.OnBackButtonPressed();
    }
}
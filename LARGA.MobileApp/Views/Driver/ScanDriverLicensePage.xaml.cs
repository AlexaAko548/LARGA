using LARGA.MobileApp.Services;
using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Views.Driver;

public partial class ScanDriverLicensePage : ContentPage
{
    // THE FIX: Accept string localFilePath instead of byte[] imageBytes
    public ScanDriverLicensePage(IOcrService ocrService, string localFilePath, string targetUserId)
    {
        InitializeComponent();

        // THE FIX: Pass the string directly to the updated ViewModel
        BindingContext = new ScanDriverLicenseViewModel(ocrService, localFilePath, targetUserId);
    }
}
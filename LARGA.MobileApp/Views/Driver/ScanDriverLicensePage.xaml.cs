using LARGA.MobileApp.Services;
using LARGA.MobileApp.ViewModels.Driver;

namespace LARGA.MobileApp.Views.Driver;

public partial class ScanDriverLicensePage : ContentPage
{
    public ScanDriverLicensePage(IOcrService ocrService, byte[] imageBytes, string targetUserId)
    {
        InitializeComponent();
        BindingContext = new ScanDriverLicenseViewModel(ocrService, imageBytes, targetUserId);
    }
}

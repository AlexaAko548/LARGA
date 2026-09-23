using LARGA.MobileApp.ViewModels.Driver;

namespace LARGA.MobileApp.Views.Driver;

public partial class DriverChangePasswordPage : ContentPage
{
    public DriverChangePasswordPage()
    {
        InitializeComponent();
        BindingContext = new DriverChangePasswordViewModel();
    }
}

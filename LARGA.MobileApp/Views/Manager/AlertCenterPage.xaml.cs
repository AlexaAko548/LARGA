using LARGA.MobileApp.ViewModels.Manager;

namespace LARGA.MobileApp.Views.Manager;

public partial class AlertCenterPage : ContentPage
{
    public AlertCenterPage(AlertCenterViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
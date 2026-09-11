using LARGA.MobileApp.ViewModels.Driver;

namespace LARGA.MobileApp.Views.Driver;

public partial class VehicleDefectPage : ContentPage
{
    public VehicleDefectPage(VehicleDefectViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
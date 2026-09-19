using LARGA.MobileApp.ViewModels.Manager;

namespace LARGA.MobileApp.Views.Manager;

public partial class FleetRegistryPage : ContentPage
{
    private readonly FleetRegistryViewModel _viewModel;

    public FleetRegistryPage()
    {
        InitializeComponent();
        _viewModel = new FleetRegistryViewModel();
        BindingContext = _viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.LoadTaxisCommand.Execute(null);
    }
}

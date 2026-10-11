using LARGA.MobileApp.ViewModels.Driver;

namespace LARGA.MobileApp.Views.Driver;

public partial class DriverUpdateContactNumberPage : ContentPage
{
    private readonly DriverUpdateContactViewModel _viewModel;

    public DriverUpdateContactNumberPage()
    {
        InitializeComponent();
        _viewModel = new DriverUpdateContactViewModel();
        BindingContext = _viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.LoadCurrentNumberCommand.Execute(null);
    }
}

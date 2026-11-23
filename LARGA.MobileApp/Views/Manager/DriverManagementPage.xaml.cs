using LARGA.MobileApp.ViewModels.Manager;

namespace LARGA.MobileApp.Views.Manager;

public partial class DriverManagementPage : ContentPage
{
    private readonly DriverManagementViewModel _viewModel;

    public DriverManagementPage()
    {
        InitializeComponent();
        _viewModel = new DriverManagementViewModel();
        BindingContext = _viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.LoadDriversCommand.Execute(null);
    }
}

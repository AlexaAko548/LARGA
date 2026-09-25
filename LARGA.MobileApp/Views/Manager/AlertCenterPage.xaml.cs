using LARGA.MobileApp.ViewModels.Manager;

namespace LARGA.MobileApp.Views.Manager;

public partial class AlertCenterPage : ContentPage
{
    private readonly AlertCenterViewModel _viewModel;

    public AlertCenterPage(AlertCenterViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.LoadAlertsCommand.Execute(null);
    }
}

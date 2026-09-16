using LARGA.MobileApp.ViewModels.Manager;

namespace LARGA.MobileApp.Views.Manager;

public partial class ManagerLedgerPage : ContentPage
{
    private readonly ManagerLedgerViewModel _viewModel;

    public ManagerLedgerPage(ManagerLedgerViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadDailySettlementsAsync();
    }
}
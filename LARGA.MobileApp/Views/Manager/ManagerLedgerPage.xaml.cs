using LARGA.MobileApp.ViewModels.Manager;

namespace LARGA.MobileApp.Views.Manager;

public partial class ManagerLedgerPage : ContentPage
{
    private readonly ManagerLedgerViewModel _viewModel;

    public ManagerLedgerPage(ManagerLedgerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Reload every time the tab is shown so payments recorded elsewhere show up.
        _viewModel.LoadCommand.Execute(null);
    }
}

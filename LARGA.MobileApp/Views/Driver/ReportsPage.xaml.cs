using LARGA.MobileApp.ViewModels.Driver;
using LARGA.MobileApp.ViewModels.Driver;

namespace LARGA.MobileApp.Views.Driver;

public partial class ReportsPage : ContentPage
{
    private readonly ReportsViewModel _viewModel;

    public ReportsPage()
    {
        InitializeComponent();
        _viewModel = new ReportsViewModel();
        BindingContext = _viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.LoadReportsCommand.Execute(null);
    }
}
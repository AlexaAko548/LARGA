using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Views.Driver;

public partial class ClockInPendingPage : ContentPage
{
    private readonly ClockInPendingViewModel _viewModel;

    public ClockInPendingPage(ClockInPendingViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    // Only checks for the manager's decision while the screen is showing.
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.Start(Dispatcher);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.Stop();
    }
}

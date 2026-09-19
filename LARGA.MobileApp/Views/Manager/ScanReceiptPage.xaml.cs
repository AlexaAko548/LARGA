using LARGA.MobileApp.ViewModels.Manager;

namespace LARGA.MobileApp.Views.Manager;

public partial class ScanReceiptPage : ContentPage
{
    private readonly ScanReceiptViewModel _viewModel;

    public ScanReceiptPage(ScanReceiptViewModel viewModel)
    {
        InitializeComponent();
        Shell.SetNavBarIsVisible(this, false);
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_viewModel.CapturedImageSource == null)
        {
            await _viewModel.CaptureAndProcessReceiptAsync();
        }
    }
}
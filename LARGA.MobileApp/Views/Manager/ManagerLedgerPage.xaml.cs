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
        _viewModel.EnsurePaymentModalClosed();
        try
        {
            await _viewModel.LoadDailySettlementsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Ledger Crash Prevented] {ex}");
            await DisplayAlert("Error", "Unable to load ledger data.", "OK");
        }
    }
}
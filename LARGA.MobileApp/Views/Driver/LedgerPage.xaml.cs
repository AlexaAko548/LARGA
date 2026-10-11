using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Views.Driver;

public partial class LedgerPage : ContentPage
{
    public LedgerPage(LedgerViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (BindingContext is LedgerViewModel viewModel)
        {
            try
            {
                await viewModel.LoadDynamicLedgerDataAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ledger load failed: {ex.Message}");
            }
        }
    }
}
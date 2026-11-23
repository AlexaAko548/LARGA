using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using System;

namespace LARGA.MobileApp.Views.Driver;

public partial class PaymentHistoryPage : ContentPage
{
    private readonly PaymentHistoryViewModel _viewModel;

    public PaymentHistoryPage(PaymentHistoryViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await _viewModel.LoadDynamicPaymentDataAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Payment history load failed: {ex.Message}");
        }

        _viewModel.StartListening();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.StopListening();
    }

    private async void OnBackButtonClicked(object sender, EventArgs e)
    {
        try
        {
            await LARGA.MobileApp.Services.NavigationGate.GoBackAsync();
        }
        catch (Exception ex)
        {
            // Navigation can fail mid-transition; never let a back tap crash the app.
            System.Diagnostics.Debug.WriteLine($"Back navigation failed: {ex.Message}");
        }
    }
}
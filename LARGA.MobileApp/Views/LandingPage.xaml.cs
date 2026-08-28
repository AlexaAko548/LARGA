using LARGA.MobileApp.ViewModels;
using Microsoft.Maui.Controls;
using System;

namespace LARGA.MobileApp.Views;

public partial class LandingPage : ContentPage
{
    // Inject the LandingViewModel here so the page knows what 'viewModel' is
    public LandingPage(LandingViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnLoginClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//login");
    }
}
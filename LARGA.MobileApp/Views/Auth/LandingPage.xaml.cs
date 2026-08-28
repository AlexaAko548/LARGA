using LARGA.MobileApp.ViewModels.Auth;
using Microsoft.Maui.Controls;
using System;

namespace LARGA.MobileApp.Views.Auth;

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
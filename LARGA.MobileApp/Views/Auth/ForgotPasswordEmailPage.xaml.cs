using LARGA.MobileApp.ViewModels.Auth;
using Microsoft.Maui.Controls;
using System;

namespace LARGA.MobileApp.Views.Auth;

public partial class ForgotPasswordEmailPage : ContentPage
{
    public ForgotPasswordEmailPage(ForgotPasswordViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnBackButtonClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}
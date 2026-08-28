using LARGA.MobileApp.ViewModels;
using Microsoft.Maui.Controls;
using System;

namespace LARGA.MobileApp.Views;

public partial class ForgotPasswordVerifyPage : ContentPage
{
    public ForgotPasswordVerifyPage(ForgotPasswordViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnBackButtonClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}
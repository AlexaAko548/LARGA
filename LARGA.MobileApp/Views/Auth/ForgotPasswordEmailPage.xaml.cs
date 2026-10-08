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
        try
        {
            await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            // Navigation can fail mid-transition; never let a back tap crash the app.
            System.Diagnostics.Debug.WriteLine($"Back navigation failed: {ex.Message}");
        }
    }
}
using LARGA.MobileApp.ViewModels.Auth;
using Microsoft.Maui.Controls;
using System;

namespace LARGA.MobileApp.Views.Auth;

public partial class LoginPage : ContentPage
{
    public LoginPage(LoginViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private void OnTogglePasswordClicked(object sender, EventArgs e)
    {
        PasswordEntry.IsPassword = !PasswordEntry.IsPassword;
        PasswordToggle.Source = PasswordEntry.IsPassword ? "eye_icon.png" : "eye_off_icon.png";
        SemanticProperties.SetDescription(PasswordToggle, PasswordEntry.IsPassword ? "Show password" : "Hide password");
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
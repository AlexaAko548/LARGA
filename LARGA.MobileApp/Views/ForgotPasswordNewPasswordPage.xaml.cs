using LARGA.MobileApp.ViewModels;
using Microsoft.Maui.Controls;
using System;

namespace LARGA.MobileApp.Views;

public partial class ForgotPasswordNewPasswordPage : ContentPage
{
    public ForgotPasswordNewPasswordPage(ForgotPasswordViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnBackButtonClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }

    private void OnTogglePasswordClicked(object sender, EventArgs e)
    {
        NewPasswordEntry.IsPassword = !NewPasswordEntry.IsPassword;
    }

    private void OnToggleConfirmPasswordClicked(object sender, EventArgs e)
    {
        ConfirmPasswordEntry.IsPassword = !ConfirmPasswordEntry.IsPassword;
    }
}
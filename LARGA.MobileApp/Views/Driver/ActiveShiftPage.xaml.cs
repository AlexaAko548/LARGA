using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Controls;
using System;

namespace LARGA.MobileApp.Views.Driver;

public partial class ActiveShiftPage : ContentPage
{
    public ActiveShiftPage(ActiveShiftViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnBackButtonClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}
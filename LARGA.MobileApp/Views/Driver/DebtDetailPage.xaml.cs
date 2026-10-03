using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using System;

namespace LARGA.MobileApp.Views.Driver;

public partial class DebtDetailPage : ContentPage
{
	public DebtDetailPage(DebtDetailViewModel viewModel)
	{
		InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnBackButtonClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}
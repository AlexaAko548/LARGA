using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Controls;
using System;

namespace LARGA.MobileApp.Views.Driver;

public partial class DriverDashboardPage : ContentPage
{
    public DriverDashboardPage(ViewModels.Driver.DriverDashboardViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
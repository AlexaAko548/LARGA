using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Controls;
using System;

namespace LARGA.MobileApp.Views.Driver;

public partial class ProfilePage : ContentPage
{

    public ProfilePage(ViewModels.Driver.ProfileViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
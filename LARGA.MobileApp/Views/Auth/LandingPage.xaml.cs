using LARGA.MobileApp.ViewModels.Auth;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Views.Auth;

public partial class LandingPage : ContentPage
{
    public LandingPage(LandingViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
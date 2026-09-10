using Microsoft.Maui.Controls;
using LARGA.MobileApp.ViewModels.Driver;

namespace LARGA.MobileApp.Views.Driver;

public partial class ActiveShiftPage : ContentPage
{
    public ActiveShiftPage(ActiveShiftViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
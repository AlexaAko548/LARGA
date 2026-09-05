using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Views.Driver;

public partial class ShiftCompletedPage : ContentPage
{
    public ShiftCompletedPage(ShiftCompletedViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
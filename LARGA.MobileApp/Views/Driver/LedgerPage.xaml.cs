using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Views.Driver;

public partial class LedgerPage : ContentPage
{
    public LedgerPage(LedgerViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
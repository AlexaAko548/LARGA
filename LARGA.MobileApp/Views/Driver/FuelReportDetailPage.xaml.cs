using Microsoft.Maui.Controls;
using LARGA.MobileApp.ViewModels.Driver;

namespace LARGA.MobileApp.Views.Driver;

public partial class FuelReportDetailPage : ContentPage
{
    public FuelReportDetailPage(FuelReportDetailViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
using LARGA.MobileApp.ViewModels.Driver;
using LARGA.MobileApp.ViewModels.Driver;

namespace LARGA.MobileApp.Views.Driver;

public partial class FuelReportPage : ContentPage
{
	public FuelReportPage(FuelReportViewModel viewModel)
	{
		InitializeComponent();
		BindingContext = viewModel;
       viewModel.ResetState();
	}
}
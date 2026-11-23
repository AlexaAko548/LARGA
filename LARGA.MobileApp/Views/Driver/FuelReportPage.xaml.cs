using LARGA.MobileApp.ViewModels.Driver;

namespace LARGA.MobileApp.Views.Driver;

public partial class FuelReportPage : ContentPage
{
	private readonly FuelReportViewModel _viewModel;
	private bool _shiftChecked;

	public FuelReportPage(FuelReportViewModel viewModel)
	{
		InitializeComponent();
		BindingContext = _viewModel = viewModel;
		viewModel.RestoreDraftOrReset();
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		// REQ-4.3-3: no fuel report without a shift to link it to. Checked once per visit - the
		// scan pages open modally on top of this one and bring OnAppearing back.
		if (_shiftChecked) return;
		_shiftChecked = true;

		if (!await _viewModel.EnsureShiftAsync())
		{
			await Shell.Current.GoToAsync("..");
		}
	}
}

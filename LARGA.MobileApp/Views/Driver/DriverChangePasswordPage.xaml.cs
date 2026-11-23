using LARGA.MobileApp.ViewModels.Driver;

namespace LARGA.MobileApp.Views.Driver;

public partial class DriverChangePasswordPage : ContentPage
{
    private readonly DriverChangePasswordViewModel _viewModel;

    public DriverChangePasswordPage()
    {
        InitializeComponent();
        _viewModel = new DriverChangePasswordViewModel();
        BindingContext = _viewModel;
    }

    // The phone's back button: same "discard changes?" check as the nav bar's arrow.
    protected override bool OnBackButtonPressed()
    {
        if (!_viewModel.HasChanges)
        {
            return base.OnBackButtonPressed();
        }

        _ = _viewModel.GoBackAsync();
        return true;
    }
}

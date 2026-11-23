using LARGA.MobileApp.ViewModels.Manager;

namespace LARGA.MobileApp.Views.Manager;

public partial class ChangePasswordPage : ContentPage
{
    private readonly ChangePasswordViewModel _viewModel;

    public ChangePasswordPage()
    {
        InitializeComponent();
        _viewModel = new ChangePasswordViewModel();
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

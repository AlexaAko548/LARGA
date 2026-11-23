using LARGA.MobileApp.ViewModels.Manager;

namespace LARGA.MobileApp.Views.Manager;

public partial class UpdateContactNumberPage : ContentPage
{
    private readonly UpdateContactNumberViewModel _viewModel;

    public UpdateContactNumberPage()
    {
        InitializeComponent();
        _viewModel = new UpdateContactNumberViewModel();
        BindingContext = _viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.LoadCurrentNumberCommand.Execute(null);
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

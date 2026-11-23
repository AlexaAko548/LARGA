using LARGA.MobileApp.ViewModels.Manager;

namespace LARGA.MobileApp.Views.Manager;

public partial class ManagerProfilePage : ContentPage
{
    private readonly ManagerProfileViewModel _viewModel;

    public ManagerProfilePage()
    {
        InitializeComponent();
        _viewModel = new ManagerProfileViewModel();
        BindingContext = _viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.LoadProfileCommand.Execute(null);
    }
}

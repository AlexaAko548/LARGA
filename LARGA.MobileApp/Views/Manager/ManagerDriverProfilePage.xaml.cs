using LARGA.MobileApp.Services;
using LARGA.MobileApp.ViewModels.Manager;

namespace LARGA.MobileApp.Views.Manager;

public partial class ManagerDriverProfilePage : ContentPage
{
    private readonly ManagerDriverProfileViewModel _viewModel;

    public ManagerDriverProfilePage(IOcrService ocrService)
    {
        InitializeComponent();
        _viewModel = new ManagerDriverProfileViewModel(ocrService);
        BindingContext = _viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Also fires when returning from the "Upload License Photo" modal, so the card
        // reflects a just-saved scan without the manager having to navigate away and back.
        _viewModel.ReloadCommand.Execute(null);
    }
}

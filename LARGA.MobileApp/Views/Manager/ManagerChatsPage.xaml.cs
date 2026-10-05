using LARGA.MobileApp.ViewModels.Manager;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Views.Manager;

public partial class ManagerChatsPage : ContentPage
{
    private readonly ChatsViewModel _viewModel;

    public ManagerChatsPage(ChatsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Refresh the driver list each time the tab is shown; chat previews stay live via the listener.
        _ = _viewModel.LoadDriversAsync();
    }
}
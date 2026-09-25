using LARGA.MobileApp.ViewModels.Manager;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Views.Manager;

public partial class ManagerChatsPage : ContentPage
{
    public ManagerChatsPage(ChatsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
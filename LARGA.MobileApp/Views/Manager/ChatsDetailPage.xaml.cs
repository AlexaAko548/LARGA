using LARGA.MobileApp.ViewModels.Manager;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Views.Manager;

public partial class ChatsDetailPage : ContentPage
{
    public ChatsDetailPage(ChatsDetailViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
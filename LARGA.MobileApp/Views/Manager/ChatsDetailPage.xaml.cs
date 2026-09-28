using Microsoft.Maui.Controls;
using LARGA.MobileApp.ViewModels.Manager;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;

namespace LARGA.MobileApp.Views.Manager;

public partial class ChatsDetailPage : ContentPage
{
    public ChatsDetailPage(ChatsDetailViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;

        // Wire up the scroll action from the ViewModel
        viewModel.ScrollToBottom = async () =>
        {
            if (viewModel.Messages.Count > 0)
            {
                // A tiny delay ensures the UI has fully drawn the items before scrolling
                await Task.Delay(50);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    // Scroll to the last item. animate: false prevents a jarring visual jump on open.
                    MessagesCollectionView.ScrollTo(viewModel.Messages.Count - 1, position: ScrollToPosition.End, animate: false);
                });
            }
        };
    }
}
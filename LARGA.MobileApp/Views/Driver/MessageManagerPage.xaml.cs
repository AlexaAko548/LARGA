using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Controls;
using System;

namespace LARGA.MobileApp.Views.Driver;

public partial class MessageManagerPage : ContentPage
{
    public MessageManagerPage(MessageManagerViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;

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

    private async void OnBackButtonClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}
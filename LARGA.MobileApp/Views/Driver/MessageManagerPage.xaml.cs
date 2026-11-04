using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Controls;
using System;

namespace LARGA.MobileApp.Views.Driver;

public partial class MessageManagerPage : ContentPage
{
    private readonly LARGA.MobileApp.Services.UnreadBadgeService _badges;
    private string? _shownDriverId;

    public MessageManagerPage(MessageManagerViewModel viewModel, LARGA.MobileApp.Services.UnreadBadgeService badges)
    {
        InitializeComponent();
        BindingContext = viewModel;
        _badges = badges;

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

    // While the driver has their conversation open it's kept read for them, and the managers'
    // replies don't pop up as banners (UnreadBadgeService).
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _shownDriverId = Plugin.Firebase.Auth.CrossFirebaseAuth.Current.CurrentUser?.Uid;
        if (!string.IsNullOrEmpty(_shownDriverId))
        {
            _badges.SetActiveConversation(_shownDriverId);
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _badges.SetActiveConversation(null, leaving: _shownDriverId);
        _shownDriverId = null;
    }

    private async void OnBackButtonClicked(object sender, EventArgs e)
    {
        try
        {
            await LARGA.MobileApp.Services.NavigationGate.GoBackAsync();
        }
        catch (Exception ex)
        {
            // Navigation can fail mid-transition; never let a back tap crash the app.
            System.Diagnostics.Debug.WriteLine($"Back navigation failed: {ex.Message}");
        }
    }
}
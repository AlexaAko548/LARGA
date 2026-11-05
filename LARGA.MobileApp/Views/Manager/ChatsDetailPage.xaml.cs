using Microsoft.Maui.Controls;
using LARGA.MobileApp.ViewModels.Manager;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;

namespace LARGA.MobileApp.Views.Manager;

public partial class ChatsDetailPage : ContentPage
{
    private readonly ChatsDetailViewModel _viewModel;
    private readonly LARGA.MobileApp.Services.UnreadBadgeService _badges;
    private string? _shownDriverId;

    public ChatsDetailPage(ChatsDetailViewModel viewModel, LARGA.MobileApp.Services.UnreadBadgeService badges)
    {
        InitializeComponent();
        BindingContext = viewModel;
        _viewModel = viewModel;
        _badges = badges;

        // The driver can be set after the page appears (Shell query parameters); follow it.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatsDetailViewModel.DriverId) && _shownDriverId != null)
            {
                ShowConversation();
            }
        };

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

    // While this conversation is on screen it's kept read for this staff member, and its pushes
    // don't pop up as banners (UnreadBadgeService).
    protected override void OnAppearing()
    {
        base.OnAppearing();
        ShowConversation();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _badges.SetActiveConversation(null, leaving: _shownDriverId);
        _shownDriverId = null;
    }

    private void ShowConversation()
    {
        _shownDriverId = _viewModel.DriverId ?? string.Empty;
        if (_shownDriverId.Length > 0)
        {
            _badges.SetActiveConversation(_shownDriverId);
        }
    }
}
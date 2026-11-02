using LARGA.MobileApp.ViewModels.Manager;

namespace LARGA.MobileApp.Views.Manager;

public partial class AlertCenterPage : ContentPage
{
    private readonly AlertCenterViewModel _viewModel;

    public AlertCenterPage(AlertCenterViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;

        // Opened from an SOS push: bring that alert into view.
        _viewModel.FocusRequested += (_, alert) =>
            Dispatcher.Dispatch(() => AlertsList.ScrollTo(alert, position: ScrollToPosition.Start, animate: true));

        // Switching All / Unread starts the new list from the top, not wherever the other one was scrolled to.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AlertCenterViewModel.ShowUnreadOnly) && _viewModel.AlertGroups.Count > 0)
            {
                Dispatcher.Dispatch(() => AlertsList.ScrollTo(0, groupIndex: 0, position: ScrollToPosition.Start, animate: false));
            }
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.LoadAlertsCommand.Execute(null);
        _viewModel.ApplyPendingFocus();
    }
}

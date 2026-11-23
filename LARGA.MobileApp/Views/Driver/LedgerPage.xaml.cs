using LARGA.MobileApp.Services;
using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Views.Driver;

public partial class LedgerPage : ContentPage
{
    private readonly UnreadBadgeService _badges;

    // Updates counted on the Ledger tab that this visit has shown the driver (the banner at the top).
    private int _newThisVisit;

    public LedgerPage(LedgerViewModel viewModel, UnreadBadgeService badges)
    {
        InitializeComponent();
        BindingContext = viewModel;
        _badges = badges;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (BindingContext is LedgerViewModel viewModel)
        {
            // The red number on this tab: the driver is looking at their ledger now, so say what was new and
            // clear it. It stays until here - opening the app or another tab doesn't count as seeing it.
            _newThisVisit = _badges.MarkLedgerSeen();
            viewModel.ShowNewUpdates(_newThisVisit);
            _badges.Changed -= OnBadgesChanged;
            _badges.Changed += OnBadgesChanged;

            try
            {
                await viewModel.LoadDynamicLedgerDataAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ledger load failed: {ex.Message}");
            }

            viewModel.StartListening();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _badges.Changed -= OnBadgesChanged;
        if (BindingContext is LedgerViewModel viewModel)
        {
            viewModel.StopListening();
        }
    }

    // A payment or adjustment that arrives while the Ledger is open is on screen already (the page refreshes by
    // itself), so it counts as seen straight away rather than lighting up the tab behind the driver's back.
    private void OnBadgesChanged(object? sender, EventArgs e)
    {
        if (_badges.LedgerCount <= 0 || BindingContext is not LedgerViewModel viewModel) return;

        _newThisVisit += _badges.MarkLedgerSeen();
        viewModel.ShowNewUpdates(_newThisVisit);
    }
}

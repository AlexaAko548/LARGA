using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using System;

namespace LARGA.MobileApp.Views.Driver;

public partial class DriverDashboardPage : ContentPage
{
    public DriverDashboardPage(DriverDashboardViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Intercepts the page load to instantly update the ONLINE/OFFLINE UI
        if (BindingContext is DriverDashboardViewModel vm)
        {
            vm.IsOffline = !Preferences.Get("IsShiftActive", false);

            // Then confirm against Firestore (the shift may have been auto-closed, or started
            // on another phone) - see SyncOpenShiftAsync.
            _ = vm.SyncOpenShiftAsync();
            vm.ResumeWatchingUnit();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // LAR-123: the unit's live maintenance listeners only run while the dashboard is showing.
        if (BindingContext is DriverDashboardViewModel vm)
        {
            vm.StopWatchingUnit();
        }
    }
}
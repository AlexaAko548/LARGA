using LARGA.MobileApp.Views.Auth;
using LARGA.MobileApp.Views.Driver;
using LARGA.MobileApp.Views.Manager;

namespace LARGA.MobileApp;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("login", typeof(LoginPage));
        Routing.RegisterRoute("forgot-password-email", typeof(ForgotPasswordEmailPage));
        Routing.RegisterRoute("message-manager", typeof(MessageManagerPage));
        Routing.RegisterRoute("driver-dashboard", typeof(DriverDashboardPage));
        Routing.RegisterRoute("manager-dashboard", typeof(ManagerDashboardPage));
        Routing.RegisterRoute("pre-shift-step1", typeof(Views.Driver.PreShiftStep1Page));
        Routing.RegisterRoute("pre-shift-step2", typeof(Views.Driver.PreShiftStep2Page));
        Routing.RegisterRoute("odometer-scan", typeof(Views.Driver.OdometerScanPage));
        Routing.RegisterRoute("active-shift", typeof(Views.Driver.ActiveShiftPage));
        Routing.RegisterRoute("end-shift-step1", typeof(Views.Driver.EndShiftStep1Page));
        Routing.RegisterRoute("end-shift-step2", typeof(Views.Driver.EndShiftStep2Page));
        Routing.RegisterRoute("shift-completed", typeof(Views.Driver.ShiftCompletedPage));
        Routing.RegisterRoute("vehicle-defect-page", typeof(Views.Driver.VehicleDefectPage));
    }

    protected override void OnNavigated(ShellNavigatedEventArgs args)
    {
        base.OnNavigated(args);

        // Detects when the user clicks a bottom tab icon to switch sections
        if (args.Source == ShellNavigationSource.ShellSectionChanged)
        {
            // Check if they tapped the active Home tab
            if (args.Current != null && args.Current.Location.OriginalString.Contains("driver-dashboard"))
            {
                // If the Active Shift screen (or any other sub-page) is stuck on top, destroy it
                if (Shell.Current.Navigation.NavigationStack.Count > 1)
                {
                    // Instantly snaps back to the root dashboard
                    Shell.Current.Navigation.PopToRootAsync(false);
                }
            }
        }
    }
}
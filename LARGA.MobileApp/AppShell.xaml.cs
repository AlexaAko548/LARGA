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
    }

    protected override void OnNavigating(ShellNavigatingEventArgs args)
    {
        base.OnNavigating(args);

        // Detects if the user tapped the tab they are already currently inside
        if (args.Source == ShellNavigationSource.ShellSectionChanged)
        {
            var currentRoute = Shell.Current?.CurrentState?.Location?.OriginalString;
            var targetRoute = args.Target?.Location?.OriginalString;

            if (currentRoute != null && targetRoute != null && currentRoute == targetRoute)
            {
                // Double-tap detected: Pop the stack back to the root Dashboard
                Shell.Current.Navigation.PopToRootAsync(true);
            }
        }
    }
}
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
    }
}

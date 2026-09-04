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
        Routing.RegisterRoute("end-shift-step1", typeof(Views.Driver.EndShiftStep1Page));
        Routing.RegisterRoute("end-shift-step2", typeof(Views.Driver.EndShiftStep2Page));
        Routing.RegisterRoute("shift-completed", typeof(Views.Driver.ShiftCompletedPage));
    }
}
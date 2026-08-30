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
	}
}

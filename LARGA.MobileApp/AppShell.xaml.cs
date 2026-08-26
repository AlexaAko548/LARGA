using LARGA.MobileApp.Views;

namespace LARGA.MobileApp;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();

		Routing.RegisterRoute("login", typeof(LoginPage));
		// Routing.RegisterRoute("driver-dashboard", typeof(DriverDashboardPage));
		// Routing.RegisterRoute("manager-dashboard", typeof(ManagerDashboardPage));
	}
}

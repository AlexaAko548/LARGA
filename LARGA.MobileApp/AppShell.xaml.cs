using LARGA.MobileApp.Views;

namespace LARGA.MobileApp;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();

		Routing.RegisterRoute("login", typeof(LoginPage));
		Routing.RegisterRoute("forgot-password-email", typeof(ForgotPasswordEmailPage));
		Routing.RegisterRoute("forgot-password-verify", typeof(ForgotPasswordVerifyPage));
		Routing.RegisterRoute("forgot-password-new", typeof(ForgotPasswordNewPasswordPage));
		// Routing.RegisterRoute("driver-dashboard", typeof(DriverDashboardPage));
		// Routing.RegisterRoute("manager-dashboard", typeof(ManagerDashboardPage));
	}
}

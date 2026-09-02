namespace LARGA.MobileApp.Views.Manager;

public partial class ManagerDashboardPage : ContentPage
{
	public ManagerDashboardPage()
	{
		InitializeComponent();
	}

	private async void OnViewAlertCenterClicked(object sender, EventArgs e)
	{
		await Shell.Current.GoToAsync("///alertcenter");
	}
}
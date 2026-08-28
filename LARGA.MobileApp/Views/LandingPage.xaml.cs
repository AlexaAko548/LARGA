namespace LARGA.MobileApp.Views;

public partial class LandingPage : ContentPage
{
    public LandingPage()
    {
        InitializeComponent();
    }

    private async void OnLoginClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//login");
    }

    private async void OnPreviewChecklistClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//preshiftchecklist");
    }

    private async void OnPreviewAlertCenterClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//alertcenter");
    }
}
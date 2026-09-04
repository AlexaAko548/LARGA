namespace LARGA.MobileApp.Views.Driver;

public partial class ShiftCompletedPage : ContentPage
{
    public ShiftCompletedPage()
    {
        InitializeComponent();
    }

    private async void OnReturnHomeClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("driver-dashboard");
    }
}
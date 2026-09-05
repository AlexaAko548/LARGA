using LARGA.MobileApp.ViewModels.Driver;

namespace LARGA.MobileApp.Views.Driver;

public partial class EndShiftStep1Page : ContentPage
{
    public EndShiftStep1Page(EndShiftStep1ViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnBackButtonClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("active-shift");
    }
}
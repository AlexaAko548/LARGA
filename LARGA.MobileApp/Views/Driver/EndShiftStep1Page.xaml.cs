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
        try
        {
            await Shell.Current.GoToAsync("active-shift");
        }
        catch (Exception ex)
        {
            // Navigation can fail mid-transition; never let a back tap crash the app.
            System.Diagnostics.Debug.WriteLine($"Back navigation failed: {ex.Message}");
        }
    }
}
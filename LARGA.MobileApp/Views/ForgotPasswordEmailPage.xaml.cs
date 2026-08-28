using LARGA.MobileApp.ViewModels;

namespace LARGA.MobileApp.Views;

public partial class ForgotPasswordEmailPage : ContentPage
{
    public ForgotPasswordEmailPage(ForgotPasswordViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnBackButtonClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}

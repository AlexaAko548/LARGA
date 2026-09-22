using LARGA.MobileApp.ViewModels.Manager;

namespace LARGA.MobileApp.Views.Manager;

public partial class UpdateContactNumberPage : ContentPage
{
    public UpdateContactNumberPage()
    {
        InitializeComponent();
        BindingContext = new UpdateContactNumberViewModel();
    }
}

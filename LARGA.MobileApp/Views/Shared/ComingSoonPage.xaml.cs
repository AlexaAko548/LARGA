using LARGA.MobileApp.ViewModels.Shared;

namespace LARGA.MobileApp.Views.Shared;

public partial class ComingSoonPage : ContentPage
{
    public ComingSoonPage()
    {
        InitializeComponent();
        BindingContext = new ComingSoonViewModel();
    }
}

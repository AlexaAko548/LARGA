using LARGA.MobileApp.ViewModels.Driver;

namespace LARGA.MobileApp.Views.Driver;

public partial class DefectReportDetailPage : ContentPage
{
    public DefectReportDetailPage()
    {
        InitializeComponent();
        BindingContext = new DefectReportDetailViewModel();
    }
}

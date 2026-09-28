using LARGA.MobileApp.ViewModels.Driver;
using LARGA.SharedCore.Services;

namespace LARGA.MobileApp.Views.Driver;

public partial class VehicleDefectPage : ContentPage
{
    public VehicleDefectPage(IMaintenanceService maintenanceService, IShiftManagementService shiftManagementService, IPhotoStorageService photoStorage)
    {
        InitializeComponent();
        BindingContext = new VehicleDefectViewModel(maintenanceService, shiftManagementService, photoStorage);
    }
}
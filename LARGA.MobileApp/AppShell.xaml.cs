using LARGA.MobileApp.Views.Auth;
using LARGA.MobileApp.Views.Driver;
using LARGA.MobileApp.Views.Manager;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        Routing.RegisterRoute("login", typeof(LoginPage));
        Routing.RegisterRoute("forgot-password-email", typeof(ForgotPasswordEmailPage));
        Routing.RegisterRoute("message-manager", typeof(MessageManagerPage));
        Routing.RegisterRoute("pre-shift-step1", typeof(Views.Driver.PreShiftStep1Page));
        Routing.RegisterRoute("pre-shift-step2", typeof(Views.Driver.PreShiftStep2Page));
        //Routing.RegisterRoute("odometer-scan", typeof(Views.Driver.OdometerScanPage));
        Routing.RegisterRoute("active-shift", typeof(Views.Driver.ActiveShiftPage));
        Routing.RegisterRoute("end-shift-step1", typeof(Views.Driver.EndShiftStep1Page));
        Routing.RegisterRoute("end-shift-step2", typeof(Views.Driver.EndShiftStep2Page));
        Routing.RegisterRoute("shift-completed", typeof(Views.Driver.ShiftCompletedPage));
        Routing.RegisterRoute("debt-details", typeof(Views.Driver.DebtDetailPage));
        Routing.RegisterRoute("payment-history", typeof(Views.Driver.PaymentHistoryPage));
        Routing.RegisterRoute("vehicle-defect-page", typeof(Views.Driver.VehicleDefectPage));
        Routing.RegisterRoute("defect-report-detail", typeof(Views.Driver.DefectReportDetailPage));
        Routing.RegisterRoute("driver-change-password", typeof(Views.Driver.DriverChangePasswordPage));
        Routing.RegisterRoute("driver-update-contact-number", typeof(Views.Driver.DriverUpdateContactNumberPage));
        Routing.RegisterRoute("driver-management", typeof(Views.Manager.DriverManagementPage));
        Routing.RegisterRoute("manager-driver-profile", typeof(Views.Manager.ManagerDriverProfilePage));
        Routing.RegisterRoute("fleet-registry", typeof(Views.Manager.FleetRegistryPage));
        Routing.RegisterRoute("change-password", typeof(Views.Manager.ChangePasswordPage));
        Routing.RegisterRoute("update-contact-number", typeof(Views.Manager.UpdateContactNumberPage));
        Routing.RegisterRoute("coming-soon", typeof(Views.Shared.ComingSoonPage));
        Routing.RegisterRoute("fuel-report-page", typeof(Views.Driver.FuelReportPage));
        Routing.RegisterRoute(nameof(FuelReportDetailPage), typeof(Views.Driver.FuelReportDetailPage));
        Routing.RegisterRoute("ChatsDetailPage", typeof(ChatsDetailPage));
    }
}
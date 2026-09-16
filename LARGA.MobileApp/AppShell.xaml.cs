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
        Routing.RegisterRoute("fuel-report-page", typeof(Views.Driver.FuelReportPage));
    }
}
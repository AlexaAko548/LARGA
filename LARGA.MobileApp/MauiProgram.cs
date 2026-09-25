using Microsoft.Extensions.Logging;
using LARGA.MobileApp.Views.Auth;
using LARGA.MobileApp.Views.Driver;
using LARGA.MobileApp.Views.Manager;
using LARGA.SharedCore.Services;
using CommunityToolkit.Maui;
#if ANDROID
using Plugin.Firebase.Core.Platforms.Android;
#endif
using Microsoft.Maui.LifecycleEvents;
using LARGA.MobileApp.ViewModels.Driver;
using LARGA.MobileApp.ViewModels.Auth;
using LARGA.MobileApp.ViewModels.Manager;
using Plugin.Firebase.CloudMessaging;
using LARGA.MobileApp.Services;
using LARGA.MobileApp.Views.Shared;
using Camera.MAUI; // Added Camera.MAUI namespace
using SkiaSharp.Views.Maui.Controls.Hosting;
using Mapsui.Utilities;
using Mapsui.Widgets;
using Mapsui.Widgets.InfoWidgets;

namespace LARGA.MobileApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // Mapsui's built-in FPS/log overlay (LAR-48 Live Fleet map) defaults to
        // OnlyInDebugMode - only ON when a debugger is attached. That's exactly how QA runs
        // the app from Visual Studio, so they always see it, while a plain adb-installed APK
        // (how this was tested here) never does. Force both off unconditionally so nobody
        // sees Mapsui's internal debug info on the map, regardless of how they launched it.
        LoggingWidget.ShowLoggingInMap = ActiveMode.No;
        Performance.DefaultIsActive = ActiveMode.No;

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .UseMauiCameraView() // Registered the Camera View
            .UseSkiaSharp() // Required by Mapsui (LAR-48 Live Fleet map, renders MapTiler tiles)
            .RegisterFirebaseServices()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                fonts.AddFont("MaterialIcons-Regular.ttf", "MaterialIcons");
                fonts.AddFont("Inter-Regular.ttf", "InterRegular");
                fonts.AddFont("Inter-Bold.ttf", "InterBold");
                fonts.AddFont("DMMono-Regular.ttf", "DMMonoRegular");
                fonts.AddFont("DMMono-Medium.ttf", "DMMonoMedium");
                fonts.AddFont("BarlowCondensed-Bold.ttf", "BarlowCondensedBold");
                fonts.AddFont("BarlowCondensed-SemiBold.ttf", "BarlowCondensedSemiBold");
            });

        // Register Services
        builder.Services.AddSingleton<IFirebaseAuthService, FirebaseAuthService>();
        builder.Services.AddSingleton<IChatService, ChatService>();
        builder.Services.AddSingleton<IShiftManagementService, ShiftManagementService>();
        builder.Services.AddSingleton<INotificationService, NotificationService>();
        builder.Services.AddSingleton<IMaintenanceService, MaintenanceService>();
        builder.Services.AddSingleton<IFuelService, FuelService>();

#if ANDROID
        builder.Services.AddSingleton<IOcrService, LARGA.MobileApp.Platforms.Android.Services.AndroidOcrService>();
#endif

        // Register ViewModels 
        builder.Services.AddTransient<LandingViewModel>();
        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddTransient<ForgotPasswordViewModel>();
        builder.Services.AddTransient<DriverDashboardViewModel>();
        builder.Services.AddTransient<ProfileViewModel>();
        builder.Services.AddTransient<MessageManagerViewModel>();
        builder.Services.AddTransient<PreShiftStep1ViewModel>();
        builder.Services.AddTransient<PreShiftStep2ViewModel>();
        builder.Services.AddTransient<ShiftCompletedViewModel>();
        builder.Services.AddSingleton<ActiveShiftViewModel>();
        builder.Services.AddTransient<LedgerViewModel>();
        builder.Services.AddTransient<PaymentHistoryViewModel>();
        builder.Services.AddTransient<DebtDetailViewModel>();
        builder.Services.AddTransient<ReportsViewModel>();
        builder.Services.AddTransient<FuelReportViewModel>();
        builder.Services.AddTransient<FuelReportDetailViewModel>();
        builder.Services.AddTransient<ChatsViewModel>();
        builder.Services.AddTransient<ChatsDetailViewModel>();

        // Register Views 
        builder.Services.AddTransient<LandingPage>();
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<ForgotPasswordEmailPage>();
        builder.Services.AddTransient<DriverDashboardPage>();
        builder.Services.AddTransient<ManagerDashboardPage>();
        builder.Services.AddTransient<LedgerPage>();
        builder.Services.AddTransient<ReportsPage>();
        builder.Services.AddTransient<ProfilePage>();
        builder.Services.AddTransient<MessageManagerPage>();
        builder.Services.AddTransient<ActiveShiftPage>();
        builder.Services.AddTransient<PreShiftStep1Page>();
        builder.Services.AddTransient<PreShiftStep2Page>();
        builder.Services.AddTransient<LARGA.MobileApp.ViewModels.Manager.AlertCenterViewModel>();
        builder.Services.AddTransient<AlertCenterPage>();
        builder.Services.AddTransient<EndShiftStep1ViewModel>();
        builder.Services.AddTransient<EndShiftStep1Page>();
        builder.Services.AddTransient<EndShiftStep2ViewModel>();
        builder.Services.AddTransient<EndShiftStep2Page>();
        builder.Services.AddTransient<ShiftCompletedPage>();
        builder.Services.AddTransient<VehicleDefectViewModel>();
        builder.Services.AddTransient<VehicleDefectPage>();
        builder.Services.AddTransient<PaymentHistoryPage>();
        builder.Services.AddTransient<DebtDetailPage>();
        builder.Services.AddTransient<DefectReportDetailViewModel>();
        builder.Services.AddTransient<DefectReportDetailPage>();
        builder.Services.AddTransient<ManagerProfilePage>();
        builder.Services.AddTransient<DriverManagementPage>();
        builder.Services.AddTransient<ManagerDriverProfilePage>();
        builder.Services.AddTransient<ManagerChatsPage>();
        builder.Services.AddTransient<ManagerLedgerPage>();
        builder.Services.AddTransient<FleetRegistryPage>();
        builder.Services.AddTransient<ChangePasswordPage>();
        builder.Services.AddTransient<UpdateContactNumberPage>();
        builder.Services.AddTransient<ComingSoonPage>();
        builder.Services.AddTransient<FuelReportPage>();
        builder.Services.AddTransient<Views.Driver.ScanFuelReceiptPage>();
        builder.Services.AddTransient<FuelReportDetailPage>();
        builder.Services.AddTransient<ChatsDetailPage>();


#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }

    private static MauiAppBuilder RegisterFirebaseServices(this MauiAppBuilder builder)
    {
        builder.ConfigureLifecycleEvents(events =>
        {
#if ANDROID
            events.AddAndroid(android => android.OnCreate((activity, state) =>
            {
                Plugin.Firebase.Core.Platforms.Android.CrossFirebase.Initialize(activity, () => Platform.CurrentActivity ?? activity);
            }));
#elif IOS
            events.AddiOS(ios => ios.FinishedLaunching((app, options) =>
            {
                Plugin.Firebase.Core.Platforms.iOS.CrossFirebase.Initialize();
                return true;
            }));
#endif
        });

        return builder;
    }
}
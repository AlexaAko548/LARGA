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
using Plugin.Firebase.CloudMessaging;
using Microsoft.Maui.LifecycleEvents;

namespace LARGA.MobileApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
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

        // Register ViewModels (CRITICAL: Make sure LandingViewModel is here!)
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

        // Register Views (CRITICAL: Make sure LandingPage is here!)
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
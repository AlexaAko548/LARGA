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

namespace LARGA.MobileApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                fonts.AddFont("MaterialIcons-Regular.ttf", "MaterialIcons");
            })
            .ConfigureLifecycleEvents(events =>
            {
#if ANDROID
                events.AddAndroid(android => android.OnCreate((activity, _) =>
                CrossFirebase.Initialize(activity, () => Microsoft.Maui.ApplicationModel.Platform.CurrentActivity)));
#endif
            });

        // Register Services
        builder.Services.AddSingleton<IFirebaseAuthService, FirebaseAuthService>();
        builder.Services.AddSingleton<IChatService, ChatService>();

        // Register ViewModels (CRITICAL: Make sure LandingViewModel is here!)
        builder.Services.AddTransient<LandingViewModel>();
        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddTransient<ForgotPasswordViewModel>();
        builder.Services.AddTransient<DriverDashboardViewModel>();
        builder.Services.AddTransient<ProfileViewModel>();
        builder.Services.AddTransient<MessageManagerViewModel>();
        builder.Services.AddTransient<PreShiftStep1ViewModel>();
        builder.Services.AddTransient<PreShiftStep2ViewModel>();

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
}
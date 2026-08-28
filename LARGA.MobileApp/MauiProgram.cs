using Microsoft.Extensions.Logging;
using LARGA.MobileApp.ViewModels;
using LARGA.MobileApp.Views;
using LARGA.SharedCore.Services;
using CommunityToolkit.Maui;

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
            });

        // Register Services
        builder.Services.AddSingleton<IFirebaseAuthService, FirebaseAuthService>();

        // Register ViewModels (CRITICAL: Make sure LandingViewModel is here!)
        builder.Services.AddTransient<LandingViewModel>();
        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddTransient<ForgotPasswordViewModel>();

        // Register Views (CRITICAL: Make sure LandingPage is here!)
        builder.Services.AddTransient<LandingPage>();
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<ForgotPasswordEmailPage>();
        builder.Services.AddTransient<ForgotPasswordVerifyPage>();
        builder.Services.AddTransient<ForgotPasswordNewPasswordPage>();
        builder.Services.AddTransient<DriverDashboardPage>();
        builder.Services.AddTransient<ManagerDashboardPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
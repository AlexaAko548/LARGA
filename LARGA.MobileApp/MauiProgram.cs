using Microsoft.Extensions.Logging;
using LARGA.MobileApp.ViewModels;
using LARGA.MobileApp.Views;
using LARGA.MobileApp.Services;
using CommunityToolkit.Maui;
using Plugin.Firebase.Core.Platforms;

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
			})
			.ConfigureLifecycleEvents(events =>
			{
#if ANDROID
				events.AddAndroid(android => android.OnCreate((activity, _) =>
					CrossFirebase.Initialize(activity)));
#endif
			});

		builder.Services.AddSingleton<IFirebaseAuthService, FirebaseAuthService>();
		builder.Services.AddSingleton<FirestoreService>();
		builder.Services.AddTransient<LoginViewModel>();
		builder.Services.AddTransient<LoginPage>();
		builder.Services.AddTransient<LandingPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
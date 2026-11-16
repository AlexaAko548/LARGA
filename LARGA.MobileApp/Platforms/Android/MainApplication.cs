using Android.App;
using Android.Runtime;

namespace LARGA.MobileApp;

[Application]
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	public override void OnCreate()
	{
		base.OnCreate();
		// Here, not only in MainActivity: FCM can start the process with no activity (app killed),
		// and a data-only chat push must still be checked and drawn by ForegroundPushPresenter -
		// left unset, Plugin.Firebase would post it on a null channel, which Android drops.
		Plugin.Firebase.CloudMessaging.FirebaseCloudMessagingImplementation.ShowLocalNotificationAction =
			Platforms.Android.Services.ForegroundPushPresenter.Show;
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

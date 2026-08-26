using Android.App;
using Android.Content.PM;
using Android.OS;
using Plugin.Firebase.Core.Platforms.Android;

namespace LARGA.DriverApp;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		CrossFirebase.Initialize(this, () => this, null, null);

		// Log FirebaseApp details to verify integration
		try
		{
			var app = Firebase.FirebaseApp.Instance;
			if (app != null)
			{
				var opts = app.Options;
				Android.Util.Log.Info("FIREBASE", $"App name: {app.Name}, ApplicationId: {opts?.ApplicationId}, ProjectId: {opts?.ProjectId}, ApiKey: {opts?.ApiKey}");
			}
			else
			{
				Android.Util.Log.Error("FIREBASE", "FirebaseApp.Instance is null");
			}
		}
		catch (System.Exception ex)
		{
			Android.Util.Log.Error("FIREBASE", ex.ToString());
		}

		// Fetch and log FCM token for testing using OnComplete listener (avoids awaiting Android.Gms.Tasks.Task)
		Firebase.Messaging.FirebaseMessaging.Instance.GetToken().AddOnCompleteListener(new TokenOnCompleteListener((t) =>
		{
			try
			{
				if (t.IsSuccessful)
				{
					var result = t.Result;
					var token = result?.ToString() ?? "<null>";
					Android.Util.Log.Info("FCM_TOKEN", token);
				}
				else
				{
					Android.Util.Log.Error("FCM_TOKEN", t.Exception?.ToString() ?? "Token retrieval failed");
				}
			}
			catch (System.Exception ex)
			{
				Android.Util.Log.Error("FCM_TOKEN", ex.ToString());
			}
		}));
	}

	class TokenOnCompleteListener : Java.Lang.Object, Android.Gms.Tasks.IOnCompleteListener
	{
		private readonly System.Action<Android.Gms.Tasks.Task> _onComplete;
		public TokenOnCompleteListener(System.Action<Android.Gms.Tasks.Task> onComplete) => _onComplete = onComplete;
		public void OnComplete(Android.Gms.Tasks.Task task) => _onComplete?.Invoke(task);
	}
}

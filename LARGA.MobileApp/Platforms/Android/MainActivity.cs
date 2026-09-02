using Android.App;
using Android.Content.PM;
using Android.OS;
using Plugin.Firebase.Core.Platforms.Android;
using System.Runtime.Versioning;

namespace LARGA.MobileApp;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		CrossFirebase.Initialize(this, () => this, null, null);

		CreateNotificationChannels();
	}

	[SupportedOSPlatform("android26.0")]
    private void CreateNotificationChannels()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);

            // 1. Channel for Pre-Shift Reminders
            var shiftChannel = new NotificationChannel(
                "pre_shift_channel",
                "Pre-Shift Reminders",
                NotificationImportance.High)
            {
                Description = "Notifications for upcoming driver shift schedules"
            };

            // 2. Channel for Manager Messages
            var chatChannel = new NotificationChannel(
                "chat_messages_channel",
                "Manager Messages",
                NotificationImportance.High)
            {
                Description = "Direct incoming chat messages from fleet managers"
            };

            notificationManager?.CreateNotificationChannel(shiftChannel);
            notificationManager?.CreateNotificationChannel(chatChannel);
        }
    }
}

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Microsoft.Extensions.DependencyInjection;
using Plugin.Firebase.CloudMessaging;
using Plugin.Firebase.Core.Platforms.Android;
using System.Runtime.Versioning;
using LARGA.MobileApp.Platforms.Android;
using LARGA.MobileApp.Platforms.Android.Emergency;
using LARGA.MobileApp.Services;

namespace LARGA.MobileApp;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private EmergencyCountdownCoordinator? _countdown;
    private readonly KeyboardDismisser _keyboard = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        CrossFirebase.Initialize(this, () => this, null, null);

        CreateNotificationChannels();

        // Pushes that arrive with the app open: shown on their own channel, except a chat message
        // for the conversation already on screen (ForegroundPushPresenter).
        FirebaseCloudMessagingImplementation.ShowLocalNotificationAction = Platforms.Android.Services.ForegroundPushPresenter.Show;
        HandleNotificationTap(Intent);

        _countdown = IPlatformApplication.Current?.Services.GetService<EmergencyCountdownCoordinator>();
        if (_countdown != null)
        {
            _countdown.Changed += OnCountdownChanged;
        }

        HandleEmergencyIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleNotificationTap(intent);
        HandleEmergencyIntent(intent);
    }

    /// <summary>
    /// A tapped push - one Android drew while the app was in the background (FCM extras) or one
    /// ForegroundPushPresenter posted - raises CrossFirebaseCloudMessaging.NotificationTapped, which
    /// App.xaml.cs routes (SOS, pre-shift reminder, chat message). Plugin.Firebase only does this when
    /// the activity hands it the intent. The FCM extras are removed afterwards so the same tap isn't
    /// routed twice if the activity is created again from this intent.
    /// </summary>
    private static void HandleNotificationTap(Intent? intent)
    {
        if (intent == null) return;
        try
        {
            FirebaseCloudMessagingImplementation.OnNewIntent(intent);
            intent.RemoveExtra("google.message_id");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Notification tap handling failed: {ex.Message}");
        }
    }

    /// <summary>Tap anywhere outside the focused text field to hide the keyboard (KeyboardDismisser).</summary>
    public override bool DispatchTouchEvent(MotionEvent? e)
    {
        if (e == null)
        {
            return base.DispatchTouchEvent(e);
        }
        return _keyboard.Dispatch(this, e, ev => base.DispatchTouchEvent(ev));
    }

    protected override void OnDestroy()
    {
        if (_countdown != null)
        {
            _countdown.Changed -= OnCountdownChanged;
        }
        base.OnDestroy();
    }

    /// <summary>
    /// LAR-86/87: opened by the SOS countdown's full-screen notification - show over the lock
    /// screen and wake the display so the driver can see and cancel it. App shows the pop-up.
    /// </summary>
    private void HandleEmergencyIntent(Intent? intent)
    {
        if (intent?.GetBooleanExtra(EmergencyDetectionService.ExtraShowCountdown, false) != true) return;

        if (_countdown?.Active != null)
        {
            SetShowOverLockScreen(true);
        }
    }

    private void OnCountdownChanged(object? sender, EventArgs e)
    {
        // Once the countdown is over, go back to normal lock-screen behaviour.
        if (_countdown?.Active == null)
        {
            RunOnUiThread(() => SetShowOverLockScreen(false));
        }
    }

    private void SetShowOverLockScreen(bool show)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(27))
        {
            SetShowWhenLocked(show);
            SetTurnScreenOn(show);
        }
    }

    [SupportedOSPlatform("android26.0")]
    private void CreateNotificationChannels()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);

            var shiftChannel = new NotificationChannel(
                "pre_shift_channel",
                "Pre-Shift Reminders",
                NotificationImportance.High)
            {
                Description = "Notifications for upcoming driver shift schedules"
            };

            // Chat pushes from ManagerWeb's ChatPushService (ChatPushPolicy.AndroidChannelId). Drivers
            // get the managers' replies here, and managers the drivers' messages - so a neutral name.
            // (Android keeps a channel's importance once created; the name and description update.)
            var chatChannel = new NotificationChannel(
                LARGA.SharedCore.Chats.ChatPushPolicy.AndroidChannelId,
                "Messages",
                NotificationImportance.High)
            {
                Description = "New chat messages between drivers and managers"
            };

            // Manager phones: SOS pushes from ManagerWeb's SosPushService (its AndroidChannelId).
            // Alarm sound and a long vibration so it's noticed in a pocket.
            var sosChannel = new NotificationChannel(
                "sos_alert_channel",
                "Driver SOS Alerts",
                NotificationImportance.High)
            {
                Description = "Emergency alerts raised by drivers (SOS button, hostile passenger, crash)",
                LockscreenVisibility = NotificationVisibility.Public,
            };
            sosChannel.EnableVibration(true);
            sosChannel.SetVibrationPattern(new long[] { 0, 800, 400, 800, 400, 800 });
            sosChannel.SetSound(
                Android.Media.RingtoneManager.GetDefaultUri(Android.Media.RingtoneType.Alarm),
                new Android.Media.AudioAttributes.Builder()
                    .SetUsage(Android.Media.AudioUsageKind.Alarm)!
                    .SetContentType(Android.Media.AudioContentType.Sonification)!
                    .Build());

            notificationManager?.CreateNotificationChannel(shiftChannel);
            notificationManager?.CreateNotificationChannel(chatChannel);
            notificationManager?.CreateNotificationChannel(sosChannel);
        }
    }
}

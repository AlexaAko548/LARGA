using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.App;

namespace LARGA.MobileApp.Platforms.Android.Services;

/// <summary>
/// GPS-012: keeps the shift's GPS telemetry running when the driver swipes LARGA away from Recents
/// or the screen goes off. It's a location-typed foreground service with a persistent notification
/// ("LARGA - shift tracking active") and StopWithTask=false, so Android keeps the app's process -
/// and the GpsTelemetryService loop in it, at its normal 30-second interval - alive after the task
/// is removed. It holds a partial wake lock so the loop isn't held up by Doze while the screen is off.
///
/// It does no tracking itself: <see cref="AndroidGpsTelemetryService"/> starts it alongside
/// GpsTelemetryService.Start (clock-in, or the dashboard resuming an open shift) and stops it with
/// GpsTelemetryService.Stop (clock-out, logout, or the shift closed elsewhere - the LAR-77
/// Contextual Auto-Cutoff), so it never runs outside an active shift. Break pauses stay in
/// GpsTelemetryService (no points while isOnBreak).
///
/// Separate from EmergencyDetectionService on purpose: that one stops itself when its sensors or
/// its own start fail, and shift tracking must not depend on it - nor show its "Safety monitor"
/// text. Not sticky: if Android kills the whole process anyway, Firebase isn't initialized without
/// the activity, so a restarted service couldn't write points; tracking resumes when the driver
/// opens the app (the dashboard re-starts it for the open shift).
/// </summary>
// Declared in AndroidManifest.xml (not a [Service] attribute, which can't set stopWithTask) - the
// Register name here must match that <service android:name>.
[Register("com.blmtaxi.larga.mobile.ShiftTrackingService")]
public sealed class ShiftTrackingService : Service
{
    public const string ActionStart = "larga.shifttracking.START";
    public const string ExtraShiftId = "larga.shifttracking.SHIFT_ID";

    private const string ChannelId = "shift_tracking_channel";
    private const int NotificationId = 8701;

    private PowerManager.WakeLock? _wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    [return: GeneratedEnum]
    public override StartCommandResult OnStartCommand(Intent? intent, [GeneratedEnum] StartCommandFlags flags, int startId)
    {
        if (intent?.Action != ActionStart)
        {
            // Restarted by the system with no shift to track (see the class summary).
            StopSelfCleanly();
            return StartCommandResult.NotSticky;
        }

        try
        {
            StartInForeground();
        }
        catch (Exception ex)
        {
            // e.g. Android 14+ refusing a location foreground service without the location permission.
            // The in-app telemetry loop still runs while the app is open, as before.
            global::Android.Util.Log.Error("LARGA_GPS", $"Shift tracking foreground start failed: {ex}");
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        AcquireWakeLock();
        return StartCommandResult.NotSticky;
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        // Swiped away from Recents: keep running (StopWithTask=false) - the shift is still active.
        global::Android.Util.Log.Info("LARGA_GPS", "App task removed - shift tracking continues.");
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        if (_wakeLock?.IsHeld == true)
        {
            _wakeLock.Release();
        }
        _wakeLock = null;
        base.OnDestroy();
    }

    private void StartInForeground()
    {
        CreateChannel();

        // Tapping the notification brings the driver back to the app.
        Intent? launch = PackageManager?.GetLaunchIntentForPackage(PackageName ?? string.Empty);
        PendingIntent? open = launch is null
            ? null
            : PendingIntent.GetActivity(this, 0, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetContentTitle("LARGA - shift tracking active");
        builder.SetContentText("Your location is shared with BLM only while your shift is active.");
        builder.SetSmallIcon(global::Android.Resource.Drawable.IcMenuMyLocation);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetPriority(NotificationCompat.PriorityLow);
        builder.SetCategory(NotificationCompat.CategoryService);
        if (open != null)
        {
            builder.SetContentIntent(open);
        }
        Notification notification = builder.Build()!;

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeLocation);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }
    }

    private void AcquireWakeLock()
    {
        if (_wakeLock?.IsHeld == true) return;

        var power = (PowerManager?)GetSystemService(PowerService);
        _wakeLock = power?.NewWakeLock(WakeLockFlags.Partial, "LARGA::ShiftTracking");
        _wakeLock?.Acquire();
    }

    private void CreateChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager == null) return;

        var channel = new NotificationChannel(ChannelId, "Shift Tracking", NotificationImportance.Low)
        {
            Description = "Shown while your shift's GPS tracking is running.",
        };
        channel.SetShowBadge(false);
        manager.CreateNotificationChannel(channel);
    }

    private void StopSelfCleanly()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
        {
            StopForeground(StopForegroundFlags.Remove);
        }
        else
        {
#pragma warning disable CS0618, CA1422
            StopForeground(true);
#pragma warning restore CS0618, CA1422
        }

        StopSelf();
    }

    /// <summary>Starts (or refreshes) the service for a shift. Never throws - if Android refuses
    /// (e.g. no location permission), the in-app telemetry loop still runs while the app is open.</summary>
    public static void Start(Context context, string shiftId)
    {
        try
        {
            var intent = new Intent(context, typeof(ShiftTrackingService));
            intent.SetAction(ActionStart);
            intent.PutExtra(ExtraShiftId, shiftId);
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StartService(intent);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("LARGA_GPS", $"Couldn't start shift tracking service: {ex}");
        }
    }

    public static void Stop(Context context)
    {
        try
        {
            context.StopService(new Intent(context, typeof(ShiftTrackingService)));
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("LARGA_GPS", $"Couldn't stop shift tracking service: {ex}");
        }
    }
}

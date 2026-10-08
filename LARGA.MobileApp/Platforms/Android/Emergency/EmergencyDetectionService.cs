using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Hardware;
using Android.Locations;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.App;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Storage;
using LARGA.MobileApp.Services;
using LARGA.Shared.Models.Entities;
using Location = Android.Locations.Location;

namespace LARGA.MobileApp.Platforms.Android.Emergency;

/// <summary>
/// LAR-86/87 background emergency detection. Runs as a foreground service for the length of a
/// shift so the accelerometer and GPS keep delivering with the app minimized or the screen off.
/// A detected Hostile shake or Crash starts a 5-second cancel countdown (popup + notification +
/// haptics); if it isn't cancelled the alert is written to Firestore and
/// <see cref="EmergencyCallHandler"/> is armed to auto-answer the manager's callback.
/// </summary>
#if DEBUG
// Exported in Debug builds only, so a test run can fire a protocol from adb (see SimulateHostile).
[Service(Name = "com.blmtaxi.larga.mobile.EmergencyDetectionService", Exported = true, ForegroundServiceType = ForegroundService.TypeLocation)]
#else
[Service(Name = "com.blmtaxi.larga.mobile.EmergencyDetectionService", Exported = false, ForegroundServiceType = ForegroundService.TypeLocation)]
#endif
public sealed class EmergencyDetectionService : Service, ISensorEventListener, ILocationListener
{
    public const string ActionStart = "larga.emergency.START";
    public const string ActionStop = "larga.emergency.STOP";
    public const string ActionCancelCountdown = "larga.emergency.CANCEL_COUNTDOWN";
    public const string ExtraShowCountdown = "larga.emergency.SHOW_COUNTDOWN";
#if DEBUG
    // adb shell am startservice -n com.blmtaxi.larga.mobile/com.blmtaxi.larga.mobile.EmergencyDetectionService -a larga.emergency.SIMULATE_CRASH
    private const string ActionSimulateHostile = "larga.emergency.SIMULATE_HOSTILE";
    private const string ActionSimulateCrash = "larga.emergency.SIMULATE_CRASH";
#endif

    private const string MonitorChannelId = "emergency_monitor_channel";
    // Silent on purpose: a Hostile countdown must not chime in front of the passenger. Haptics
    // are driven by the service itself.
    private const string CountdownChannelId = "emergency_countdown_channel";
    // Silent: the crash alarm is played by AndroidEmergencyFeedback, so the channel doesn't add a second one.
    private const string CrashAlertChannelId = "emergency_crash_alert_channel";
    // Created by earlier builds with a sound (channel sounds can't be changed afterwards), so they're
    // removed in favour of the channels above.
    private const string LegacyAlertChannelId = "emergency_alert_channel";
    private const string LegacyCrashAlarmChannelId = "emergency_crash_alarm_channel";

    private const int MonitorNotificationId = 8601;
    private const int CountdownNotificationId = 8602;
    private const int AlertNotificationId = 8603;

    private static readonly TimeSpan CountdownLength = TimeSpan.FromSeconds(5);
    private const float GravityAlpha = 0.8f;

    private SensorManager? _sensorManager;
    private Sensor? _accelerometer;
    private LocationManager? _locationManager;
    private PowerManager.WakeLock? _wakeLock;
    private bool _monitoring;

    private readonly HostileMotionDetector _hostile = new();
    private readonly CrashDetector _crash = new();
    private EmergencyCallHandler? _callHandler;
    private IEmergencyAlertService? _alertService;
    private EmergencyCountdownCoordinator? _coordinator;

    private readonly float[] _gravity = new float[3];
    private bool _gravityPrimed;

    private double _lastLat;
    private double _lastLng;
    private bool _hasFix;

    private readonly object _countdownGate = new();
    private CancellationTokenSource? _countdownCts;

    public override IBinder? OnBind(Intent? intent) => null;

    private NotificationManagerCompat Notifications => NotificationManagerCompat.From(this)!;

    public override void OnCreate()
    {
        base.OnCreate();

        IServiceProvider? services = IPlatformApplication.Current?.Services;
        _alertService = services?.GetService<IEmergencyAlertService>();
        _coordinator = services?.GetService<EmergencyCountdownCoordinator>();

        if (_alertService != null)
        {
            _callHandler = new EmergencyCallHandler(this, _alertService);
        }

        if (_coordinator != null)
        {
            _coordinator.CancelRequested += OnPopupCancelRequested;
        }

        _sensorManager = (SensorManager?)GetSystemService(SensorService);
        _accelerometer = _sensorManager?.GetDefaultSensor(SensorType.Accelerometer);
        _locationManager = (LocationManager?)GetSystemService(LocationService);

        if (_alertService == null || _coordinator == null)
        {
            EmergencyLog.Warn("Service created without the MAUI services - alerts can't be sent.");
        }
    }

    [return: GeneratedEnum]
    public override StartCommandResult OnStartCommand(Intent? intent, [GeneratedEnum] StartCommandFlags flags, int startId)
    {
        switch (intent?.Action)
        {
            case ActionStop:
                StopSelfCleanly();
                return StartCommandResult.NotSticky;

            case ActionCancelCountdown:
                CancelCountdown("notification action");
                return StartCommandResult.Sticky;

#if DEBUG
            case ActionSimulateHostile:
            case ActionSimulateCrash:
                if (!_monitoring)
                {
                    // Only meaningful during a shift - otherwise the alert has nothing to attach to.
                    EmergencyLog.Warn("Simulate ignored: the monitor isn't running (clock in first).");
                    StopSelf();
                    return StartCommandResult.NotSticky;
                }
                EmergencyLog.Info($"Simulated trigger: {intent!.Action}");
                BeginCountdown(intent.Action == ActionSimulateCrash ? EmergencyAlert.Crash : EmergencyAlert.Hostile);
                return StartCommandResult.Sticky;
#endif

            default:
                // A null intent is Android restarting the sticky service after killing it - only
                // pick monitoring back up if the shift is still open.
                if (intent == null && !Preferences.Get("IsShiftActive", false))
                {
                    EmergencyLog.Info("Restarted with no active shift - stopping.");
                    StopSelfCleanly();
                    return StartCommandResult.NotSticky;
                }

                StartMonitoring();
                return StartCommandResult.Sticky;
        }
    }

    private void StartMonitoring()
    {
        if (_monitoring) return;

        CreateChannels();

        try
        {
            StartInForeground();
        }
        catch (Exception ex)
        {
            // e.g. a location-typed foreground service refused on Android 14+ when the permission
            // isn't actually held. Nothing to monitor without it, so stop cleanly rather than crash.
            EmergencyLog.Error("Foreground start failed", ex);
            StopSelf();
            return;
        }

        AcquireWakeLock();

        if (_accelerometer != null)
        {
            _sensorManager?.RegisterListener(this, _accelerometer, SensorDelay.Game);
        }
        else
        {
            EmergencyLog.Warn("No accelerometer on this device - shake/crash detection unavailable.");
        }

        TryRequestLocationUpdates();

        _monitoring = true;
        EmergencyLog.Info("Monitoring started.");
    }

    private void StartInForeground()
    {
        var builder = new NotificationCompat.Builder(this, MonitorChannelId);
        builder.SetContentTitle("Safety monitor active");
        builder.SetContentText("Watching for emergencies during your shift.");
        builder.SetSmallIcon(global::Android.Resource.Drawable.IcMenuMyCalendar);
        builder.SetOngoing(true);
        builder.SetPriority(NotificationCompat.PriorityLow);
        Notification notification = builder.Build()!;

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            StartForeground(MonitorNotificationId, notification, ForegroundService.TypeLocation);
        }
        else
        {
            StartForeground(MonitorNotificationId, notification);
        }
    }

    private void TryRequestLocationUpdates()
    {
        if (_locationManager == null) return;

        try
        {
            if (_locationManager.IsProviderEnabled(LocationManager.GpsProvider))
            {
                _locationManager.RequestLocationUpdates(LocationManager.GpsProvider, 1000, 0, this);
            }
            else
            {
                EmergencyLog.Warn("GPS provider is off - crash confirmation and alert coordinates will be limited.");
            }
        }
        catch (Exception ex)
        {
            // Location permission was revoked after the shift started - detection still runs on
            // the accelerometer; the crash protocol just can't confirm the stop.
            EmergencyLog.Error("GPS updates unavailable", ex);
        }
    }

    private void AcquireWakeLock()
    {
        if (_wakeLock != null) return;

        var power = (PowerManager?)GetSystemService(PowerService);
        _wakeLock = power?.NewWakeLock(WakeLockFlags.Partial, "LARGA:EmergencyMonitor");
        _wakeLock?.Acquire();
    }

    // ---- Sensors ------------------------------------------------------------

    public void OnSensorChanged(SensorEvent? e)
    {
        if (e?.Values == null || e.Values.Count < 3) return;

        float rawX = e.Values[0];
        float rawY = e.Values[1];
        float rawZ = e.Values[2];

        // Isolate the gravity component with a low-pass filter, then subtract it so the detectors
        // see only movement, not the constant 1G pull.
        if (!_gravityPrimed)
        {
            _gravity[0] = rawX;
            _gravity[1] = rawY;
            _gravity[2] = rawZ;
            _gravityPrimed = true;
        }
        else
        {
            _gravity[0] = GravityAlpha * _gravity[0] + (1 - GravityAlpha) * rawX;
            _gravity[1] = GravityAlpha * _gravity[1] + (1 - GravityAlpha) * rawY;
            _gravity[2] = GravityAlpha * _gravity[2] + (1 - GravityAlpha) * rawZ;
        }

        double linX = rawX - _gravity[0];
        double linY = rawY - _gravity[1];
        double linZ = rawZ - _gravity[2];

        double t = SystemClock.ElapsedRealtime() / 1000.0;

        _crash.OnAcceleration(t, linZ);

        if (_hostile.OnSample(t, linX, linY, linZ))
        {
            EmergencyLog.Info("Hostile shake pattern detected.");
            BeginCountdown(EmergencyAlert.Hostile);
        }
    }

    public void OnAccuracyChanged(Sensor? sensor, [GeneratedEnum] SensorStatus accuracy)
    {
        // Not needed - the detectors tolerate the accuracy the hardware gives.
    }

    // ---- Location -----------------------------------------------------------

    public void OnLocationChanged(Location location)
    {
        if (!_hasFix)
        {
            EmergencyLog.Info("First GPS fix received.");
        }

        _lastLat = location.Latitude;
        _lastLng = location.Longitude;
        _hasFix = true;

        double t = SystemClock.ElapsedRealtime() / 1000.0;
        double? speed = location.HasSpeed ? location.Speed : (double?)null;

        if (_crash.OnSpeed(t, speed))
        {
            EmergencyLog.Info("Crash pattern detected (G-force spike, then stopped).");
            BeginCountdown(EmergencyAlert.Crash);
        }
    }

    public void OnProviderDisabled(string provider) { }
    public void OnProviderEnabled(string provider) { }
    public void OnStatusChanged(string? provider, [GeneratedEnum] Availability status, Bundle? extras) { }

    // ---- Countdown (both protocols) ----------------------------------------

    private void BeginCountdown(string triggerType)
    {
        CancellationToken token;
        lock (_countdownGate)
        {
            // One emergency at a time: a second detection during a countdown is ignored.
            if (_countdownCts != null) return;
            _countdownCts = new CancellationTokenSource();
            token = _countdownCts.Token;
        }

        EmergencyLog.Info($"{triggerType} countdown started ({CountdownLength.TotalSeconds:0}s to cancel).");

        Vibrate(triggerType);
        ShowCountdownNotification(triggerType);
        _coordinator?.Begin(triggerType, CountdownLength);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(CountdownLength, token);
            }
            catch (TaskCanceledException)
            {
                return; // the driver cancelled - no alert
            }

            if (!EndCountdown())
            {
                return; // cancelled at the last moment
            }

            EmergencyLog.Info($"{triggerType} countdown expired - sending alert.");
            if (triggerType == EmergencyAlert.Crash)
            {
                await FireCrashAsync();
            }
            else
            {
                await FireHostileAsync();
            }
        });
    }

    private void OnPopupCancelRequested(object? sender, EventArgs e) => CancelCountdown("popup");

    private void CancelCountdown(string source)
    {
        CancellationTokenSource? cts;
        lock (_countdownGate)
        {
            cts = _countdownCts;
            _countdownCts = null;
        }

        if (cts == null) return;

        cts.Cancel();
        EmergencyLog.Info($"Countdown cancelled ({source}) - no alert sent.");
        ClearCountdownUi();
    }

    /// <summary>Ends a countdown that ran out. False if it was cancelled in the meantime.</summary>
    private bool EndCountdown()
    {
        lock (_countdownGate)
        {
            if (_countdownCts == null) return false;
            _countdownCts = null;
        }

        ClearCountdownUi();
        return true;
    }

    private void ClearCountdownUi()
    {
        CancelVibration();
        Notifications.Cancel(CountdownNotificationId);
        _coordinator?.End();
    }

    // ---- Protocols ----------------------------------------------------------

    private async Task FireHostileAsync()
    {
        // Silent payload - no driver-facing alert, so a hostile passenger sees nothing more.
        string? id = await SendAlertAsync(EmergencyAlert.Hostile);
        if (id != null && _callHandler != null)
        {
            await _callHandler.ArmAsync(EmergencyCallMode.Stealth);
        }
    }

    private async Task FireCrashAsync()
    {
        string? id = await SendAlertAsync(EmergencyAlert.Crash);
        ShowCrashNotification(sent: id != null);
        if (id != null && _callHandler != null)
        {
            await _callHandler.ArmAsync(EmergencyCallMode.Loud);
        }
    }

    private async Task<string?> SendAlertAsync(string triggerType)
    {
        if (_alertService == null) return null;

        try
        {
            // Pass the latest GPS fix so no foreground location request is needed.
            double? lat = _hasFix ? _lastLat : (double?)null;
            double? lng = _hasFix ? _lastLng : (double?)null;
            string? id = await _alertService.SendAlertAsync(triggerType, lat, lng);

            if (id == null)
            {
                EmergencyLog.Warn($"{triggerType} alert NOT written (no active shift).");
            }
            else
            {
                EmergencyLog.Info($"{triggerType} alert written: emergency_alerts/{id}");
            }
            return id;
        }
        catch (Exception ex)
        {
            EmergencyLog.Error($"{triggerType} alert failed", ex);
            return null;
        }
    }

    // ---- Notifications & haptics -------------------------------------------

    private void ShowCountdownNotification(string triggerType)
    {
        bool crash = triggerType == EmergencyAlert.Crash;

        var cancelIntent = new Intent(this, typeof(EmergencyDetectionService)).SetAction(ActionCancelCountdown);
        PendingIntent? cancelPending = PendingIntent.GetService(
            this, 0, cancelIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        // Opens (or raises) the app on the countdown popup - over the lock screen too.
        var showIntent = new Intent(this, typeof(MainActivity))
            .AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ReorderToFront)
            .PutExtra(ExtraShowCountdown, true);
        PendingIntent? showPending = PendingIntent.GetActivity(
            this, 1, showIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new NotificationCompat.Builder(this, CountdownChannelId);
        builder.SetContentTitle(crash ? "Accident detected" : "Sending silent SOS");
        builder.SetContentText(crash
            ? "Alerting your manager in 5 seconds. Tap Cancel if you're OK."
            : "A hostile-situation alert will be sent in 5 seconds. Tap Cancel to stop it.");
        builder.SetSmallIcon(global::Android.Resource.Drawable.IcDialogAlert);
        builder.SetPriority(NotificationCompat.PriorityMax);
        builder.SetCategory(NotificationCompat.CategoryAlarm);
        builder.SetOngoing(true);
        builder.SetContentIntent(showPending);
        builder.SetFullScreenIntent(showPending, true);
        builder.AddAction(global::Android.Resource.Drawable.IcMenuCloseClearCancel, "Cancel SOS", cancelPending);
        Notification notification = builder.Build()!;

        Notifications.Notify(CountdownNotificationId, notification);
    }

    private void ShowCrashNotification(bool sent)
    {
        var builder = new NotificationCompat.Builder(this, CrashAlertChannelId);
        builder.SetContentTitle("Accident alert");
        builder.SetContentText(sent
            ? "A crash alert has been sent to your manager."
            : "A crash was detected but the alert couldn't be sent - call your manager.");
        builder.SetSmallIcon(global::Android.Resource.Drawable.IcDialogAlert);
        builder.SetPriority(NotificationCompat.PriorityMax);
        builder.SetCategory(NotificationCompat.CategoryAlarm);
        Notification notification = builder.Build()!;

        Notifications.Notify(AlertNotificationId, notification);
    }

    private void Vibrate(string triggerType)
    {
        try
        {
            Vibrator? vibrator = GetVibrator();
            if (vibrator == null || !vibrator.HasVibrator) return;

            // Both patterns last the length of the countdown. Crash is longer, full-strength pulses.
            bool crash = triggerType == EmergencyAlert.Crash;
            long[] pattern = crash
                ? new long[] { 0, 800, 200, 800, 200, 800, 200, 800, 200, 800 }
                : new long[] { 0, 400, 600, 400, 600, 400, 600, 400, 600, 400 };

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                int strength = crash ? 255 : VibrationEffect.DefaultAmplitude;
                int[] amplitudes = new int[pattern.Length];
                for (int i = 0; i < pattern.Length; i++)
                {
                    amplitudes[i] = i % 2 == 1 ? strength : 0;
                }
                vibrator.Vibrate(VibrationEffect.CreateWaveform(pattern, amplitudes, -1));
            }
            else
            {
#pragma warning disable CS0618, CA1422
                vibrator.Vibrate(pattern, -1);
#pragma warning restore CS0618, CA1422
            }
        }
        catch (Exception ex)
        {
            EmergencyLog.Error("Vibration failed", ex);
        }
    }

    private void CancelVibration()
    {
        try { GetVibrator()?.Cancel(); }
        catch (Exception ex) { EmergencyLog.Error("Vibration cancel failed", ex); }
    }

    private Vibrator? GetVibrator()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            var manager = (VibratorManager?)GetSystemService(VibratorManagerService);
            return manager?.DefaultVibrator;
        }

#pragma warning disable CS0618, CA1422
        return (Vibrator?)GetSystemService(VibratorService);
#pragma warning restore CS0618, CA1422
    }

    private void CreateChannels()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager == null) return;

        manager.DeleteNotificationChannel(LegacyAlertChannelId);
        manager.DeleteNotificationChannel(LegacyCrashAlarmChannelId);

        var monitor = new NotificationChannel(MonitorChannelId, "Safety Monitor", NotificationImportance.Low)
        {
            Description = "Shows while automatic emergency detection is running.",
        };

        var countdown = new NotificationChannel(CountdownChannelId, "Emergency Countdown", NotificationImportance.High)
        {
            Description = "The 5-second window to cancel an automatic SOS.",
        };
        countdown.SetSound(null, null);
        countdown.EnableVibration(false);
        countdown.LockscreenVisibility = NotificationVisibility.Public;

        var alert = new NotificationChannel(CrashAlertChannelId, "Accident Alert", NotificationImportance.High)
        {
            Description = "Shown after an accident alert is sent.",
        };
        alert.SetSound(null, null);
        alert.EnableVibration(false); // vibration and the alarm come from AndroidEmergencyFeedback
        alert.LockscreenVisibility = NotificationVisibility.Public;

        manager.CreateNotificationChannel(monitor);
        manager.CreateNotificationChannel(countdown);
        manager.CreateNotificationChannel(alert);
    }

    // ---- Teardown -----------------------------------------------------------

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

    public override void OnDestroy()
    {
        CancelCountdown("service stopped");

        if (_coordinator != null)
        {
            _coordinator.CancelRequested -= OnPopupCancelRequested;
        }

        try { _sensorManager?.UnregisterListener(this); } catch { /* never registered */ }
        try { _locationManager?.RemoveUpdates(this); } catch { /* never requested */ }

        _callHandler?.Disarm();

        if (_wakeLock?.IsHeld == true)
        {
            _wakeLock.Release();
        }
        _wakeLock = null;

        _monitoring = false;
        EmergencyLog.Info("Monitoring stopped.");

        base.OnDestroy();
    }
}

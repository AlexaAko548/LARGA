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
using LARGA.SharedCore.Emergency;
using Location = Android.Locations.Location;

namespace LARGA.MobileApp.Platforms.Android.Emergency;

/// <summary>
/// LAR-86/87 background emergency detection. Runs as a foreground service for the length of a
/// shift so the accelerometer and GPS keep delivering with the app minimized or the screen off.
/// A detected Crash starts a 5-second cancel countdown (popup + notification + haptics). A Hostile
/// trigger (shake, or the volume-button pattern from <see cref="VolumeSosAccessibilityService"/>)
/// starts a 10-second one that stays hidden: two short taps and a disguised notification, no
/// popup. If it isn't cancelled the alert is written to Firestore and
/// <see cref="EmergencyCallHandler"/> is armed to auto-answer the manager's callback. The duress
/// PIN skips the countdown (<see cref="TrySendDuressAlertAsync"/>).
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
    // Sensor replay harness (SensorTrace format). The trace is read from the app's external files
    // folder: adb push trace.txt /sdcard/Android/data/com.blmtaxi.larga.mobile/files/
    // then  ... -a larga.emergency.REPLAY --es trace trace.txt
    private const string ActionReplay = "larga.emergency.REPLAY";
    // Records the live accelerometer and GPS speed to files/<name> until RECORD_STOP:
    // ... -a larga.emergency.RECORD --es trace shake.txt
    private const string ActionRecord = "larga.emergency.RECORD";
    private const string ActionRecordStop = "larga.emergency.RECORD_STOP";
    // Moves the auto-answer window back by --ei minutes N (e.g. 31 for "manager calls 31 minutes later").
    private const string ActionAgeArmed = "larga.emergency.AGE_ARMED";
    private const string ExtraTrace = "trace";
    private const string ExtraMinutes = "minutes";
#endif

    private const string MonitorChannelId = "emergency_monitor_channel";
    // Silent on purpose: a Hostile countdown must not chime in front of the passenger. Haptics
    // are driven by the service itself.
    private const string CountdownChannelId = "emergency_countdown_channel";
    // Hostile countdown: low importance, so it never pops up as a banner over whatever is on screen.
    // It only sits in the notification shade, worded like routine app activity.
    private const string QuietCountdownChannelId = "emergency_quiet_channel";
    // Silent: the crash alarm is played by AndroidEmergencyFeedback, so the channel doesn't add a second one.
    private const string CrashAlertChannelId = "emergency_crash_alert_channel";
    // Created by earlier builds with a sound (channel sounds can't be changed afterwards), so they're
    // removed in favour of the channels above.
    private const string LegacyAlertChannelId = "emergency_alert_channel";
    private const string LegacyCrashAlarmChannelId = "emergency_crash_alarm_channel";

    private const int MonitorNotificationId = 8601;
    private const int CountdownNotificationId = 8602;
    private const int AlertNotificationId = 8603;

    private static readonly TimeSpan CrashCountdownLength = TimeSpan.FromSeconds(5);
    // Longer: the hidden Hostile countdown takes a little more effort to cancel (pull down the
    // shade, or repeat the volume pattern) than the Crash popup's big button.
    private static readonly TimeSpan HostileCountdownLength = TimeSpan.FromSeconds(10);

    // The service while it's monitoring a shift, for the in-process triggers (the accessibility
    // service's volume pattern, the duress PIN). Null when no shift is being monitored.
    private static EmergencyDetectionService? s_monitoring;

    private SensorManager? _sensorManager;
    private Sensor? _accelerometer;
    private LocationManager? _locationManager;
    private PowerManager.WakeLock? _wakeLock;
    private bool _monitoring;

    private readonly GravityFilter _gravity = new();
    private readonly HostileMotionDetector _hostile = new();
    private readonly CrashDetector _crash = new();
    private EmergencyCallHandler? _callHandler;
    private IEmergencyAlertService? _alertService;
    private IEmergencyFeedback? _feedback;
    private EmergencyCountdownCoordinator? _coordinator;

    private double _lastLat;
    private double _lastLng;
    private bool _hasFix;

    private readonly EmergencyCountdownGate _countdownGate = new();
#if DEBUG
    private System.IO.StreamWriter? _recorder;
    private double _recordStart;
    private string? _lastLoggedSpeed;
#endif

    public override IBinder? OnBind(Intent? intent) => null;

    private NotificationManagerCompat Notifications => NotificationManagerCompat.From(this)!;

    public override void OnCreate()
    {
        base.OnCreate();

        IServiceProvider? services = IPlatformApplication.Current?.Services;
        _alertService = services?.GetService<IEmergencyAlertService>();
        _coordinator = services?.GetService<EmergencyCountdownCoordinator>();
        _feedback = services?.GetService<IEmergencyFeedback>();

        if (_alertService != null)
        {
            _callHandler = new EmergencyCallHandler(this, _alertService);
            _callHandler.CallConnected += OnCallConnected;
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

            case ActionReplay:
            case ActionRecord:
            case ActionRecordStop:
            case ActionAgeArmed:
                if (!_monitoring)
                {
                    EmergencyLog.Warn($"{intent!.Action} ignored: the monitor isn't running (clock in first).");
                    StopSelf();
                    return StartCommandResult.NotSticky;
                }
                HandleDebugAction(intent!);
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
        s_monitoring = this;
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
            // GPS is the one that reports speed, which crash confirmation needs. The fused provider
            // (Android 12+) blends GPS with Wi-Fi/cell and keeps fixes coming indoors and in
            // tunnels; with GPS off, the network provider still gives coordinates for the alert.
            var providers = new List<string>();
            if (_locationManager.IsProviderEnabled(LocationManager.GpsProvider))
            {
                providers.Add(LocationManager.GpsProvider);
            }
            if (OperatingSystem.IsAndroidVersionAtLeast(31) && _locationManager.IsProviderEnabled(LocationManager.FusedProvider))
            {
                providers.Add(LocationManager.FusedProvider);
            }
            if (providers.Count == 0 && _locationManager.IsProviderEnabled(LocationManager.NetworkProvider))
            {
                providers.Add(LocationManager.NetworkProvider);
            }

            foreach (string provider in providers)
            {
                _locationManager.RequestLocationUpdates(provider, 1000, 0, this);
            }

            if (providers.Count == 0)
            {
                EmergencyLog.Warn("No location provider is on - crash confirmation and alert coordinates are unavailable.");
            }
            else
            {
                EmergencyLog.Info($"Location providers: {string.Join(", ", providers)}.");
                if (!providers.Contains(LocationManager.GpsProvider))
                {
                    EmergencyLog.Warn("GPS provider is off - crash confirmation needs speed and may not work.");
                }
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
        double t = SystemClock.ElapsedRealtime() / 1000.0;
#if DEBUG
        Record(SensorTrace.FormatAccel(t - _recordStart, rawX, rawY, rawZ));
#endif

        // The detectors see only movement, not the constant 1G pull.
        (double linX, double linY, double linZ) = _gravity.Linear(rawX, rawY, rawZ);

        _crash.OnLinearAcceleration(t, linX, linY, linZ);

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

    // ---- In-process Hostile triggers ----------------------------------------

    /// <summary>
    /// The volume-button pattern was pressed (<see cref="VolumeSosAccessibilityService"/>). Starts
    /// the hidden Hostile countdown, or cancels it when one is already running. False when no
    /// shift is being monitored.
    /// </summary>
    internal static bool HandleVolumePattern()
    {
        EmergencyDetectionService? service = s_monitoring;
        if (service == null) return false;

        // Repeating the pattern is the eyes-free cancel: the driver never has to look at the phone.
        if (service._countdownGate.Active == EmergencyAlert.Hostile && !service._countdownGate.Sending)
        {
            service.CancelCountdown("volume pattern repeated");
            service.VibratePattern(CancelledPattern, VibrationEffect.DefaultAmplitude);
            return true;
        }

        EmergencyLog.Info("Hostile volume-button pattern detected.");
        service.BeginCountdown(EmergencyAlert.Hostile);
        return true;
    }

    /// <summary>
    /// The duress PIN was entered at clock-out: sends the Hostile alert straight away (typing it
    /// is always deliberate, so there's no countdown) and arms the stealth callback. A countdown
    /// already running is dropped in its favour. False when no shift is being monitored.
    /// </summary>
    internal static async Task<bool> TrySendDuressAlertAsync()
    {
        EmergencyDetectionService? service = s_monitoring;
        if (service == null) return false;

        EmergencyLog.Info("Duress PIN entered - sending a Hostile alert now (no countdown).");
        service.CancelCountdown("duress PIN");
        await service.FireHostileAsync();
        return true;
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
#if DEBUG
        Record(SensorTrace.FormatGps(t - _recordStart, speed) + "," + location.Provider);
        // Only when the speed changes, so logcat shows moving / stopped without one line per second.
        string speedText = speed is double v ? $"{v * 3.6:0} km/h" : "none";
        if (speedText != _lastLoggedSpeed)
        {
            _lastLoggedSpeed = speedText;
            EmergencyLog.Info($"GPS fix: speed {speedText}.");
        }
#endif

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
        // One emergency at a time: a second detection during a countdown, or while its alert is
        // still being written, is ignored.
        if (!_countdownGate.TryBegin(triggerType, out CancellationToken token))
        {
            EmergencyLog.Info(_countdownGate.Sending
                ? $"{triggerType} detection ignored: a {_countdownGate.Active} alert is still being written."
                : $"{triggerType} detection ignored: a {_countdownGate.Active} countdown is already running.");
            return;
        }

        TimeSpan length = triggerType == EmergencyAlert.Crash ? CrashCountdownLength : HostileCountdownLength;
        EmergencyLog.Info($"{triggerType} countdown started ({length.TotalSeconds:0}s to cancel).");

        Vibrate(triggerType);
        if (triggerType == EmergencyAlert.Crash)
        {
            // SOS-018: the crash countdown is loud (alarm stream, audible on silent), so a driver
            // who is conscious notices it and can cancel. Hostile stays silent.
            _feedback?.CrashCountdownStarted();
        }
        ShowCountdownNotification(triggerType, length);
        _coordinator?.Begin(triggerType, length);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(length, token);
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
            try
            {
                if (triggerType == EmergencyAlert.Crash)
                {
                    await FireCrashAsync();
                }
                else
                {
                    await FireHostileAsync();
                }
            }
            finally
            {
                _countdownGate.Finish();
            }
        });
    }

    private void OnPopupCancelRequested(object? sender, EventArgs e) => CancelCountdown("popup");

    // SOS-024: the auto-answered crash callback is on the speakerphone; the alarm would drown it.
    private void OnCallConnected(object? sender, EventArgs e) => _feedback?.StopAlarm();

    private void CancelCountdown(string source)
    {
        if (!_countdownGate.Cancel()) return;

        EmergencyLog.Info($"Countdown cancelled ({source}) - no alert sent.");
        _feedback?.StopAlarm();
        ClearCountdownUi();
    }

    /// <summary>Ends a countdown that ran out. False if it was cancelled in the meantime.</summary>
    private bool EndCountdown()
    {
        if (!_countdownGate.TryComplete()) return false;

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
            else if (_alertService.LastSendQueued)
            {
                EmergencyLog.Warn($"{triggerType} alert queued offline (no signal): emergency_alerts/{id} - it syncs when the phone reconnects.");
            }
            else
            {
                EmergencyLog.Info($"{triggerType} alert written: emergency_alerts/{id} (locationSource {_alertService.LastLocationSource}).");
            }
            return id;
        }
        catch (Exception ex)
        {
            EmergencyLog.Error($"{triggerType} alert failed", ex);
            return null;
        }
    }

#if DEBUG
    // ---- DEBUG test hooks (alpha testing on the emulator) -------------------

    private void HandleDebugAction(Intent intent)
    {
        string name = intent.GetStringExtra(ExtraTrace) ?? "trace.txt";
        string folder = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
        string path = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(name));

        try
        {
            switch (intent.Action)
            {
                case ActionReplay:
                    // Fresh detectors on the trace's own clock, so the live sensor stream (whose
                    // samples keep arriving meanwhile) can't mix into the replay's windows.
                    SensorTrace trace = SensorTrace.Parse(System.IO.File.ReadAllText(path));
                    List<EmergencyTraceReplayer.Detection> detections = EmergencyTraceReplayer.Run(trace);
                    EmergencyLog.Info($"Replay {name}: {trace.Samples.Count} samples, "
                        + (detections.Count == 0 ? "no detection." : string.Join(", ", detections.Select(d => $"{d.TriggerType} at {d.T:0.00}s"))));
                    if (detections.Count > 0)
                    {
                        EmergencyLog.Info(detections[0].TriggerType == EmergencyAlert.Crash
                            ? "Crash pattern detected (G-force spike, then stopped)."
                            : "Hostile shake pattern detected.");
                        BeginCountdown(detections[0].TriggerType);
                    }
                    break;

                case ActionRecord:
                    StopRecording();
                    _recordStart = SystemClock.ElapsedRealtime() / 1000.0;
                    var recorder = new System.IO.StreamWriter(path, append: false);
                    recorder.WriteLine($"# LARGA sensor trace recorded {DateTime.UtcNow:O} on {Build.Model} (API {(int)Build.VERSION.SdkInt})");
                    _recorder = recorder;
                    EmergencyLog.Info($"Recording sensors to {path}.");
                    break;

                case ActionRecordStop:
                    StopRecording();
                    break;

                case ActionAgeArmed:
                    _callHandler?.AgeArmedWindow(TimeSpan.FromMinutes(intent.GetIntExtra(ExtraMinutes, 31)));
                    break;
            }
        }
        catch (Exception ex)
        {
            EmergencyLog.Error($"{intent.Action} failed", ex);
        }
    }

    private void Record(string line)
    {
        System.IO.StreamWriter? recorder = _recorder;
        if (recorder == null) return;
        lock (recorder) recorder.WriteLine(line);
    }

    private void StopRecording()
    {
        System.IO.StreamWriter? recorder = _recorder;
        _recorder = null;
        if (recorder == null) return;
        lock (recorder) recorder.Dispose();
        EmergencyLog.Info("Sensor recording stopped.");
    }
#endif

    // ---- Notifications & haptics -------------------------------------------

    private void ShowCountdownNotification(string triggerType, TimeSpan length)
    {
        bool crash = triggerType == EmergencyAlert.Crash;

        var cancelIntent = new Intent(this, typeof(EmergencyDetectionService)).SetAction(ActionCancelCountdown);
        PendingIntent? cancelPending = PendingIntent.GetService(
            this, 0, cancelIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        if (!crash)
        {
            Notifications.Notify(CountdownNotificationId, BuildHiddenHostileNotification(cancelPending, length));
            return;
        }

        // Opens (or raises) the app on the countdown popup - over the lock screen too.
        var showIntent = new Intent(this, typeof(MainActivity))
            .AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ReorderToFront)
            .PutExtra(ExtraShowCountdown, true);
        PendingIntent? showPending = PendingIntent.GetActivity(
            this, 1, showIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new NotificationCompat.Builder(this, CountdownChannelId);
        builder.SetContentTitle("Accident detected");
        builder.SetContentText($"Alerting your manager in {length.TotalSeconds:0} seconds. Tap Cancel if you're OK.");
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

    /// <summary>
    /// The Hostile countdown, dressed as routine app activity: same icon and title as the safety
    /// monitor, no banner, no full-screen popup, nothing that says SOS. "Dismiss" cancels it. The
    /// driver knows what it is from the two short taps that started it.
    /// </summary>
    private Notification BuildHiddenHostileNotification(PendingIntent? cancelPending, TimeSpan length)
    {
        var builder = new NotificationCompat.Builder(this, QuietCountdownChannelId);
        builder.SetContentTitle("Safety monitor active");
        builder.SetContentText("Syncing shift data…");
        builder.SetSmallIcon(global::Android.Resource.Drawable.IcMenuMyCalendar);
        builder.SetPriority(NotificationCompat.PriorityLow);
        builder.SetOngoing(true);
        builder.SetSilent(true);
        // Clears itself if the service somehow misses the end of the countdown.
        builder.SetTimeoutAfter((long)length.TotalMilliseconds + 2000);
        builder.AddAction(global::Android.Resource.Drawable.IcMenuCloseClearCancel, "Dismiss", cancelPending);
        return builder.Build()!;
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

    // Crash: full-strength pulses for the length of its countdown.
    private static readonly long[] CrashCountdownPattern = { 0, 800, 200, 800, 200, 800, 200, 800, 200, 800 };
    // Hostile: two short taps, felt by the driver but too brief to be heard or noticed by anyone else.
    private static readonly long[] HostileCountdownPattern = { 0, 60, 140, 60 };
    // One short tap: the hidden Hostile countdown was cancelled by repeating the volume pattern.
    private static readonly long[] CancelledPattern = { 0, 60 };

    private void Vibrate(string triggerType)
    {
        bool crash = triggerType == EmergencyAlert.Crash;
        VibratePattern(crash ? CrashCountdownPattern : HostileCountdownPattern,
            crash ? 255 : VibrationEffect.DefaultAmplitude);
    }

    private void VibratePattern(long[] pattern, int strength)
    {
        try
        {
            Vibrator? vibrator = GetVibrator();
            if (vibrator == null || !vibrator.HasVibrator) return;

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
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

        // Named like routine activity, since the driver's notification settings list it.
        var quietCountdown = new NotificationChannel(QuietCountdownChannelId, "Shift Sync", NotificationImportance.Low)
        {
            Description = "Background shift activity.",
        };
        quietCountdown.SetSound(null, null);
        quietCountdown.EnableVibration(false);
        quietCountdown.LockscreenVisibility = NotificationVisibility.Public;

        var alert = new NotificationChannel(CrashAlertChannelId, "Accident Alert", NotificationImportance.High)
        {
            Description = "Shown after an accident alert is sent.",
        };
        alert.SetSound(null, null);
        alert.EnableVibration(false); // vibration and the alarm come from AndroidEmergencyFeedback
        alert.LockscreenVisibility = NotificationVisibility.Public;

        manager.CreateNotificationChannel(monitor);
        manager.CreateNotificationChannel(countdown);
        manager.CreateNotificationChannel(quietCountdown);
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

        if (_callHandler != null)
        {
            _callHandler.CallConnected -= OnCallConnected;
            // Also refuses an arming still on its way from an alert being written (SOS-027).
            _callHandler.ShutDown();
        }
#if DEBUG
        StopRecording();
#endif

        if (_wakeLock?.IsHeld == true)
        {
            _wakeLock.Release();
        }
        _wakeLock = null;

        _monitoring = false;
        if (s_monitoring == this) s_monitoring = null;
        EmergencyLog.Info("Monitoring stopped.");

        base.OnDestroy();
    }
}

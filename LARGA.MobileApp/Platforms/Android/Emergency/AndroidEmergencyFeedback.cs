using System;
using Android.Content;
using Android.Media;
using Android.OS;
using LARGA.MobileApp.Services;
using LARGA.Shared.Models.Entities;
using AndroidApp = Android.App.Application;

namespace LARGA.MobileApp.Platforms.Android.Emergency;

/// <summary>
/// Android implementation of <see cref="IEmergencyFeedback"/>. The vibration tells the driver an
/// SOS went out; the crash alarm is played here rather than through a notification, so it runs
/// even when the notification is muted or the phone is in Do Not Disturb.
/// </summary>
public sealed class AndroidEmergencyFeedback : IEmergencyFeedback
{
    // Press-complete pulse: one firm buzz, short enough to release on.
    private static readonly long[] ButtonPattern = { 0, 200 };

    // Sent pulse: three buzzes, so it can't be mistaken for a stray vibration.
    private static readonly long[] SentPattern = { 0, 250, 150, 250, 150, 250 };

    private static readonly TimeSpan CrashAlarmLength = TimeSpan.FromSeconds(10);

    private readonly Handler _handler = new(Looper.MainLooper!);
    private MediaPlayer? _player;
    private int _alarmToken;

    public void ButtonHeld() => Vibrate(ButtonPattern);

    public void AlertSent(string triggerType)
    {
        Vibrate(SentPattern);

        if (triggerType == EmergencyAlert.Crash)
        {
            PlayCrashAlarm();
        }
    }

    private void PlayCrashAlarm()
    {
        try
        {
            StopAlarm();

            // Alarm usage plays on the alarm stream, so it's audible even when the ringer is silent.
            var player = new MediaPlayer();
            player.SetAudioAttributes(new AudioAttributes.Builder()!
                .SetUsage(AudioUsageKind.Alarm)!
                .SetContentType(AudioContentType.Sonification)!
                .Build());
            player.SetDataSource(AndroidApp.Context, RingtoneManager.GetDefaultUri(RingtoneType.Alarm)!);
            player.Looping = true;
            player.Prepare();
            player.Start();
            _player = player;

            // Each new alarm gets its own token, so a stop scheduled for an earlier alarm can't cut this one short.
            int token = ++_alarmToken;
            _handler.PostDelayed(() =>
            {
                if (token == _alarmToken) StopAlarm();
            }, (long)CrashAlarmLength.TotalMilliseconds);

            EmergencyLog.Info("Crash alarm playing.");
        }
        catch (Exception ex)
        {
            EmergencyLog.Error("Crash alarm failed", ex);
            StopAlarm();
        }
    }

    private void StopAlarm()
    {
        MediaPlayer? player = _player;
        _player = null;
        if (player == null) return;

        try
        {
            if (player.IsPlaying) player.Stop();
            player.Release();
        }
        catch (Exception ex)
        {
            EmergencyLog.Error("Crash alarm stop failed", ex);
        }
    }

    private static void Vibrate(long[] pattern)
    {
        try
        {
            Vibrator? vibrator = GetVibrator();
            if (vibrator == null || !vibrator.HasVibrator) return;

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                vibrator.Vibrate(VibrationEffect.CreateWaveform(pattern, -1));
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
            EmergencyLog.Error("Feedback vibration failed", ex);
        }
    }

    private static Vibrator? GetVibrator()
    {
        Context context = AndroidApp.Context;

        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            var manager = (VibratorManager?)context.GetSystemService(Context.VibratorManagerService);
            return manager?.DefaultVibrator;
        }

#pragma warning disable CS0618, CA1422
        return (Vibrator?)context.GetSystemService(Context.VibratorService);
#pragma warning restore CS0618, CA1422
    }
}

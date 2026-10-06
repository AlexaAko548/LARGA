using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Android;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Telecom;
using Android.Telephony;
using AndroidX.Core.Content;
using LARGA.MobileApp.Services;
using LARGA.SharedCore;
using Stream = Android.Media.Stream;

namespace LARGA.MobileApp.Platforms.Android.Emergency;

public enum EmergencyCallMode
{
    /// <summary>Hostile: answer on the earpiece with the volume at zero, so the manager can
    /// listen in without anyone in the cab hearing them.</summary>
    Stealth,

    /// <summary>Crash: answer on the speakerphone at full volume, so a hurt driver can talk
    /// without holding the phone.</summary>
    Loud,
}

/// <summary>
/// After an automated SOS, auto-answers the next incoming call from a manager number
/// (system_configs/global.managerPhoneNumbers) and forces the call audio for the protocol.
/// Listens to the PHONE_STATE broadcast, which carries the caller's number when READ_CALL_LOG
/// is granted (TelephonyCallback doesn't, on Android 12+).
/// </summary>
internal sealed class EmergencyCallHandler : BroadcastReceiver
{
    private static readonly TimeSpan ArmedFor = TimeSpan.FromMinutes(30);

    private readonly Context _context;
    private readonly IEmergencyAlertService _alertService;
    private readonly AudioManager _audio;
    private readonly Handler _mainHandler = new(Looper.MainLooper!);

    private EmergencyCallMode? _mode;
    private DateTime _armedUntilUtc;
    private IReadOnlyList<string> _managerNumbers = Array.Empty<string>();
    private bool _registered;
    private bool _answering;
    private bool _inHandledCall;
    private SavedAudio? _saved;

    public EmergencyCallHandler(Context context, IEmergencyAlertService alertService)
    {
        _context = context;
        _alertService = alertService;
        _audio = (AudioManager)context.GetSystemService(Context.AudioService)!;
    }

    public async Task ArmAsync(EmergencyCallMode mode)
    {
        if (!HasPermission(Manifest.Permission.ReadPhoneState) || !HasPermission(Manifest.Permission.ReadCallLog))
        {
            EmergencyLog.Warn("Auto-answer NOT armed: READ_PHONE_STATE / READ_CALL_LOG not granted.");
            return;
        }

        IReadOnlyList<string> numbers = await _alertService.GetManagerPhoneNumbersAsync();
        if (numbers.Count == 0)
        {
            EmergencyLog.Warn("Auto-answer NOT armed: system_configs/global.managerPhoneNumbers is empty or unreadable.");
            return;
        }

        _mainHandler.Post(() =>
        {
            // A Crash arriving while a Hostile call is armed (or vice versa) switches the mode;
            // the audio already saved for restoring stays the driver's original settings.
            _managerNumbers = numbers;
            _mode = mode;
            _armedUntilUtc = DateTime.UtcNow + ArmedFor;

            if (mode == EmergencyCallMode.Stealth)
            {
                SaveAudio();
                // The ring itself would give the phone away before it's answered.
                TrySetVolume(Stream.Ring, MinVolume(Stream.Ring));
            }

            Register();
            _mainHandler.PostDelayed(DisarmIfExpired, (long)ArmedFor.TotalMilliseconds + 1000);
            EmergencyLog.Info($"Auto-answer armed ({mode}) for {ArmedFor.TotalMinutes:0} min; {numbers.Count} manager number(s) allowed.");
        });
    }

    private void DisarmIfExpired()
    {
        if (_mode != null && !_answering && !_inHandledCall && DateTime.UtcNow > _armedUntilUtc)
        {
            Disarm();
        }
    }

    /// <summary>Stops waiting for a call and puts the driver's audio settings back.</summary>
    public void Disarm()
    {
        if (_mode != null || _inHandledCall)
        {
            EmergencyLog.Info("Auto-answer disarmed; audio settings restored.");
        }

        _mode = null;
        _answering = false;
        _inHandledCall = false;
        RestoreAudio();
        Unregister();
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action != TelephonyManager.ActionPhoneStateChanged) return;

        string? state = intent.GetStringExtra(TelephonyManager.ExtraState);

        if (state == TelephonyManager.ExtraStateRinging)
        {
            if (_mode is null || _inHandledCall) return;

            if (DateTime.UtcNow > _armedUntilUtc)
            {
                EmergencyLog.Info("Incoming call after the armed window expired - not answering.");
                Disarm();
                return;
            }

            // The broadcast arrives twice per call; only the copy with the number is useful.
#pragma warning disable CS0618, CA1422 // still the only way to get the incoming number without being the dialer
            string? number = intent.GetStringExtra(TelephonyManager.ExtraIncomingNumber);
#pragma warning restore CS0618, CA1422
            if (string.IsNullOrWhiteSpace(number) || _answering) return;

            if (_managerNumbers.Any(m => InputValidator.SamePhone(m, number)))
            {
                EmergencyLog.Info($"Incoming call from manager {Mask(number)} - auto-answering.");
                AnswerCall();
            }
            else
            {
                EmergencyLog.Info($"Incoming call from {Mask(number)} is not a manager number - ignoring.");
            }
        }
        else if (state == TelephonyManager.ExtraStateOffhook)
        {
            if (!_answering) return;

            _answering = false;
            _inHandledCall = true;
            EmergencyLog.Info($"Call connected - applying {_mode} audio.");
            ApplyCallAudio();
            // Some dialers reset routing just after the call connects - apply it again.
            _mainHandler.PostDelayed(ApplyCallAudio, 700);
            _mainHandler.PostDelayed(ApplyCallAudio, 2000);
        }
        else if (state == TelephonyManager.ExtraStateIdle)
        {
            if (_inHandledCall)
            {
                // One call per emergency: the manager has been connected.
                Disarm();
            }
        }
    }

    private void AnswerCall()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26) || !HasPermission(Manifest.Permission.AnswerPhoneCalls))
        {
            EmergencyLog.Warn("Can't auto-answer: needs Android 8+ and ANSWER_PHONE_CALLS.");
            return;
        }

        SaveAudio();
        _answering = true;
        try
        {
            var telecom = (TelecomManager)_context.GetSystemService(Context.TelecomService)!;
#pragma warning disable CS0618, CA1422 // deprecated in API 29 in favour of InCallService (dialer apps only), still works
            telecom.AcceptRingingCall();
#pragma warning restore CS0618, CA1422
        }
        catch (Exception ex)
        {
            _answering = false;
            EmergencyLog.Error("Auto-answer failed", ex);
        }
    }

    private void ApplyCallAudio()
    {
        if (!_inHandledCall || _mode is not EmergencyCallMode mode) return;

        bool loud = mode == EmergencyCallMode.Loud;
        RouteToSpeaker(loud);

        if (loud)
        {
            TrySetVolume(Stream.VoiceCall, _audio.GetStreamMaxVolume(Stream.VoiceCall));
        }
        else
        {
            // Most devices won't go below 1 for a call, so mute the stream as well.
            TrySetVolume(Stream.VoiceCall, MinVolume(Stream.VoiceCall));
            try { _audio.AdjustStreamVolume(Stream.VoiceCall, Adjust.Mute, 0); }
            catch (Exception ex) { EmergencyLog.Error("Voice call mute refused", ex); }
        }
    }

    private void RouteToSpeaker(bool speaker)
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                if (speaker)
                {
                    AudioDeviceInfo? builtIn = _audio.AvailableCommunicationDevices
                        .FirstOrDefault(d => d.Type == AudioDeviceType.BuiltinSpeaker);
                    if (builtIn != null) _audio.SetCommunicationDevice(builtIn);
                }
                else
                {
                    _audio.ClearCommunicationDevice();
                }
            }

#pragma warning disable CS0618, CA1422 // still honoured for cellular calls on many devices; harmless where it isn't
            _audio.SpeakerphoneOn = speaker;
#pragma warning restore CS0618, CA1422
        }
        catch (Exception ex)
        {
            EmergencyLog.Error("Call audio routing failed", ex);
        }
    }

    private void SaveAudio()
    {
        if (_saved != null) return;

#pragma warning disable CS0618, CA1422
        _saved = new SavedAudio(
            _audio.GetStreamVolume(Stream.Ring),
            _audio.GetStreamVolume(Stream.VoiceCall),
            _audio.SpeakerphoneOn);
#pragma warning restore CS0618, CA1422
    }

    private void RestoreAudio()
    {
        if (_saved is not SavedAudio saved) return;
        _saved = null;

        try { _audio.AdjustStreamVolume(Stream.VoiceCall, Adjust.Unmute, 0); }
        catch (Exception) { /* not muted, or the device doesn't allow it - nothing to undo */ }

        TrySetVolume(Stream.VoiceCall, saved.VoiceCallVolume);
        TrySetVolume(Stream.Ring, saved.RingVolume);
        RouteToSpeaker(saved.SpeakerphoneOn);
    }

    // GetStreamMinVolume is API 28+; below that every stream's minimum is 0.
    private int MinVolume(Stream stream) =>
        OperatingSystem.IsAndroidVersionAtLeast(28) ? _audio.GetStreamMinVolume(stream) : 0;

    private void TrySetVolume(Stream stream, int volume)
    {
        try
        {
            _audio.SetStreamVolume(stream, volume, 0);
        }
        catch (Exception ex)
        {
            // e.g. lowering the ring volume to 0 while Do Not Disturb is on needs DND access.
            EmergencyLog.Error($"Couldn't set {stream} volume", ex);
        }
    }

    private void Register()
    {
        if (_registered) return;

        var filter = new IntentFilter(TelephonyManager.ActionPhoneStateChanged);
        // PHONE_STATE is a protected system broadcast, so it's still delivered to a non-exported receiver.
        ContextCompat.RegisterReceiver(_context, this, filter, ContextCompat.ReceiverNotExported);
        _registered = true;
    }

    private void Unregister()
    {
        if (!_registered) return;

        try { _context.UnregisterReceiver(this); }
        catch (Java.Lang.IllegalArgumentException) { /* already gone */ }
        _registered = false;
    }

    /// <summary>Last 4 digits only - logcat isn't the place for full phone numbers.</summary>
    private static string Mask(string number) =>
        number.Length <= 4 ? "****" : new string('*', number.Length - 4) + number[^4..];

    private bool HasPermission(string permission) =>
        ContextCompat.CheckSelfPermission(_context, permission) == Permission.Granted;

    private sealed record SavedAudio(int RingVolume, int VoiceCallVolume, bool SpeakerphoneOn);
}

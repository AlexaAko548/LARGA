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
using LARGA.SharedCore.Emergency;
using Stream = Android.Media.Stream;

namespace LARGA.MobileApp.Platforms.Android.Emergency;

/// <summary>
/// After an automated SOS, auto-answers the next incoming call from a manager number
/// (system_configs/global.managerPhoneNumbers) and forces the call audio for the protocol.
/// Listens to the PHONE_STATE broadcast, which carries the caller's number when READ_CALL_LOG
/// is granted (TelephonyCallback doesn't, on Android 12+). The rules (window, caller check,
/// audio save/restore) are <see cref="EmergencyCallAutoAnswer"/> in SharedCore; this class only
/// wires them to Android.
/// </summary>
internal sealed class EmergencyCallHandler : BroadcastReceiver
{
    private readonly Context _context;
    private readonly IEmergencyAlertService _alertService;
    private readonly Handler _mainHandler = new(Looper.MainLooper!);
    private readonly EmergencyCallAutoAnswer _state;

    private bool _registered;

    public EmergencyCallHandler(Context context, IEmergencyAlertService alertService)
    {
        _context = context;
        _alertService = alertService;
        _state = new EmergencyCallAutoAnswer(new AndroidCallAudio((AudioManager)context.GetSystemService(Context.AudioService)!));
    }

    public async Task ArmAsync(EmergencyCallMode mode)
    {
        bool phoneState = HasPermission(Manifest.Permission.ReadPhoneState);
        bool callLog = HasPermission(Manifest.Permission.ReadCallLog);
        if (EmergencyCallAutoAnswer.ArmBlockedReason(phoneState, callLog, managerNumberCount: null) is string permissionReason)
        {
            EmergencyLog.Warn($"Auto-answer NOT armed: {permissionReason}");
            return;
        }

        IReadOnlyList<string> numbers = await _alertService.GetManagerPhoneNumbersAsync();
        if (EmergencyCallAutoAnswer.ArmBlockedReason(phoneState, callLog, numbers.Count) is string reason)
        {
            EmergencyLog.Warn($"Auto-answer NOT armed: {reason}");
            return;
        }

        _mainHandler.Post(() =>
        {
            _state.Arm(mode, numbers, DateTime.UtcNow);
            Register();
            _mainHandler.PostDelayed(DisarmIfExpired, (long)EmergencyCallAutoAnswer.ArmedFor.TotalMilliseconds + 1000);
            EmergencyLog.Info($"Auto-answer armed ({mode}) for {EmergencyCallAutoAnswer.ArmedFor.TotalMinutes:0} min; {numbers.Count} manager number(s) allowed.");
        });
    }

    private void DisarmIfExpired()
    {
        if (_state.DisarmIfExpired(DateTime.UtcNow))
        {
            EmergencyLog.Info("Auto-answer disarmed; audio settings restored.");
            Unregister();
        }
    }

#if DEBUG
    /// <summary>DEBUG (AGE_ARMED): as if the SOS had been sent <paramref name="by"/> earlier.</summary>
    public void AgeArmedWindow(TimeSpan by) => _mainHandler.Post(() =>
    {
        if (_state.Mode is null)
        {
            EmergencyLog.Info("AGE_ARMED: auto-answer isn't armed.");
            return;
        }
        _state.AgeArmedWindow(by);
        EmergencyLog.Info($"AGE_ARMED: window moved back {by.TotalMinutes:0} min; armed until {_state.ArmedUntilUtc:HH:mm:ss}Z.");
        DisarmIfExpired();
    });
#endif

    /// <summary>Stops waiting for a call and puts the driver's audio settings back.</summary>
    public void Disarm()
    {
        if (_state.Disarm())
        {
            EmergencyLog.Info("Auto-answer disarmed; audio settings restored.");
        }
        Unregister();
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action != TelephonyManager.ActionPhoneStateChanged) return;

        string? state = intent.GetStringExtra(TelephonyManager.ExtraState);

        if (state == TelephonyManager.ExtraStateRinging)
        {
#pragma warning disable CS0618, CA1422 // still the only way to get the incoming number without being the dialer
            string? number = intent.GetStringExtra(TelephonyManager.ExtraIncomingNumber);
#pragma warning restore CS0618, CA1422

            switch (_state.OnRinging(number, DateTime.UtcNow))
            {
                case RingingDecision.Expired:
                    EmergencyLog.Info("Incoming call after the armed window expired - not answering.");
                    EmergencyLog.Info("Auto-answer disarmed; audio settings restored.");
                    Unregister();
                    break;
                case RingingDecision.Answer:
                    EmergencyLog.Info($"Incoming call from manager {EmergencyCallAutoAnswer.Mask(number!)} - auto-answering.");
                    AnswerCall();
                    break;
                case RingingDecision.NotManager:
                    EmergencyLog.Info($"Incoming call from {EmergencyCallAutoAnswer.Mask(number!)} is not a manager number - ignoring.");
                    break;
            }
        }
        else if (state == TelephonyManager.ExtraStateOffhook)
        {
            if (!_state.OnOffhook()) return;

            EmergencyLog.Info($"Call connected - applying {_state.Mode} audio.");
            ApplyCallAudio();
            // Some dialers reset routing just after the call connects - apply it again.
            _mainHandler.PostDelayed(ApplyCallAudio, 700);
            _mainHandler.PostDelayed(ApplyCallAudio, 2000);
        }
        else if (state == TelephonyManager.ExtraStateIdle)
        {
            // One call per emergency: the manager has been connected.
            if (_state.OnIdle())
            {
                EmergencyLog.Info("Auto-answer disarmed; audio settings restored.");
                Unregister();
            }
        }
    }

    private void ApplyCallAudio() => _state.ApplyCallAudio();

    private void AnswerCall()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26) || !HasPermission(Manifest.Permission.AnswerPhoneCalls))
        {
            EmergencyLog.Warn("Can't auto-answer: needs Android 8+ and ANSWER_PHONE_CALLS.");
            return;
        }

        _state.BeginAnswer();
        try
        {
            var telecom = (TelecomManager)_context.GetSystemService(Context.TelecomService)!;
#pragma warning disable CS0618, CA1422 // deprecated in API 29 in favour of InCallService (dialer apps only), still works
            telecom.AcceptRingingCall();
#pragma warning restore CS0618, CA1422
        }
        catch (Exception ex)
        {
            _state.AnswerFailed();
            EmergencyLog.Error("Auto-answer failed", ex);
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

    private bool HasPermission(string permission) =>
        ContextCompat.CheckSelfPermission(_context, permission) == Permission.Granted;

    /// <summary><see cref="ICallAudio"/> over AudioManager; every refusal is logged, never thrown.</summary>
    private sealed class AndroidCallAudio : ICallAudio
    {
        private readonly AudioManager _audio;

        public AndroidCallAudio(AudioManager audio) => _audio = audio;

        private static Stream ToStream(CallAudioStream stream) =>
            stream == CallAudioStream.Ring ? Stream.Ring : Stream.VoiceCall;

        public int GetVolume(CallAudioStream stream) => _audio.GetStreamVolume(ToStream(stream));

        public void SetVolume(CallAudioStream stream, int volume)
        {
            try
            {
                _audio.SetStreamVolume(ToStream(stream), volume, 0);
            }
            catch (Exception ex)
            {
                // e.g. lowering the ring volume to 0 while Do Not Disturb is on needs DND access.
                EmergencyLog.Error($"Couldn't set {ToStream(stream)} volume", ex);
            }
        }

        // GetStreamMinVolume is API 28+; below that every stream's minimum is 0.
        public int MinVolume(CallAudioStream stream) =>
            OperatingSystem.IsAndroidVersionAtLeast(28) ? _audio.GetStreamMinVolume(ToStream(stream)) : 0;

        public int MaxVolume(CallAudioStream stream) => _audio.GetStreamMaxVolume(ToStream(stream));

#pragma warning disable CS0618, CA1422
        public bool SpeakerphoneOn => _audio.SpeakerphoneOn;
#pragma warning restore CS0618, CA1422

        public void RouteToSpeaker(bool speaker)
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

        public void SetVoiceCallMuted(bool muted)
        {
            try
            {
                _audio.AdjustStreamVolume(Stream.VoiceCall, muted ? Adjust.Mute : Adjust.Unmute, 0);
            }
            catch (Exception ex)
            {
                // Unmuting: not muted, or the device doesn't allow it - nothing to undo.
                if (muted) EmergencyLog.Error("Voice call mute refused", ex);
            }
        }
    }
}

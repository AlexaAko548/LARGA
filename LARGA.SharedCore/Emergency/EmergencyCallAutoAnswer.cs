using System;
using System.Collections.Generic;
using System.Linq;

namespace LARGA.SharedCore.Emergency;

public enum EmergencyCallMode
{
    /// <summary>Hostile: answer on the earpiece with the volume at zero, so the manager can
    /// listen in without anyone in the cab hearing them.</summary>
    Stealth,

    /// <summary>Crash: answer on the speakerphone at full volume, so a hurt driver can talk
    /// without holding the phone.</summary>
    Loud,
}

public enum CallAudioStream
{
    Ring,
    VoiceCall,
}

/// <summary>The phone's audio controls the auto-answer changes and puts back (Android: AudioManager).</summary>
public interface ICallAudio
{
    int GetVolume(CallAudioStream stream);

    /// <summary>Never throws - a refusal (e.g. Do Not Disturb) is logged by the implementation.</summary>
    void SetVolume(CallAudioStream stream, int volume);

    int MinVolume(CallAudioStream stream);

    int MaxVolume(CallAudioStream stream);

    bool SpeakerphoneOn { get; }

    /// <summary>Never throws.</summary>
    void RouteToSpeaker(bool speaker);

    /// <summary>Never throws.</summary>
    void SetVoiceCallMuted(bool muted);
}

/// <summary>What to do with a ringing call while auto-answer is armed.</summary>
public enum RingingDecision
{
    /// <summary>Not armed, already in the handled call, already answering, or no number yet.</summary>
    Ignore,

    /// <summary>The armed window ran out: disarmed (audio restored), the call rings normally.</summary>
    Expired,

    /// <summary>A manager number: answer it.</summary>
    Answer,

    /// <summary>Some other number: let it ring.</summary>
    NotManager,
}

/// <summary>
/// The LAR-86/87 auto-answer rules, without Android: after an automated SOS is written, the next
/// call from a manager number (system_configs/global.managerPhoneNumbers) within
/// <see cref="ArmedFor"/> is answered, with Stealth (earpiece, muted, minimum volume) or Loud
/// (speakerphone, maximum volume) audio. The driver's own audio settings are saved before
/// anything is changed and put back when the window ends or the call hangs up.
/// EmergencyCallHandler (Android) feeds it the PHONE_STATE broadcasts and does the answering.
/// Not thread-safe: call it from one thread (the main looper).
/// </summary>
public sealed class EmergencyCallAutoAnswer
{
    public static readonly TimeSpan ArmedFor = TimeSpan.FromMinutes(30);

    private readonly ICallAudio _audio;

    private IReadOnlyList<string> _managerNumbers = Array.Empty<string>();
    private bool _answering;
    private bool _inHandledCall;
    private bool _shutDown;
    private SavedAudio? _saved;

    public EmergencyCallAutoAnswer(ICallAudio audio)
    {
        _audio = audio;
    }

    public EmergencyCallMode? Mode { get; private set; }

    public DateTime ArmedUntilUtc { get; private set; }

    public bool InHandledCall => _inHandledCall;

    public bool Answering => _answering;

    /// <summary>True while the driver's audio settings are saved (something was changed).</summary>
    public bool HasSavedAudio => _saved != null;

    /// <summary>Why auto-answer can't be armed, or null when it can. The alert itself is sent either way.
    /// <paramref name="managerNumberCount"/> null: the numbers haven't been looked up yet (permissions only).</summary>
    public static string? ArmBlockedReason(bool hasReadPhoneState, bool hasReadCallLog, bool hasAnswerPhoneCalls, int? managerNumberCount)
    {
        if (!hasReadPhoneState || !hasReadCallLog)
        {
            return "READ_PHONE_STATE / READ_CALL_LOG not granted.";
        }
        // Without it the call can't be picked up - arming would only silence the ringer, so the
        // driver would miss the manager's call altogether.
        if (!hasAnswerPhoneCalls)
        {
            return "ANSWER_PHONE_CALLS not granted.";
        }
        if (managerNumberCount == 0)
        {
            return "system_configs/global.managerPhoneNumbers is empty or unreadable.";
        }
        return null;
    }

    /// <summary>
    /// Arms (or re-arms) for <see cref="ArmedFor"/> from <paramref name="nowUtc"/>. A Crash
    /// arriving while a Hostile call is armed (or vice versa) switches the mode; the audio already
    /// saved for restoring stays the driver's original settings. False after <see cref="ShutDown"/>
    /// (the shift ended while the alert was being written).
    /// </summary>
    public bool Arm(EmergencyCallMode mode, IReadOnlyList<string> managerNumbers, DateTime nowUtc)
    {
        if (_shutDown) return false;

        _managerNumbers = managerNumbers;
        Mode = mode;
        ArmedUntilUtc = nowUtc + ArmedFor;

        if (mode == EmergencyCallMode.Stealth)
        {
            SaveAudio();
            // The ring itself would give the phone away before it's answered.
            _audio.SetVolume(CallAudioStream.Ring, _audio.MinVolume(CallAudioStream.Ring));
        }
        else if (_saved is SavedAudio saved)
        {
            // Switching from Stealth: the ring was lowered for the hostile window, but a crash
            // callback should be heard (SOS-025).
            _audio.SetVolume(CallAudioStream.Ring, saved.RingVolume);
        }
        return true;
    }

    /// <summary>Disarms for good: the detection service stopped (clock-out, logout). A later
    /// <see cref="Arm"/> is ignored. True when it was armed or in a call.</summary>
    public bool ShutDown()
    {
        _shutDown = true;
        return Disarm();
    }

    /// <summary>Test hook: moves the armed window back by <paramref name="by"/>, as if the SOS were that much older.</summary>
    public void AgeArmedWindow(TimeSpan by) => ArmedUntilUtc -= by;

    /// <summary>True when the window ran out with no call in progress (and it disarmed).</summary>
    public bool DisarmIfExpired(DateTime nowUtc)
    {
        if (Mode != null && !_answering && !_inHandledCall && nowUtc > ArmedUntilUtc)
        {
            Disarm();
            return true;
        }
        return false;
    }

    /// <summary>Stops waiting for a call and puts the driver's audio settings back. True when it was armed or in a call.</summary>
    public bool Disarm()
    {
        bool wasActive = Mode != null || _inHandledCall;

        Mode = null;
        _answering = false;
        _inHandledCall = false;
        RestoreAudio();
        return wasActive;
    }

    /// <summary>The phone is ringing. <paramref name="number"/> is null on the copy of the broadcast without it.</summary>
    public RingingDecision OnRinging(string? number, DateTime nowUtc)
    {
        if (Mode is null || _inHandledCall) return RingingDecision.Ignore;

        if (nowUtc > ArmedUntilUtc)
        {
            Disarm();
            return RingingDecision.Expired;
        }

        // The broadcast arrives twice per call; only the copy with the number is useful.
        if (string.IsNullOrWhiteSpace(number) || _answering) return RingingDecision.Ignore;

        return _managerNumbers.Any(m => InputValidator.SamePhone(m, number))
            ? RingingDecision.Answer
            : RingingDecision.NotManager;
    }

    /// <summary>Called just before the call is accepted.</summary>
    public void BeginAnswer()
    {
        SaveAudio();
        _answering = true;
    }

    /// <summary>Accepting the call threw.</summary>
    public void AnswerFailed() => _answering = false;

    /// <summary>The call went off-hook. True when it's the call being answered (apply the protocol's audio).</summary>
    public bool OnOffhook()
    {
        if (!_answering) return false;

        _answering = false;
        _inHandledCall = true;
        return true;
    }

    /// <summary>The line went idle. True when that ended the handled call (and disarmed: one call per emergency).</summary>
    public bool OnIdle()
    {
        if (_inHandledCall)
        {
            Disarm();
            return true;
        }

        // The call stopped ringing before it was picked up (the manager hung up first): keep
        // waiting for their next call. Left set, it blocked every later call and the window's
        // expiry, with the ringer kept down.
        _answering = false;
        return false;
    }

    /// <summary>Forces the call audio for the protocol. Re-applied a few times, as some dialers reset it.</summary>
    public void ApplyCallAudio()
    {
        if (!_inHandledCall || Mode is not EmergencyCallMode mode) return;

        bool loud = mode == EmergencyCallMode.Loud;
        _audio.RouteToSpeaker(loud);

        if (loud)
        {
            _audio.SetVolume(CallAudioStream.VoiceCall, _audio.MaxVolume(CallAudioStream.VoiceCall));
        }
        else
        {
            // Most devices won't go below 1 for a call, so mute the stream as well.
            _audio.SetVolume(CallAudioStream.VoiceCall, _audio.MinVolume(CallAudioStream.VoiceCall));
            _audio.SetVoiceCallMuted(true);
        }
    }

    private void SaveAudio()
    {
        if (_saved != null) return;

        _saved = new SavedAudio(
            _audio.GetVolume(CallAudioStream.Ring),
            _audio.GetVolume(CallAudioStream.VoiceCall),
            _audio.SpeakerphoneOn);
    }

    private void RestoreAudio()
    {
        if (_saved is not SavedAudio saved) return;
        _saved = null;

        _audio.SetVoiceCallMuted(false);
        _audio.SetVolume(CallAudioStream.VoiceCall, saved.VoiceCallVolume);
        _audio.SetVolume(CallAudioStream.Ring, saved.RingVolume);
        _audio.RouteToSpeaker(saved.SpeakerphoneOn);
    }

    /// <summary>Last 4 digits only - logcat isn't the place for full phone numbers.</summary>
    public static string Mask(string number) =>
        number.Length <= 4 ? "****" : new string('*', number.Length - 4) + number[^4..];

    private sealed record SavedAudio(int RingVolume, int VoiceCallVolume, bool SpeakerphoneOn);
}

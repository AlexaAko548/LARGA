using LARGA.SharedCore.Emergency;

namespace LARGA.Tests;

/// <summary>Loads the SensorTrace fixtures (Fixtures/sos*.txt, emu_*.txt).</summary>
internal static class SosTraces
{
    public static SensorTrace Load(string name) =>
        SensorTrace.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    public static List<EmergencyTraceReplayer.Detection> Replay(string name) => EmergencyTraceReplayer.Run(Load(name));
}

/// <summary>An AudioManager stand-in: ring 0-7 (min 0), voice call 1-5 (min 1, like most phones).</summary>
internal sealed class FakeCallAudio : ICallAudio
{
    public Dictionary<CallAudioStream, int> Volume { get; } = new()
    {
        [CallAudioStream.Ring] = 5,
        [CallAudioStream.VoiceCall] = 3,
    };

    public bool VoiceMuted { get; private set; }

    public bool SpeakerphoneOn { get; private set; }

    public int GetVolume(CallAudioStream stream) => Volume[stream];

    public void SetVolume(CallAudioStream stream, int volume) => Volume[stream] = volume;

    public int MinVolume(CallAudioStream stream) => stream == CallAudioStream.Ring ? 0 : 1;

    public int MaxVolume(CallAudioStream stream) => stream == CallAudioStream.Ring ? 7 : 5;

    public void RouteToSpeaker(bool speaker) => SpeakerphoneOn = speaker;

    public void SetVoiceCallMuted(bool muted) => VoiceMuted = muted;

    public (int Ring, int Voice, bool Speaker, bool Muted) Snapshot() =>
        (Volume[CallAudioStream.Ring], Volume[CallAudioStream.VoiceCall], SpeakerphoneOn, VoiceMuted);
}

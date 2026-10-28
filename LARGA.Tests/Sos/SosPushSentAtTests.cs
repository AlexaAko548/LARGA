using LARGA.SharedCore.Emergency;

namespace LARGA.Tests.Sos;

/// <summary>The SOS push to Managers / Assistant Managers says when the driver sent it (Philippine time).</summary>
public class SosPushSentAtTests
{
    // 1:40 PM, Oct 28, 2026 in the Philippines.
    private static readonly DateTime SentUtc = new(2026, 10, 28, 5, 40, 0, DateTimeKind.Utc);

    [Fact]
    public void Online_ShowsWhenItWasSent() =>
        Assert.Equal("Sent Oct 28, 2026, 1:40 PM", SosPushPolicy.SentAtText(SentUtc.AddSeconds(2), SentUtc));

    [Fact]
    public void QueuedOffline_ShowsTheSosTimeAndWhenItArrived() =>
        Assert.Equal("Sent Oct 28, 2026, 1:40 PM (reached LARGA 2:25 PM)",
            SosPushPolicy.SentAtText(SentUtc.AddMinutes(45), SentUtc));

    [Fact]
    public void QueuedOverMidnight_ArrivalShowsItsDate() =>
        Assert.Equal("Sent Oct 28, 2026, 11:50 PM (reached LARGA Oct 29, 12:20 AM)",
            SosPushPolicy.SentAtText(new DateTime(2026, 10, 28, 16, 20, 0, DateTimeKind.Utc), new DateTime(2026, 10, 28, 15, 50, 0, DateTimeKind.Utc)));

    [Fact]
    public void PhoneClockAhead_UsesTheServerTime() =>
        Assert.Equal("Sent Oct 28, 2026, 1:40 PM", SosPushPolicy.SentAtText(SentUtc, SentUtc.AddHours(3)));

    [Fact]
    public void OnlyOneTimeKnown()
    {
        Assert.Equal("Sent Oct 28, 2026, 1:40 PM", SosPushPolicy.SentAtText(null, SentUtc));
        Assert.Equal("Sent Oct 28, 2026, 1:40 PM", SosPushPolicy.SentAtText(SentUtc, null));
        Assert.Null(SosPushPolicy.SentAtText(null, null));
    }
}

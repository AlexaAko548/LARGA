using LARGA.SharedCore.Emergency;

namespace LARGA.Tests.Sos;

/// <summary>
/// Hostile Protocol volume-button trigger (VolumeSosAccessibilityService): down, up, down, up
/// within 3 seconds. false = volume down, true = volume up.
/// </summary>
public class VolumePatternTests
{
    private static bool Feed(VolumePatternDetector detector, params (double T, bool Up)[] presses)
    {
        bool fired = false;
        foreach (var (t, up) in presses)
        {
            fired = detector.OnPress(t, up);
        }
        return fired;
    }

    [Fact]
    public void Down_up_down_up_within_3_seconds_triggers()
    {
        Assert.True(Feed(new VolumePatternDetector(), (0.0, false), (0.4, true), (0.8, false), (1.2, true)));
    }

    [Fact]
    public void Pattern_slower_than_3_seconds_does_not_trigger()
    {
        Assert.False(Feed(new VolumePatternDetector(), (0.0, false), (1.2, true), (2.4, false), (3.6, true)));
    }

    [Theory]
    [InlineData(false, false, false, false)] // turning the volume down
    [InlineData(true, true, true, true)]     // turning it up
    [InlineData(true, false, true, false)]   // alternating, but starting with up
    [InlineData(false, true, true, false)]
    public void Other_press_sequences_do_not_trigger(bool a, bool b, bool c, bool d)
    {
        Assert.False(Feed(new VolumePatternDetector(), (0.0, a), (0.3, b), (0.6, c), (0.9, d)));
    }

    [Fact]
    public void Pattern_after_other_presses_still_triggers()
    {
        // Volume up, up first; the last four presses make the pattern.
        Assert.True(Feed(new VolumePatternDetector(),
            (0.0, true), (0.3, true), (0.6, false), (0.9, true), (1.2, false), (1.5, true)));
    }

    [Fact]
    public void Repeating_needs_a_full_new_pattern()
    {
        var detector = new VolumePatternDetector();
        Assert.True(Feed(detector, (0.0, false), (0.3, true), (0.6, false), (0.9, true)));

        // The presses that completed it don't count toward the next one (the repeat cancels the countdown).
        Assert.False(Feed(detector, (1.2, false), (1.5, true)));
        Assert.True(Feed(detector, (1.8, false), (2.1, true)));
    }
}

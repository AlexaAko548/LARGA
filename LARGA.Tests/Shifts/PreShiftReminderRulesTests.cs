using LARGA.SharedCore;

namespace LARGA.Tests.Shifts;

/// <summary>SRS REQ-4.1-9: the 5:30 AM pre-shift reminder (PreShiftReminderRules).</summary>
public class PreShiftReminderRulesTests
{
    private static DateTime Ph(int hour, int minute) => new(2026, 10, 28, hour, minute, 0);

    [Theory]
    [InlineData(5, 29, false)]
    [InlineData(5, 30, true)]
    [InlineData(6, 0, true)]
    [InlineData(8, 59, true)]  // catch-up after a late ManagerWeb start
    [InlineData(9, 0, false)]
    [InlineData(22, 0, false)]
    public void SendWindow_FromHalfPastFiveUntilNine(int hour, int minute, bool expected) =>
        Assert.Equal(expected, PreShiftReminderRules.IsSendWindow(Ph(hour, minute)));

    [Fact]
    public void Marker_IsOnePerDriverPerPhilippineDay()
    {
        Assert.Equal("drvA_20261028", PreShiftReminderRules.MarkerId("drvA", Ph(5, 30)));
        Assert.Equal(PreShiftReminderRules.MarkerId("drvA", Ph(5, 30)), PreShiftReminderRules.MarkerId("drvA", Ph(8, 55)));
        Assert.NotEqual(PreShiftReminderRules.MarkerId("drvA", Ph(5, 30)), PreShiftReminderRules.MarkerId("drvA", Ph(5, 30).AddDays(1)));
    }

    [Fact]
    public void Push_MatchesWhatTheDriverAppHandles()
    {
        Assert.Equal("pre_shift_reminder", PreShiftReminderRules.PushType);
        Assert.Equal("pre_shift_channel", PreShiftReminderRules.AndroidChannelId);
        Assert.Contains("TX-01", PreShiftReminderRules.Body("TX-01", Ph(5, 30)));
    }

    [Fact]
    public void Body_SaysClockInOpensAtSix_BeforeSix_AndIsOpen_After()
    {
        Assert.Contains("Clock-in opens at 6:00 AM", PreShiftReminderRules.Body("ABC 1234", Ph(5, 45)));
        Assert.Contains("Clock-in is open now", PreShiftReminderRules.Body("ABC 1234", Ph(6, 0)));
        Assert.Contains("Clock-in is open now", PreShiftReminderRules.Body("ABC 1234", Ph(8, 55)));
        Assert.DoesNotContain("opens at 6:00", PreShiftReminderRules.Body("ABC 1234", Ph(7, 10)));
    }

    [Theory]
    [InlineData("TAXI_001", "ABC 1234", "ABC 1234")]
    [InlineData("TAXI_001", "  ABC 1234 ", "ABC 1234")]
    [InlineData("TAXI_001", null, "TAXI_001")]
    [InlineData("TAXI_001", "", "TAXI_001")]
    public void UnitLabel_IsThePlate_FallingBackToTheTaxiId(string taxiId, string? plate, string expected) =>
        Assert.Equal(expected, PreShiftReminderRules.UnitLabel(taxiId, plate));

    private static readonly DateTime NowUtc = new(2026, 10, 27, 21, 40, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(PreShiftPushOutcome.Failed, 1, true)]
    [InlineData(PreShiftPushOutcome.Failed, 2, true)]
    [InlineData(PreShiftPushOutcome.Failed, 3, false)]   // MaxAttempts reached
    [InlineData(PreShiftPushOutcome.Sent, 1, false)]     // already delivered
    [InlineData(PreShiftPushOutcome.NoDevice, 1, false)] // no phone: a retry can't help
    public void FailedPush_IsRetried_UpToMaxAttempts(PreShiftPushOutcome outcome, int attempts, bool expected) =>
        Assert.Equal(expected, PreShiftReminderRules.CanClaim(outcome, attempts, NowUtc.AddMinutes(-5), NowUtc));

    [Fact]
    public void ClaimedTry_StillSending_CannotBeClaimedAgain_UntilItsLeaseRunsOut()
    {
        // Two ManagerWeb copies: the second sees the first one's claim (no outcome yet) and stays out.
        Assert.False(PreShiftReminderRules.CanClaim(null, 1, NowUtc.AddSeconds(-10), NowUtc));
        Assert.False(PreShiftReminderRules.CanClaim(PreShiftPushOutcome.Failed, 1, NowUtc.AddMinutes(-1), NowUtc));
        // The claimer stopped mid-send and never reported back: the try can be taken over later.
        Assert.True(PreShiftReminderRules.CanClaim(null, 1, NowUtc - PreShiftReminderRules.ClaimLease, NowUtc));
    }

    [Fact]
    public void ClaimLease_IsShorterThanTheFiveMinuteCheck() =>
        Assert.True(PreShiftReminderRules.ClaimLease < TimeSpan.FromMinutes(5));

    [Theory]
    [InlineData(PreShiftPushOutcome.Failed, 1, true)]
    [InlineData(PreShiftPushOutcome.Failed, 3, false)]
    [InlineData(PreShiftPushOutcome.NoDevice, 1, false)]
    [InlineData(PreShiftPushOutcome.Sent, 1, false)]
    public void RetryLeft_OnlyForAFailedPushWithTriesLeft(PreShiftPushOutcome outcome, int attempts, bool expected) =>
        Assert.Equal(expected, PreShiftReminderRules.RetryLeft(outcome, attempts));

    [Theory]
    [InlineData(PreShiftPushOutcome.Sent)]
    [InlineData(PreShiftPushOutcome.NoDevice)]
    [InlineData(PreShiftPushOutcome.Failed)]
    public void Outcome_RoundTripsThroughTheMarker(PreShiftPushOutcome outcome) =>
        Assert.Equal(outcome, PreShiftReminderRules.ParseOutcome(PreShiftReminderRules.OutcomeValue(outcome), null));

    [Fact]
    public void OlderMarkers_ReadFromTheirPushedFlag()
    {
        Assert.Equal(PreShiftPushOutcome.Sent, PreShiftReminderRules.ParseOutcome(null, true));
        Assert.Equal(PreShiftPushOutcome.Failed, PreShiftReminderRules.ParseOutcome(null, false));
        Assert.Null(PreShiftReminderRules.ParseOutcome(null, null));
    }
}

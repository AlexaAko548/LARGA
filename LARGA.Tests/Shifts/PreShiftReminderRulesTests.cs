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

    [Theory]
    [InlineData(false, 1, true)]
    [InlineData(false, 2, true)]
    [InlineData(false, 3, false)] // MaxAttempts reached
    [InlineData(true, 1, false)]  // already delivered
    public void FailedPush_IsRetried_UpToMaxAttempts(bool pushed, int attempts, bool expected) =>
        Assert.Equal(expected, PreShiftReminderRules.ShouldRetry(pushed, attempts));
}

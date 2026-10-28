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
        Assert.Contains("TX-01", PreShiftReminderRules.Body("TX-01"));
    }
}

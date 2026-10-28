using LARGA.SharedCore;

namespace LARGA.Tests.Fuel;

/// <summary>REQ-4.3-3 / REQ-5.5-6: which shift a fuel report may be linked to (FuelShiftRules).</summary>
public class FuelShiftRulesTests
{
    // 7:00 AM PH on Oct 27, 2026.
    private static readonly DateTime Start = new(2026, 10, 26, 23, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ActiveShift_AlwaysAccepts() =>
        Assert.True(FuelShiftRules.AcceptsFuelReport("Active", Start, null, Start.AddHours(20)));

    [Theory]
    [InlineData(0, true)]
    [InlineData(11.9, true)]
    [InlineData(12, true)]
    [InlineData(12.1, false)]
    public void CompletedShift_AcceptsWithin12HoursOfClockOut(double hoursAfterEnd, bool expected)
    {
        DateTime end = Start.AddHours(14);
        Assert.Equal(expected, FuelShiftRules.AcceptsFuelReport("Completed", Start, end, end.AddHours(hoursAfterEnd)));
    }

    [Fact]
    public void AutoClosedShift_WindowStartsAtAutoClose()
    {
        DateTime autoClose = ShiftRules.AutoCloseAtUtc(Start); // 6:00 AM PH the next day
        Assert.Equal(autoClose, FuelShiftRules.EffectiveEndUtc("Auto-Closed", Start, null));
        Assert.True(FuelShiftRules.AcceptsFuelReport("Auto-Closed", Start, null, autoClose.AddHours(11)));
        Assert.False(FuelShiftRules.AcceptsFuelReport("Auto-Closed", Start, null, autoClose.AddHours(13)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Cancelled")]
    public void UnknownStatus_Refused(string? status) =>
        Assert.False(FuelShiftRules.AcceptsFuelReport(status, Start, Start.AddHours(1), Start.AddHours(2)));

    [Fact]
    public void EndedRecently_UsesTheRememberedEndTime()
    {
        DateTime ended = Start.AddHours(14);
        Assert.True(FuelShiftRules.EndedRecently(ended, ended.AddHours(3)));
        Assert.False(FuelShiftRules.EndedRecently(ended, ended.AddHours(13)));
        Assert.False(FuelShiftRules.EndedRecently(null, ended));
    }

    [Fact]
    public void NoShiftMessage_MatchesTheSrsWording() =>
        Assert.Equal("Start a shift before submitting a fuel report.", FuelShiftRules.NoShiftMessage);
}

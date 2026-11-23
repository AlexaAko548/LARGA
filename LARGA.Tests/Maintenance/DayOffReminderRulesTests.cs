using LARGA.SharedCore;

namespace LARGA.Tests.Maintenance;

/// <summary>Proposal §4.2.4.7: the week-ahead Garage day-off reminder to the unit's driver (DayOffReminderRules).</summary>
public class DayOffReminderRulesTests
{
    private static readonly DateTime Today = new(2026, 10, 28);
    private static readonly DateTime ShopDay = Today.AddDays(7);

    [Fact]
    public void BookedTwoWeeksAhead_RemindedSevenDaysBefore()
    {
        DateTime booked = ShopDay.AddDays(-14);
        Assert.Equal(DayOffNotice.None, DayOffReminderRules.Decide(ShopDay, ShopDay.AddDays(-8), booked, ShopDay, null));
        Assert.Equal(DayOffNotice.Reminder, DayOffReminderRules.Decide(ShopDay, ShopDay.AddDays(-7), booked, ShopDay, null));
    }

    [Fact]
    public void Reminder_SentOnce()
    {
        DateTime booked = ShopDay.AddDays(-14);
        Assert.Equal(DayOffNotice.None, DayOffReminderRules.Decide(ShopDay, Today, booked, ShopDay, reminderSentFor: ShopDay));
        Assert.Equal(DayOffNotice.None, DayOffReminderRules.Decide(ShopDay, Today.AddDays(1), booked, ShopDay, reminderSentFor: ShopDay));
    }

    [Fact]
    public void ServiceDownOnTheSeventhDay_StillRemindsLater() =>
        Assert.Equal(DayOffNotice.Reminder,
            DayOffReminderRules.Decide(ShopDay, ShopDay.AddDays(-5), ShopDay.AddDays(-20), ShopDay, null));

    [Theory]
    [InlineData(7)]
    [InlineData(3)]
    [InlineData(0)]
    public void BookedWithinTheWeek_TheBookingMessageWasTheReminder(int daysAheadWhenBooked) =>
        Assert.Equal(DayOffNotice.None,
            DayOffReminderRules.Decide(ShopDay, Today, ShopDay.AddDays(-daysAheadWhenBooked), ShopDay, null));

    [Fact]
    public void ShopDayMoved_DriverToldAtOnce_EvenWeeksAhead()
    {
        DateTime moved = Today.AddDays(30);
        Assert.Equal(DayOffNotice.DateChanged,
            DayOffReminderRules.Decide(moved, Today, Today.AddDays(-10), notifiedShopDay: ShopDay, reminderSentFor: ShopDay));
    }

    [Fact]
    public void OlderWorkOrder_WithNoRecord_GetsTheReminder() =>
        Assert.Equal(DayOffNotice.Reminder, DayOffReminderRules.Decide(ShopDay, Today, null, null, null));

    [Fact]
    public void TodayOrPast_Nothing()
    {
        Assert.Equal(DayOffNotice.None, DayOffReminderRules.Decide(Today, Today, null, null, null));
        Assert.Equal(DayOffNotice.None, DayOffReminderRules.Decide(Today.AddDays(-1), Today, null, Today, null));
    }
}

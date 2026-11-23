using System;

namespace LARGA.SharedCore;

/// <summary>What a Scheduled work order's driver needs to be told today (DayOffReminderRules.Decide).</summary>
public enum DayOffNotice
{
    None,

    /// <summary>The shop day is <see cref="DayOffReminderRules.DaysAhead"/> days away or less.</summary>
    Reminder,

    /// <summary>The shop day isn't the one the driver was last told about.</summary>
    DateChanged,
}

/// <summary>
/// Proposal §4.2.4.7: a driver whose unit is booked into the Garage hears about it when it's booked
/// (DriverNotifier.NotifyUnitBookedForShopAsync) and again a week before the shop day, so they know
/// that's a day off their own unit. MaintenanceSchedulerService checks every Scheduled work order
/// hourly. A booking made a week ahead or less already told them, so it gets no separate reminder.
/// If the shop day moves, the driver is told straight away. Days are Philippine calendar days; the
/// work order remembers what was last said (maintenance_logs.driverNotifiedShopDay,
/// dayOffReminderSentFor), so each notice goes out once.
/// </summary>
public static class DayOffReminderRules
{
    public const int DaysAhead = 7;

    public static DayOffNotice Decide(DateTime shopDay, DateTime todayPh, DateTime? bookedOnPh,
        DateTime? notifiedShopDay, DateTime? reminderSentFor)
    {
        DateTime day = shopDay.Date;
        if (day <= todayPh.Date)
        {
            return DayOffNotice.None; // in the shop today or past: StartScheduledNow/booking already told them
        }

        if (notifiedShopDay is DateTime told && told.Date != day)
        {
            return DayOffNotice.DateChanged;
        }

        if (reminderSentFor?.Date == day || (day - todayPh.Date).TotalDays > DaysAhead)
        {
            return DayOffNotice.None;
        }

        // Booked within the week: the booking message was the reminder.
        return bookedOnPh is DateTime booked && (day - booked.Date).TotalDays <= DaysAhead
            ? DayOffNotice.None
            : DayOffNotice.Reminder;
    }
}

using System;
using System.Globalization;

namespace LARGA.SharedCore;

/// <summary>
/// SRS REQ-4.1-9: at 5:30 AM Philippine time every driver expected to work that day gets a push
/// reminding them to do the pre-shift inspection (clock-in opens at 6:00 AM, ShiftRules). "Expected
/// to work" is the Schedule Planner's own answer for the day (DriverManagementService
/// .GetWeekScheduleAsync: a unit assigned or lent, no DayOff, license eligible, unit not in the shop),
/// minus drivers who already have a shift open. Sent at most once per driver per day - a marker
/// document (<see cref="MarkerCollection"/>/<see cref="MarkerId"/>) is created first. If ManagerWeb
/// wasn't running at 5:30 (Cloud Run scaled to zero), it still sends until <see cref="CatchUpUntil"/>.
/// </summary>
public static class PreShiftReminderRules
{
    public static readonly TimeSpan SendAt = new(5, 30, 0);
    public static readonly TimeSpan CatchUpUntil = new(9, 0, 0);

    public const string MarkerCollection = "pre_shift_reminders";

    /// <summary>FCM data "type" the driver app routes to the pre-shift inspection (App.xaml.cs).</summary>
    public const string PushType = "pre_shift_reminder";

    /// <summary>Android channel declared by the driver app (MainActivity).</summary>
    public const string AndroidChannelId = "pre_shift_channel";

    public const string Title = "Shift reminder";

    /// <summary>Whether reminders go out at this Philippine time: 5:30 up to 9:00 AM.</summary>
    public static bool IsSendWindow(DateTime philippineNow) =>
        philippineNow.TimeOfDay >= SendAt && philippineNow.TimeOfDay < CatchUpUntil;

    /// <summary>One marker per driver per Philippine calendar day.</summary>
    public static string MarkerId(string driverId, DateTime philippineDay) =>
        $"{driverId}_{philippineDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    public static string Body(string taxiId) =>
        $"Good morning! Clock-in opens at 6:00 AM. Complete the pre-shift inspection for {taxiId} before you head out.";
}

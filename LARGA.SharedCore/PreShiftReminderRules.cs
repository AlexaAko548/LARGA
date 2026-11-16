using System;
using System.Globalization;

namespace LARGA.SharedCore;

/// <summary>
/// SRS REQ-4.1-9: at 5:30 AM Philippine time every driver expected to work that day gets a push
/// reminding them to do the pre-shift inspection (clock-in opens at 6:00 AM, ShiftRules). "Expected
/// to work" is the Schedule Planner's own answer for the day (DriverManagementService
/// .GetWeekScheduleAsync: a unit assigned or lent, no DayOff, license eligible, unit not in the shop),
/// minus drivers who already have a shift open. Sent at most once per driver per day - a marker
/// document (<see cref="MarkerCollection"/>/<see cref="MarkerId"/>) is created first. A push that fails
/// is tried again on later checks, up to <see cref="MaxAttempts"/> times in all. ManagerWeb runs one
/// always-on Cloud Run instance (deploy-cloudrun.ps1: --min-instances 1); the window up to
/// <see cref="CatchUpUntil"/> is the safety net when it was restarted or redeployed around 5:30.
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

    /// <summary>Tries per driver per day, the first one included (a failed push is retried on later checks).</summary>
    public const int MaxAttempts = 3;

    /// <summary>Whether a reminder whose push failed <paramref name="attempts"/> times may be tried again.</summary>
    public static bool ShouldRetry(bool pushed, int attempts) => !pushed && attempts < MaxAttempts;

    /// <summary>The unit the driver knows: its plate number, or the taxi ID when no plate is on file.</summary>
    public static string UnitLabel(string taxiId, string? plateNumber) =>
        string.IsNullOrWhiteSpace(plateNumber) ? taxiId : plateNumber.Trim();

    /// <param name="philippineNow">When it's sent: before 6:00 AM clock-in "opens at 6:00", from then it "is open now"
    /// (catch-up sends, ShiftRules' 6:00 AM clock-in).</param>
    public static string Body(string unitLabel, DateTime philippineNow)
    {
        string clockIn = philippineNow.Hour < 6 ? "Clock-in opens at 6:00 AM." : "Clock-in is open now.";
        return $"Good morning! {clockIn} Complete the pre-shift inspection for {unitLabel} before you head out.";
    }
}

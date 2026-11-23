using System;
using System.Globalization;

namespace LARGA.SharedCore;

/// <summary>
/// SRS REQ-4.1-9: at 5:30 AM Philippine time every driver expected to work that day gets a push
/// reminding them to do the pre-shift inspection (clock-in opens at 6:00 AM, ShiftRules). "Expected
/// to work" is the Schedule Planner's own answer for the day (DriverManagementService
/// .GetWeekScheduleAsync: a unit assigned or lent, no DayOff, license eligible, unit not in the shop),
/// minus drivers who already have a shift open. One marker document per driver per day
/// (<see cref="MarkerCollection"/>/<see cref="MarkerId"/>) records each try: a try is claimed in a
/// Firestore transaction before the push goes out (<see cref="CanClaim"/>), so even two ManagerWeb
/// copies running at once (a redeploy at 5:30) can't both send it. A push that fails is tried again on
/// later checks, up to <see cref="MaxAttempts"/> tries; a driver with no registered phone is not.
/// ManagerWeb runs one always-on Cloud Run instance (deploy-cloudrun.ps1: --min-instances 1); the
/// window up to <see cref="CatchUpUntil"/> is the safety net when it was restarted or redeployed around 5:30.
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

    /// <summary>How long a claimed try belongs to whoever claimed it. A try whose outcome never got
    /// recorded (the instance stopped mid-send) can be claimed again after this. Shorter than the
    /// service's 5-minute check, so a failed push is retried on the next check.</summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(4);

    /// <summary>Whether another try may be claimed for a driver today.</summary>
    /// <param name="outcome">The last try's recorded outcome; null while a claimed try hasn't reported back.</param>
    /// <param name="attempts">Tries claimed so far today.</param>
    /// <param name="lastClaimUtc">When the last try was claimed; null when none was.</param>
    public static bool CanClaim(PreShiftPushOutcome? outcome, int attempts, DateTime? lastClaimUtc, DateTime nowUtc)
    {
        if (outcome is PreShiftPushOutcome.Sent or PreShiftPushOutcome.NoDevice || attempts >= MaxAttempts)
        {
            return false;
        }
        // Failed, or a claimed try with no outcome yet: only once its lease has run out (it may still be sending).
        return lastClaimUtc is not DateTime claimed || nowUtc - claimed >= ClaimLease;
    }

    /// <summary>Whether a try that ended with <paramref name="outcome"/> leaves another one for a later check.</summary>
    public static bool RetryLeft(PreShiftPushOutcome outcome, int attempts) =>
        outcome == PreShiftPushOutcome.Failed && attempts < MaxAttempts;

    /// <summary>Marker field value for each outcome.</summary>
    public static string OutcomeValue(PreShiftPushOutcome outcome) => outcome switch
    {
        PreShiftPushOutcome.Sent => "sent",
        PreShiftPushOutcome.NoDevice => "noDevice",
        _ => "failed",
    };

    /// <summary>Reads a marker's outcome. Markers from before outcomes were recorded only carry
    /// "pushed": true means sent, false a failed try.</summary>
    public static PreShiftPushOutcome? ParseOutcome(string? value, bool? legacyPushed) => value switch
    {
        "sent" => PreShiftPushOutcome.Sent,
        "noDevice" => PreShiftPushOutcome.NoDevice,
        "failed" => PreShiftPushOutcome.Failed,
        _ => legacyPushed switch
        {
            true => PreShiftPushOutcome.Sent,
            false => PreShiftPushOutcome.Failed,
            null => null,
        },
    };

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

/// <summary>How one pre-shift reminder push went (DriverNotifier.SendPreShiftReminderAsync).</summary>
public enum PreShiftPushOutcome
{
    /// <summary>Reached at least one of the driver's phones.</summary>
    Sent,
    /// <summary>The driver has no registered Android phone; retrying can't help.</summary>
    NoDevice,
    /// <summary>The push didn't reach any phone; worth another try.</summary>
    Failed,
}

using System;

namespace LARGA.SharedCore;

/// <summary>
/// SRS REQ-4.3-3 / REQ-5.5-6: every fuel log belongs to a shift. A driver can file one during
/// their active shift, or for a shift that ended within the last <see cref="PostShiftWindow"/>
/// (a refuel at the end of the day reported after clock-out). With neither, the report is refused
/// with <see cref="NoShiftMessage"/>. firestore.rules check the same window on create.
/// </summary>
public static class FuelShiftRules
{
    public static readonly TimeSpan PostShiftWindow = TimeSpan.FromHours(12);

    public const string NoShiftMessage = "Start a shift before submitting a fuel report.";

    /// <summary>When the shift ended: its shiftEnd, or - for an Auto-Closed shift, which never got
    /// one (ShiftDeadlineService) - the auto-close time. Null while it's still Active.</summary>
    public static DateTime? EffectiveEndUtc(string? status, DateTime shiftStartUtc, DateTime? shiftEndUtc)
    {
        if (string.Equals(status, "Active", StringComparison.Ordinal))
        {
            return null;
        }

        return shiftEndUtc ?? ShiftRules.AutoCloseAtUtc(shiftStartUtc);
    }

    /// <summary>Whether a fuel report can still be filed against this shift at <paramref name="nowUtc"/>.</summary>
    public static bool AcceptsFuelReport(string? status, DateTime shiftStartUtc, DateTime? shiftEndUtc, DateTime nowUtc)
    {
        if (string.Equals(status, "Active", StringComparison.Ordinal))
        {
            return true;
        }

        if (status is not ("Completed" or "Auto-Closed"))
        {
            return false;
        }

        DateTime end = EffectiveEndUtc(status, shiftStartUtc, shiftEndUtc)!.Value;
        return nowUtc >= end && nowUtc - end <= PostShiftWindow;
    }

    /// <summary>The phone-side check when only the remembered end time is known (offline, or
    /// before Firestore answers): ended within the window by the phone's clock.</summary>
    public static bool EndedRecently(DateTime? endedAtUtc, DateTime nowUtc) =>
        endedAtUtc is DateTime end && nowUtc >= end.AddMinutes(-5) && nowUtc - end <= PostShiftWindow;
}

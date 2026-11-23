using System;

namespace LARGA.SharedCore;

/// <summary>
/// The Philippines observes a single, fixed UTC+8 offset year-round (no daylight saving), so
/// converting to/from Philippine time is just a constant offset - no <see cref="TimeZoneInfo"/>
/// lookup needed, which would also require a Windows-specific zone ID ("Singapore Standard
/// Time") on Windows vs. an IANA ID ("Asia/Manila") on Linux, and break across platforms/hosts.
///
/// Everything in Firestore is written/queried in UTC (see FleetReportingService's read-cost
/// notes, etc.) - that stays UTC-based, as it must. This class is purely for *display*:
/// converting a UTC value to Philippine local time right before it's shown to a manager/driver,
/// so the app reads correctly for a Philippines-based fleet regardless of what timezone the
/// server/host machine itself happens to be running in (relying on ambient server-local time -
/// DateTime.Now - is fragile: it only looks right by coincidence on a machine already set to
/// UTC+8, and would be wrong the moment this is hosted somewhere else, e.g. a UTC-default
/// cloud VM).
/// </summary>
public static class PhilippineTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    /// <summary>The current moment, expressed as Philippine local time. Use this instead of
    /// DateTime.Now for anything shown to a user.</summary>
    public static DateTime Now => DateTime.UtcNow + Offset;

    /// <summary>Converts a UTC DateTime to Philippine local time for display.</summary>
    public static DateTime ToPhilippineTime(this DateTime utcValue)
    {
        DateTime asUtc = utcValue.Kind == DateTimeKind.Local
            ? utcValue.ToUniversalTime()
            : DateTime.SpecifyKind(utcValue, DateTimeKind.Utc);
        return asUtc + Offset;
    }

    /// <summary>Nullable overload - most of the app's optional date fields (ShiftEnd,
    /// DateResolved, LastPaymentDate, etc.) are DateTime?.</summary>
    public static DateTime? ToPhilippineTime(this DateTime? utcValue) =>
        utcValue.HasValue ? utcValue.Value.ToPhilippineTime() : null;
}

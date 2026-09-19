using System;

namespace LARGA.MobileApp.Services;

/// <summary>
/// WORKAROUND for a Plugin.Firebase.Firestore 4.0.0 bug on Android: it marshals a Firestore
/// Timestamp field bound to System.DateTime by calling DateTime.FromFileTimeUtc(millis), where
/// `millis` is the correct Unix-epoch millisecond value - but FromFileTimeUtc expects 100ns
/// ticks since 1601-01-01, not milliseconds since 1970-01-01. So every date this plugin hands
/// back lands near "Jan 1601" even though the value stored in Firestore is correct (confirmed
/// directly in the Firestore console). Call <see cref="Apply"/> on any DateTime read through
/// this plugin (e.g. maintenance_logs.dateLogged) to reverse the same arithmetic and recover
/// the real value.
/// </summary>
public static class FirestoreDateTimeFix
{
    private static readonly long FileTimeEpochTicks = new DateTime(1601, 1, 1).Ticks;

    public static DateTime Apply(DateTime value)
    {
        if (value.Year > 1700) return value; // not affected - already a sane date

        var millis = value.Ticks - FileTimeEpochTicks;
        return DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime;
    }
}

using System;

namespace LARGA.SharedCore.Emergency;

/// <summary>
/// When ManagerWeb's SosPushService skips the push for an alert it sees for the first time.
/// Alerts that were already old when the service started (a deploy, a restart) are claimed
/// without a push, so managers aren't buzzed about old unresolved alerts.
/// </summary>
public static class SosPushPolicy
{
    public static readonly TimeSpan MaxAlertAge = TimeSpan.FromMinutes(30);

    /// <summary>
    /// True when the alert reached the server more than <see cref="MaxAlertAge"/> ago. The age is
    /// taken from the document's server create time, not emergency_alerts.timestamp: that is the
    /// phone's clock at the moment of the SOS, so an SOS queued offline for over 30 minutes
    /// (SOS-004), or sent from a phone whose clock is behind, was never pushed. The phone time is
    /// only used when the create time isn't known.
    /// </summary>
    public static bool IsTooOldToPush(DateTime? serverCreatedAtUtc, DateTime? phoneTimestampUtc, DateTime nowUtc)
    {
        DateTime? reference = serverCreatedAtUtc ?? phoneTimestampUtc;
        return reference is DateTime at && nowUtc - at > MaxAlertAge;
    }
}

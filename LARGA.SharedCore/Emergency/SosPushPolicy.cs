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

    /// <summary>An SOS held on the phone longer than this (no signal) also says when it reached LARGA.</summary>
    public static readonly TimeSpan OfflineDelayShown = TimeSpan.FromMinutes(2);

    /// <summary>
    /// When the driver sent the SOS, for the push to Managers / Assistant Managers, in Philippine time:
    /// "Sent Oct 28, 2026, 1:40 PM". That's the phone's emergency_alerts.timestamp - the moment of the
    /// SOS, even if it was queued offline - adding "(reached LARGA 1:45 PM)" when it arrived over
    /// <see cref="OfflineDelayShown"/> later. A phone time later than the server's receipt (a wrong
    /// clock) isn't trusted: the server time is shown instead. Null when neither is known.
    /// </summary>
    public static string? SentAtText(DateTime? serverCreatedAtUtc, DateTime? phoneTimestampUtc)
    {
        DateTime? sent = phoneTimestampUtc is DateTime phone && (serverCreatedAtUtc is null || phone <= serverCreatedAtUtc.Value.AddMinutes(1))
            ? phone
            : serverCreatedAtUtc;
        if (sent is not DateTime sentUtc)
        {
            return null;
        }

        string text = $"Sent {sentUtc.ToPhilippineTime().ToString("MMM d, yyyy, h:mm tt", System.Globalization.CultureInfo.InvariantCulture)}";
        if (serverCreatedAtUtc is DateTime received && received - sentUtc > OfflineDelayShown)
        {
            DateTime receivedPh = received.ToPhilippineTime();
            string format = receivedPh.Date == sentUtc.ToPhilippineTime().Date ? "h:mm tt" : "MMM d, h:mm tt";
            text += $" (reached LARGA {receivedPh.ToString(format, System.Globalization.CultureInfo.InvariantCulture)})";
        }
        return text;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace LARGA.SharedCore.Alerts;

/// <summary>
/// The manager app's Alert Center Unread count, in one place for the Alert Center page
/// (AlertCenterViewModel) and the Alerts tab badge (UnreadBadgeService), so the two can't disagree.
/// Unread = every pending SOS / flagged fuel / clock-in card (they stay unread until handled) plus
/// every unread system_alerts notice the phone shows: not a web-only type, not a row an action
/// card already stands for, and not a read notice past <see cref="AlertFeed.HideReadAfter"/>.
/// </summary>
public static class AlertCenterUnread
{
    /// <summary>A system_alerts row (the bell feed) as the phone reads it.</summary>
    public readonly record struct BellRow(string Id, string? Type, bool IsRead, DateTime TimestampUtc);

    /// <summary>Whether the Alert Center lists this bell row (read or not).</summary>
    public static bool ShowsBellRow(BellRow row, IReadOnlySet<string> actionCardIds, DateTime nowUtc) =>
        AlertFeed.ShownOnPhone(row.Type)
        && (AlertFeed.SourceIdFor(row.Id, row.Type) is not string sourceId || !actionCardIds.Contains(sourceId))
        && !AlertFeed.IsHiddenRead(row.IsRead, row.TimestampUtc, nowUtc);

    /// <summary>The Unread tab's count. <paramref name="actionCardIds"/>: the IDs of the pending
    /// emergency_alerts, flagged fuel_logs and pending clockin_requests (one per card).</summary>
    public static int Count(IReadOnlyCollection<string> actionCardIds, IEnumerable<BellRow> bellRows, DateTime nowUtc)
    {
        var cards = actionCardIds.ToHashSet(StringComparer.Ordinal);
        return actionCardIds.Count + bellRows.Count(row => !row.IsRead && ShowsBellRow(row, cards, nowUtc));
    }
}

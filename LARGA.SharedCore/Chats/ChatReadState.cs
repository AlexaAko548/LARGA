using System;
using System.Collections.Generic;
using System.Linq;
using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore.Chats;

/// <summary>
/// Per-person read state of a driver conversation, kept on its chats/{driverId} doc next to the
/// shared isUnread flag (which still means "unread by staff" and is what ManagerWeb shows):
///  - lastMessageAt: server time of the newest message (every send path stamps it);
///  - lastSenderId: who wrote it - a uid, or "system" for a ManagerWeb notice;
///  - lastReadAtMs.{uid}: for each person, the lastMessageAt they had seen when they last had the
///    conversation open, in Unix milliseconds. It's copied from the doc rather than taken from the
///    phone's clock, so a wrong clock can't mark anything read (or unread). A number, not a
///    timestamp: Plugin.Firebase.Firestore 4.0.0 can't read a timestamp inside a map (it throws
///    "Could not convert object of type Firebase.Timestamp to Java.Lang.Object").
/// firestore.rules let a user write only their own lastReadAtMs entry and only their own uid as
/// lastSenderId. Used by the manager app's Chats list and badges and the driver's Message Manager
/// button.
/// </summary>
public static class ChatReadState
{
    public const string LastMessageAtField = "lastMessageAt";
    public const string LastSenderIdField = "lastSenderId";
    // "lastReadAt" (timestamps) was used by an earlier build of this branch and is ignored now.
    public const string LastReadAtField = "lastReadAtMs";

    /// <summary>lastSenderId of a ManagerWeb notice (DriverNotifier) - unread for the driver and every staff member.</summary>
    public const string SystemSenderId = ChatMessage.SystemSenderId;

    /// <summary>Most a number badge shows; above it reads "99+".</summary>
    public const int MaxBadgeNumber = 99;

    /// <summary>
    /// Whether the conversation has something <paramref name="viewerUid"/> hasn't read: the newest
    /// message is newer than their marker and isn't their own. A staff member also has it unread
    /// while the shared isUnread flag is set (a driver message nobody on staff has opened yet).
    /// <para>Rollout: a conversation with no lastMessageAt has had no message since this shipped.
    /// For staff it falls back to isUnread, exactly what they saw before; for the driver it reads
    /// as read until the next message, so nobody gets a badge for old history. A person with no
    /// marker on a conversation that does have a newer message has it unread.</para>
    /// Times are compared to the millisecond - the precision the phone reads them at.
    /// </summary>
    public static bool IsUnreadFor(string? viewerUid, bool viewerIsStaff, DateTime? lastMessageAtUtc, string? lastSenderId,
        DateTime? viewerLastReadAtUtc, bool staffIsUnread)
    {
        if (string.IsNullOrWhiteSpace(viewerUid))
        {
            return false;
        }

        if (viewerIsStaff && staffIsUnread)
        {
            return true;
        }

        if (lastMessageAtUtc is not DateTime lastMessage || lastSenderId == viewerUid)
        {
            return false;
        }

        return viewerLastReadAtUtc is not DateTime lastRead || ToMillis(lastMessage) > ToMillis(lastRead);
    }

    public static bool IsUnreadFor(this Conversation conversation, string? viewerUid, bool viewerIsStaff) =>
        IsUnreadFor(viewerUid, viewerIsStaff, conversation.LastMessageAtUtc, conversation.LastSenderId,
            viewerUid is not null && conversation.LastReadAtUtc.TryGetValue(viewerUid, out DateTime read) ? read : null,
            conversation.StaffIsUnread);

    /// <summary>The Chats tab badge: how many driver conversations have something unread for this staff member.</summary>
    public static int UnreadConversationCount(IEnumerable<Conversation> conversations, string? staffUid) =>
        conversations.Count(c => c.IsUnreadFor(staffUid, viewerIsStaff: true));

    /// <summary>The driver's Message Manager badge: 1 while their conversation has an unread staff or system message.</summary>
    public static int DriverBadgeCount(Conversation? ownConversation, string? driverUid) =>
        ownConversation is not null && ownConversation.IsUnreadFor(driverUid, viewerIsStaff: false) ? 1 : 0;

    /// <summary>"1".."99", "99+" above that, or null when the badge is hidden (0).</summary>
    public static string? BadgeText(int count) =>
        count <= 0 ? null : count > MaxBadgeNumber ? $"{MaxBadgeNumber}+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The lastReadAtMs value (as a time) to write when <paramref name="viewerUid"/> has the conversation open, or
    /// null when there's nothing to write: no message since the rollout, the newest message is the
    /// viewer's own, or the marker is already at the newest message.
    /// </summary>
    public static DateTime? ReadMarkerToWrite(this Conversation conversation, string? viewerUid)
    {
        if (string.IsNullOrWhiteSpace(viewerUid) || conversation.LastMessageAtUtc is not DateTime lastMessage
            || conversation.LastSenderId == viewerUid)
        {
            return null;
        }

        return conversation.LastReadAtUtc.TryGetValue(viewerUid, out DateTime read) && ToMillis(read) >= ToMillis(lastMessage)
            ? null
            : lastMessage;
    }

    private static long ToMillis(DateTime utc) => utc.Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>A UTC time as stored in lastReadAtMs (Unix milliseconds).</summary>
    public static long ToUnixMillis(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>A lastReadAtMs value back as a UTC time; null for anything that isn't a sane number.</summary>
    public static DateTime? FromUnixMillis(object? value)
    {
        long? millis = value switch
        {
            long l => l,
            int i => i,
            double d when !double.IsNaN(d) && !double.IsInfinity(d) => (long)d,
            _ => null,
        };
        return millis is long ms && ms > 0 && ms <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : null;
    }

    /// <summary>One chats/{driverId} doc's read fields (times in UTC).</summary>
    public sealed record Conversation(
        string DriverId,
        DateTime? LastMessageAtUtc,
        string? LastSenderId,
        IReadOnlyDictionary<string, DateTime> LastReadAtUtc,
        bool StaffIsUnread);
}

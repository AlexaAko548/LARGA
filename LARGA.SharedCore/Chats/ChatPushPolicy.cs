using System;
using System.Collections.Generic;
using System.Linq;
using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore.Chats;

/// <summary>
/// Who ManagerWeb's ChatPushService pushes a new chat message to, and what the push says. Each
/// driver has one conversation, chats/{driverId}/messages, shared by the driver and every Manager
/// and Assistant Manager:
///  - the driver writes → every Manager and Assistant Manager;
///  - a staff member writes → the driver of that conversation and every other staff member;
///  - a ManagerWeb notice (senderRole "System", DriverNotifier) → nobody: DriverNotifier already
///    pushes it to the driver itself.
/// A driver only ever hears about their own conversation, and the sender is never notified - not
/// even on another phone that carries the same push token.
/// </summary>
public static class ChatPushPolicy
{
    /// <summary>FCM data "type" the app routes to the conversation on tap (App.xaml.cs).</summary>
    public const string PushType = "chat_message";

    /// <summary>The channel the app creates in MainActivity.CreateNotificationChannels.</summary>
    public const string AndroidChannelId = "chat_messages_channel";

    /// <summary>A message that reached the server longer ago than this when the service first sees
    /// it (a deploy, a restart) isn't pushed, so old history never buzzes anyone.</summary>
    public static readonly TimeSpan MaxMessageAge = TimeSpan.FromMinutes(5);

    /// <summary>How far back (by the message's own timestamp, a phone's clock) the listener looks
    /// when it starts. Wide on purpose: a phone whose clock is behind still writes a recent message
    /// with an older timestamp; <see cref="IsTooOldToPush"/> is what decides, on server time.</summary>
    public static readonly TimeSpan ListenWindow = TimeSpan.FromDays(1);

    /// <summary>Longest notification body; longer messages end in "…".</summary>
    public const int MaxBodyLength = 160;

    /// <summary>A users/{uid} doc as the push needs it.</summary>
    public readonly record struct User(string Uid, string? Role, string? FcmToken, DateTime? TokenUpdatedAtUtc);

    /// <summary>A new message in chats/{DriverId}/messages.</summary>
    public readonly record struct Message(string DriverId, bool IsDriver, string? SenderId, string? SenderRole);

    public static bool IsSystemMessage(string? senderRole) =>
        string.Equals(senderRole?.Trim(), ChatMessage.SystemRole, StringComparison.OrdinalIgnoreCase);

    /// <summary>Who wrote the message: the driver of the conversation for a driver message, else
    /// senderId (empty only on staff messages older than the sender fields).</summary>
    public static string? SenderUid(Message message) =>
        message.IsDriver ? message.DriverId : string.IsNullOrWhiteSpace(message.SenderId) ? null : message.SenderId;

    /// <summary>The users who should hear about <paramref name="message"/>, by uid.</summary>
    public static IReadOnlyList<string> RecipientUids(Message message, IEnumerable<User> users)
    {
        if (string.IsNullOrWhiteSpace(message.DriverId) || IsSystemMessage(message.SenderRole))
        {
            return Array.Empty<string>();
        }

        string? sender = SenderUid(message);
        var recipients = new List<string>();
        foreach (User user in users)
        {
            if (string.IsNullOrWhiteSpace(user.Uid) || user.Uid == sender)
            {
                continue;
            }

            if (StaffRoles.IsStaff(user.Role))
            {
                // A staff message with no sender can't tell its writer apart from the other staff,
                // so it reaches only the driver.
                if (message.IsDriver || sender is not null)
                {
                    recipients.Add(user.Uid);
                }
            }
            else if (!message.IsDriver && StaffRoles.IsDriver(user.Role) && user.Uid == message.DriverId)
            {
                recipients.Add(user.Uid);
            }
        }
        return recipients;
    }

    /// <summary>
    /// The push tokens to send <paramref name="message"/> to. A token belongs to the user who saved
    /// it last (tokenUpdatedAt): on a shared phone, the account signed in now, not one that was
    /// signed in before and whose doc still holds the token. A token two users saved at the same
    /// moment belongs to neither. Any token on the sender's own doc is left out.
    /// </summary>
    public static IReadOnlyList<string> RecipientTokens(Message message, IReadOnlyCollection<User> users)
    {
        IReadOnlyList<string> recipients = RecipientUids(message, users);
        if (recipients.Count == 0)
        {
            return Array.Empty<string>();
        }

        Dictionary<string, string?> owners = TokenOwners(users);
        string? sender = SenderUid(message);
        var senderTokens = users
            .Where(u => sender is not null && u.Uid == sender && !string.IsNullOrWhiteSpace(u.FcmToken))
            .Select(u => u.FcmToken!.Trim())
            .ToHashSet(StringComparer.Ordinal);

        var recipientSet = recipients.ToHashSet(StringComparer.Ordinal);
        return users
            .Where(u => recipientSet.Contains(u.Uid) && !string.IsNullOrWhiteSpace(u.FcmToken))
            .Select(u => (u.Uid, Token: u.FcmToken!.Trim()))
            .Where(t => owners.TryGetValue(t.Token, out string? owner) && owner == t.Uid && !senderTokens.Contains(t.Token))
            .Select(t => t.Token)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // token -> the uid that saved it last, or null when that can't be told apart.
    private static Dictionary<string, string?> TokenOwners(IEnumerable<User> users)
    {
        var owners = new Dictionary<string, (string? Uid, DateTime At)>(StringComparer.Ordinal);
        foreach (User user in users.Where(u => !string.IsNullOrWhiteSpace(u.FcmToken)))
        {
            string token = user.FcmToken!.Trim();
            DateTime at = user.TokenUpdatedAtUtc ?? DateTime.MinValue;
            if (!owners.TryGetValue(token, out var current) || at > current.At)
            {
                owners[token] = (user.Uid, at);
            }
            else if (at == current.At && current.Uid != user.Uid)
            {
                owners[token] = (null, at);
            }
        }
        return owners.ToDictionary(o => o.Key, o => o.Value.Uid, StringComparer.Ordinal);
    }

    /// <summary>True when the message reached the server more than <see cref="MaxMessageAge"/>
    /// ago (its document create time). Unknown create time: pushed.</summary>
    public static bool IsTooOldToPush(DateTime? serverCreatedAtUtc, DateTime nowUtc) =>
        serverCreatedAtUtc is DateTime created && nowUtc - created > MaxMessageAge;

    /// <summary>The notification title: the driver's name for a driver message, else the staff
    /// sender's chat label ("Ana Reyes (Assistant Manager)").</summary>
    public static string Title(bool isDriver, string? driverName, string? senderName, string? senderRole) =>
        isDriver
            ? (string.IsNullOrWhiteSpace(driverName) ? "Driver" : driverName.Trim())
            : ChatMessage.LabelFor(false, senderName, senderRole);

    /// <summary>The message text on one line, cut at a word to <see cref="MaxBodyLength"/>.</summary>
    public static string Body(string? text)
    {
        string flat = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= MaxBodyLength)
        {
            return flat;
        }

        string cut = flat[..(MaxBodyLength - 1)];
        int space = cut.LastIndexOf(' ');
        if (space >= MaxBodyLength / 2)
        {
            cut = cut[..space];
        }
        else if (char.IsHighSurrogate(cut[^1]))
        {
            cut = cut[..^1]; // don't split an emoji
        }
        return cut.TrimEnd() + "…";
    }
}

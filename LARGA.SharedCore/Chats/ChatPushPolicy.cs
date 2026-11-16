using System;
using System.Collections.Generic;
using System.Linq;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Push;

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

    /// <summary>Longest notification preview; longer messages end in "…". Short on purpose - the
    /// full text stays in the chat, behind the sign-in.</summary>
    public const int MaxBodyLength = 100;

    /// <summary>A users/{uid} doc as the push needs it (its push tokens are separate:
    /// users/{uid}/fcmTokens, <see cref="PushTokenRules"/>).</summary>
    public readonly record struct User(string Uid, string? Role);

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
    /// The devices to push <paramref name="message"/> to: each recipient's own tokens
    /// (<see cref="PushTokenRules.Deliverable"/> - a token belongs to the account that registered it
    /// last, so a shared phone only hears about the account signed in on it now), never a token that
    /// also sits under the sender.
    /// </summary>
    public static IReadOnlyList<PushTokenRules.DeviceToken> RecipientDevices(
        Message message, IReadOnlyCollection<User> users, IEnumerable<PushTokenRules.DeviceToken> tokens)
    {
        IReadOnlyList<string> recipients = RecipientUids(message, users);
        return recipients.Count == 0
            ? Array.Empty<PushTokenRules.DeviceToken>()
            : PushTokenRules.Deliverable(tokens, recipients, excludeUid: SenderUid(message));
    }

    /// <summary>Data keys of a chat push. Chat pushes are data-only: the phone's presenter and the
    /// browser's service worker draw the notification themselves, after checking recipientUid.</summary>
    public const string ConversationIdKey = "conversationId";
    public const string TitleKey = "title";
    public const string BodyKey = "body";
    public const string DriverNameKey = "driverName";

    /// <summary>
    /// The whole payload of one recipient's chat push, kept small: who wrote (title), a short
    /// preview (body), which conversation, and who it's for. The driver's name - the staff app's
    /// chat header - only goes to staff; a driver's own conversation needs no name.
    /// </summary>
    public static Dictionary<string, string> Payload(
        string conversationId, string recipientUid, bool recipientIsStaff, string title, string body, string? driverName)
    {
        var data = new Dictionary<string, string>
        {
            ["type"] = PushType,
            [ConversationIdKey] = conversationId,
            [PushTokenRules.RecipientUidKey] = recipientUid,
            [TitleKey] = title,
            [BodyKey] = body,
        };
        if (recipientIsStaff && !string.IsNullOrWhiteSpace(driverName))
        {
            data[DriverNameKey] = driverName.Trim();
        }
        return data;
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

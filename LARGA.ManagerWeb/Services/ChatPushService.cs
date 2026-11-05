using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using FirebaseAdmin.Messaging;
using Google.Cloud.Firestore;
using LARGA.SharedCore.Chats;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Pushes every new chat message (chats/{driverId}/messages, from the driver app, the manager app
/// or this dashboard) to the people in that conversation - the rules are in
/// <see cref="ChatPushPolicy"/>: a driver's message to every Manager and Assistant Manager, a
/// staff message to the driver and the other staff, never to the sender, and nothing for a
/// DriverNotifier notice (it pushes those itself).
///
/// Built like SosPushService: a live collection-group listener on "messages" (restarted after a
/// failure), and each message is claimed in a transaction (pushSentAt) before it's sent, so it's
/// pushed exactly once across restarts and instances. The listener only reads the last
/// <see cref="ChatPushPolicy.ListenWindow"/> of messages, and a message that reached the server
/// more than <see cref="ChatPushPolicy.MaxMessageAge"/> before it's first seen isn't pushed, so a
/// deploy doesn't push old history. Recipients' tokens are users/{uid}.fcmToken
/// (NotificationService). Never throws out of the host.
/// </summary>
public class ChatPushService : BackgroundService
{
    // Must match the FirebaseApp name created with the FirebaseAuth registration in Program.cs.
    private const string FirebaseAppName = "LargaManagerWeb";

    private const string PushSentAtField = "pushSentAt";

    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(15);

    private readonly Lazy<FirestoreDb> _db;
    private readonly Lazy<FirebaseAuth> _auth;
    private readonly ILogger<ChatPushService> _logger;

    public ChatPushService(Lazy<FirestoreDb> db, Lazy<FirebaseAuth> auth, ILogger<ChatPushService> logger)
    {
        _db = db;
        _auth = auth;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            FirestoreChangeListener? listener = null;
            try
            {
                // Needs the collection-group index on messages.timestamp (firestore.indexes.json).
                listener = _db.Value.CollectionGroup("messages")
                    .WhereGreaterThanOrEqualTo("timestamp", DateTime.UtcNow - ChatPushPolicy.ListenWindow)
                    .Listen(OnSnapshotAsync, stoppingToken);

                // Completes only when the listener stops (cancellation) or faults.
                await listener.ListenerTask;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Chat push listener stopped; restarting in {Delay}.", RestartDelay);
            }
            finally
            {
                if (listener is not null)
                {
                    try { await listener.StopAsync(); } catch { /* already stopped */ }
                }
            }

            try
            {
                await Task.Delay(RestartDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task OnSnapshotAsync(QuerySnapshot snapshot, CancellationToken token)
    {
        foreach (DocumentChange change in snapshot.Changes)
        {
            if (change.ChangeType != DocumentChange.Type.Added)
            {
                continue;
            }

            try
            {
                await HandleNewMessageAsync(change.Document, token);
            }
            catch (Exception ex)
            {
                // One message failing must not stop the others (or the listener).
                _logger.LogWarning(ex, "Chat push for {Path} failed", change.Document.Reference.Path);
            }
        }
    }

    private async Task HandleNewMessageAsync(DocumentSnapshot message, CancellationToken token)
    {
        // Only chats/{driverId}/messages - a "messages" collection anywhere else isn't a driver chat.
        DocumentReference? chat = message.Reference.Parent.Parent;
        if (chat is null || chat.Parent.Id != "chats" || chat.Parent.Parent is not null || message.ContainsField(PushSentAtField))
        {
            return;
        }

        var parsed = new ChatPushPolicy.Message(
            chat.Id,
            message.TryGetValue("isDriver", out bool isDriver) && isDriver,
            message.TryGetValue("senderId", out string? senderId) ? senderId : null,
            message.TryGetValue("senderRole", out string? senderRole) ? senderRole : null);

        // Unclaimed and unpushed on purpose: there's nothing to send, and DriverNotifier pushed it already.
        if (ChatPushPolicy.IsSystemMessage(parsed.SenderRole)
            || ChatPushPolicy.IsTooOldToPush(message.CreateTime?.ToDateTime(), DateTime.UtcNow))
        {
            return;
        }

        if (!await ClaimAsync(message.Reference, token))
        {
            return; // another listener pass (or instance) already handled it
        }

        (List<ChatPushPolicy.User> users, Dictionary<string, string> names) = await GetUsersAsync(token);
        IReadOnlyList<string> tokens = ChatPushPolicy.RecipientTokens(parsed, users);
        if (tokens.Count == 0)
        {
            _logger.LogInformation("{Path}: no recipient has a push token; not pushed.", message.Reference.Path);
            return;
        }

        string driverName = names.TryGetValue(chat.Id, out string? name) ? name : string.Empty;
        if (string.IsNullOrWhiteSpace(driverName))
        {
            DocumentSnapshot chatDoc = await chat.GetSnapshotAsync(token);
            driverName = chatDoc.TryGetValue("driverName", out string? stored) ? stored ?? string.Empty : string.Empty;
        }

        string title = ChatPushPolicy.Title(parsed.IsDriver, driverName,
            message.TryGetValue("senderName", out string? senderName) ? senderName : null, parsed.SenderRole);
        string body = ChatPushPolicy.Body(message.TryGetValue("text", out string? text) ? text : null);

        _ = _auth.Value; // creates the shared FirebaseApp on first use
        FirebaseMessaging messaging = FirebaseMessaging.GetMessaging(FirebaseApp.GetInstance(FirebaseAppName));

        BatchResponse response = await messaging.SendEachForMulticastAsync(new MulticastMessage
        {
            // users/{id}.fcmToken is a registration token (NotificationService), not an
            // installation ID, so Tokens - not its suggested replacement Fids - is right here.
#pragma warning disable CS0618
            Tokens = tokens.Take(500).ToList(),
#pragma warning restore CS0618
            Notification = new Notification { Title = title, Body = body },
            Data = new Dictionary<string, string>
            {
                ["type"] = ChatPushPolicy.PushType,
                ["driverId"] = chat.Id,
                ["driverName"] = driverName,
            },
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                Notification = new AndroidNotification
                {
                    ChannelId = ChatPushPolicy.AndroidChannelId,
                    // One notification per conversation: a newer message replaces the older one.
                    Tag = $"chat_{chat.Id}",
                },
            },
        }, token);

        _logger.LogInformation("{Path} pushed to {Sent}/{Total} device(s).", message.Reference.Path, response.SuccessCount, tokens.Count);
        foreach (SendResponse failed in response.Responses.Where(r => !r.IsSuccess))
        {
            _logger.LogWarning(failed.Exception, "Chat push to one device failed (stale token?)");
        }
    }

    /// <summary>Marks the message as pushed - true only for the caller that set it first.</summary>
    private Task<bool> ClaimAsync(DocumentReference messageRef, CancellationToken token) =>
        _db.Value.RunTransactionAsync(async transaction =>
        {
            DocumentSnapshot current = await transaction.GetSnapshotAsync(messageRef);
            if (!current.Exists || current.ContainsField(PushSentAtField))
            {
                return false;
            }

            transaction.Update(messageRef, PushSentAtField, FieldValue.ServerTimestamp);
            return true;
        }, cancellationToken: token);

    /// <summary>Every user's role and push token (the role is matched case-insensitively by the
    /// policy), plus names by uid for the title. Read fresh for each message, not cached: a token
    /// cleared at sign-out must stop receiving straight away.</summary>
    private async Task<(List<ChatPushPolicy.User> Users, Dictionary<string, string> Names)> GetUsersAsync(CancellationToken token)
    {
        QuerySnapshot users = await _db.Value.Collection("users").GetSnapshotAsync(token);
        var list = users.Documents.Select(doc => new ChatPushPolicy.User(
                doc.Id,
                doc.TryGetValue("role", out object? role) ? role?.ToString() : null,
                doc.TryGetValue("fcmToken", out object? fcm) ? fcm as string : null,
                doc.TryGetValue("tokenUpdatedAt", out object? at) ? ToUtc(at) : null))
            .ToList();
        var names = new Dictionary<string, string>();
        foreach (DocumentSnapshot doc in users.Documents)
        {
            if (doc.TryGetValue("fullName", out object? n) && n is string fullName && !string.IsNullOrWhiteSpace(fullName))
            {
                names[doc.Id] = fullName.Trim();
            }
        }
        return (list, names);
    }

    private static DateTime? ToUtc(object? value) => value switch
    {
        Timestamp ts => ts.ToDateTime(),
        DateTime dt => dt.ToUniversalTime(),
        _ => null,
    };
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using Microsoft.Extensions.Logging;

namespace LARGA.SharedCore.Services;

/// <summary>One conversation in the Messages drawer's list.</summary>
public class ChatSessionView
{
    public string DriverId { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string LastMessage { get; set; } = string.Empty;
    public bool IsUnread { get; set; }

    /// <summary>UTC.</summary>
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// ManagerWeb's side of the driver chat (the SOS Dispatch chat panel). Reads and writes the
/// same documents as the mobile ChatService (Plugin.Firebase), so a message sent here shows
/// up in the driver's Message Manager screen and the manager app's Chats, and vice versa:
/// chats/{driverId}/messages/{auto} = { text, isDriver, timestamp } and the conversation
/// summary chats/{driverId} = { driverName, lastMessage, isUnread, timestamp }, where
/// isUnread means "unread by the manager".
/// </summary>
public class ManagerChatService
{
    private readonly Lazy<FirestoreDb> _dbLazy;
    private readonly ILogger<ManagerChatService> _logger;

    private FirestoreDb Db => _dbLazy.Value;

    public ManagerChatService(Lazy<FirestoreDb> dbLazy, ILogger<ManagerChatService> logger)
    {
        _dbLazy = dbLazy;
        _logger = logger;
    }

    /// <summary>Live conversation with a driver, oldest message first. Stop the returned
    /// listener (StopAsync) when the chat closes.</summary>
    public FirestoreChangeListener Listen(string driverId, Action<List<ChatMessage>> onMessages)
    {
        return Db.Collection("chats").Document(driverId).Collection("messages")
            .OrderBy("timestamp")
            .Listen(snapshot =>
            {
                var messages = new List<ChatMessage>(snapshot.Count);
                foreach (DocumentSnapshot doc in snapshot.Documents)
                {
                    try
                    {
                        messages.Add(doc.ConvertTo<ChatMessage>());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Skipping chats/{DriverId}/messages/{Id}: unreadable", driverId, doc.Id);
                    }
                }
                onMessages(messages);
            });
    }

    /// <summary>Live list of every driver conversation, most recent first - the Messages
    /// drawer's inbox (same list as the manager app's Chats screen).</summary>
    public FirestoreChangeListener ListenSessions(Action<List<ChatSessionView>> onSessions)
    {
        return Db.Collection("chats").Listen(snapshot =>
        {
            List<ChatSessionView> sessions = snapshot.Documents
                .Select(doc => new ChatSessionView
                {
                    DriverId = doc.Id,
                    DriverName = doc.TryGetValue("driverName", out string name) && !string.IsNullOrWhiteSpace(name) ? name : "Unknown driver",
                    LastMessage = doc.TryGetValue("lastMessage", out string last) ? last : string.Empty,
                    IsUnread = doc.TryGetValue("isUnread", out bool unread) && unread,
                    Timestamp = doc.TryGetValue("timestamp", out DateTime time) ? time : DateTime.MinValue,
                })
                .OrderByDescending(s => s.Timestamp)
                .ToList();
            onSessions(sessions);
        });
    }

    public async Task SendAsync(string driverId, string driverName, string text)
    {
        DateTime now = DateTime.UtcNow;
        DocumentReference session = Db.Collection("chats").Document(driverId);

        WriteBatch batch = Db.StartBatch();
        batch.Create(session.Collection("messages").Document(), new Dictionary<string, object>
        {
            ["text"] = text.Trim(),
            ["isDriver"] = false,
            ["timestamp"] = now,
        });
        batch.Set(session, new Dictionary<string, object>
        {
            ["driverName"] = driverName,
            ["lastMessage"] = text.Trim(),
            ["isUnread"] = false,
            ["timestamp"] = now,
        }, SetOptions.MergeAll);
        await batch.CommitAsync();
    }

    /// <summary>The manager has seen the driver's messages.</summary>
    public async Task MarkReadAsync(string driverId)
    {
        DocumentReference session = Db.Collection("chats").Document(driverId);
        DocumentSnapshot doc = await session.GetSnapshotAsync();
        if (doc.Exists && doc.TryGetValue("isUnread", out bool isUnread) && isUnread)
        {
            await session.UpdateAsync("isUnread", false);
        }
    }

    /// <summary>Which of these drivers have messages the manager hasn't read.</summary>
    public async Task<HashSet<string>> GetUnreadDriverIdsAsync(IEnumerable<string> driverIds)
    {
        var unread = new HashSet<string>();
        foreach (string driverId in driverIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct())
        {
            DocumentSnapshot doc = await Db.Collection("chats").Document(driverId).GetSnapshotAsync();
            if (doc.Exists && doc.TryGetValue("isUnread", out bool isUnread) && isUnread)
            {
                unread.Add(driverId);
            }
        }
        return unread;
    }
}

using LARGA.Shared.Models.Entities;
using LARGA.SharedCore; // Included to access PhilippineTime
using LARGA.SharedCore.Models.Chats;
using Plugin.Firebase.Firestore;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LARGA.SharedCore.Services;

public interface IChatService
{
    /// <summary>
    /// Writes the message and creates/updates the parent chats/{driverId} doc in one batch.
    /// Pass <paramref name="driverName"/> when already known; otherwise it is looked up from users.
    /// </summary>
    Task<bool> SendMessageAsync(string driverId, ChatMessage message, string? driverName = null);
    Task<IReadOnlyList<ChatDriver>> GetDriversAsync();
    IDisposable ListenForMessages(string driverId, Action<IList<ChatMessage>> onMessagesUpdated);
    Task<string> GetDriverPhoneNumberAsync(string driverId);
    IDisposable ListenForChatSessions(Action<IEnumerable<ChatSession>> onSessionsUpdated);
    Task MarkMessagesAsReadAsync(string driverId);
}

public class ChatService : IChatService
{
    public async Task<bool> SendMessageAsync(string driverId, ChatMessage message, string? driverName = null)
    {
        // A signed-out driver resolves to "unknown_driver" - never create a chat for that.
        if (string.IsNullOrWhiteSpace(driverId) || driverId == "unknown_driver")
        {
            System.Diagnostics.Debug.WriteLine("SendMessageAsync skipped: no valid driverId.");
            return false;
        }

        try
        {
            // 1. Resolve the driver's name (manager already knows it; driver side looks it up)
            string realDriverName = string.IsNullOrWhiteSpace(driverName)
                ? await GetDriverNameAsync(driverId)
                : driverName;

            var now = DateTime.UtcNow;
            var db = CrossFirebaseFirestore.Current;

            // 2. Lazy creation: the message and the parent chats/{driverId} doc are written in one
            //    atomic batch. Merge creates the parent on the first message and only updates these
            //    four fields afterwards, so no "does the chat exist yet?" read is needed.
            var messageRef = db.GetCollection($"chats/{driverId}/messages").CreateDocument();
            var chatRef = db.GetCollection("chats").GetDocument(driverId);

            var batch = db.CreateBatch();
            batch.SetData(messageRef, new ChatMessageProxy
            {
                Text = message.Text,
                IsDriver = message.IsDriver,
                Timestamp = now
            });
            batch.SetData(chatRef, new ChatSessionProxy
            {
                DriverName = realDriverName,
                LastMessage = message.Text,
                IsUnread = message.IsDriver, // unread for the manager when the driver sends
                Timestamp = now
            }, SetOptions.Merge());

            await batch.CommitAsync();
            return true;
        }
        catch (Exception ex)
        {
            // Prints the exact Firebase rejection reason to the Visual Studio Output window
            System.Diagnostics.Debug.WriteLine($"CRITICAL DATABASE ERROR: {ex.Message}");
            return false;
        }
    }

    private static async Task<string> GetDriverNameAsync(string driverId)
    {
        try
        {
            var userDoc = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(driverId)
                .GetDocumentSnapshotAsync<DriverProfileProxy>();

            if (!string.IsNullOrWhiteSpace(userDoc?.Data?.FullName))
            {
                return userDoc.Data.FullName;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Driver profile fetch error: {ex.Message}");
        }

        return "Unknown Driver";
    }

    public async Task<IReadOnlyList<ChatDriver>> GetDriversAsync()
    {
        try
        {
            var snapshot = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .WhereEqualsTo("role", "Driver")
                .GetDocumentsAsync<DriverProfileProxy>();

            var drivers = new List<ChatDriver>();
            foreach (var doc in snapshot.Documents)
            {
                if (doc.Data == null) continue;
                drivers.Add(new ChatDriver(
                    doc.Reference.Id,
                    string.IsNullOrWhiteSpace(doc.Data.FullName) ? "(Unnamed driver)" : doc.Data.FullName));
            }
            return drivers;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Chat driver list error: {ex.Message}");
            return Array.Empty<ChatDriver>();
        }
    }

    public IDisposable ListenForMessages(string driverId, Action<IList<ChatMessage>> onMessagesUpdated)
    {
        return CrossFirebaseFirestore.Current
            .GetCollection($"chats/{driverId}/messages")
            .OrderBy("timestamp")
            .AddSnapshotListener<ChatMessageProxy>(
            snapshot =>
            {
                var messages = new List<ChatMessage>();
                foreach (var doc in snapshot.Documents)
                {
                    if (doc.Data != null)
                    {
                        messages.Add(new ChatMessage
                        {
                            MessageId = doc.Reference.Id,
                            Text = doc.Data.Text ?? string.Empty,
                            IsDriver = doc.Data.IsDriver,
                            // FIX: Convert UTC to Philippine Time for the actual chat bubbles
                            Timestamp = doc.Data.Timestamp.ToPhilippineTime()
                        });
                    }
                }
                onMessagesUpdated?.Invoke(messages);
            },
            error =>
            {
                System.Diagnostics.Debug.WriteLine($"Chat Listener Error: {error.Message}");
            });
    }

    public async Task<string> GetDriverPhoneNumberAsync(string driverId)
    {
        try
        {
            var doc = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(driverId)
                .GetDocumentSnapshotAsync<DriverProfileProxy>();

            if (doc.Data != null && !string.IsNullOrWhiteSpace(doc.Data.PhoneNumber))
            {
                return doc.Data.PhoneNumber;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error fetching phone number: {ex.Message}");
        }

        return "09123456789";
    }

    public IDisposable ListenForChatSessions(Action<IEnumerable<ChatSession>> onSessionsUpdated)
    {
        return CrossFirebaseFirestore.Current
            .GetCollection("chats")
            .AddSnapshotListener<ChatSessionProxy>(
            snapshot =>
            {
                var sessions = new List<ChatSession>();
                foreach (var doc in snapshot.Documents)
                {
                    if (doc.Data != null)
                    {
                        sessions.Add(new ChatSession
                        {
                            HasConversation = true,
                            DriverId = doc.Reference.Id,
                            DriverName = doc.Data.DriverName ?? "Unknown Driver",
                            LastMessage = doc.Data.LastMessage ?? string.Empty,
                            // FIX: Convert UTC to Philippine Time for the Manager's list
                            Timestamp = doc.Data.Timestamp.ToPhilippineTime(),
                            IsUnread = doc.Data.IsUnread
                        });
                    }
                }
                onSessionsUpdated?.Invoke(sessions);
            },
            error =>
            {
                System.Diagnostics.Debug.WriteLine($"Chat Sessions Listener Error: {error.Message}");
            });
    }

    // Deliberately UpdateData (fails on a missing doc) rather than a merge-set: opening an empty
    // chat must not create chats/{driverId} - only the first message does.
    public async Task MarkMessagesAsReadAsync(string driverId)
    {
        try
        {
            await CrossFirebaseFirestore.Current
                .GetCollection("chats")
                .GetDocument(driverId)
                .UpdateDataAsync(new Dictionary<object, object> // Reverted to object, object
                {
                    { "isUnread", false }
                });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error marking messages as read: {ex.Message}");
        }
    }
}

// Local proxy class using mobile-specific Plugin.Firebase attributes
public class ChatMessageProxy
{
    [Plugin.Firebase.Firestore.FirestoreProperty("text")]
    public string Text { get; set; }

    [Plugin.Firebase.Firestore.FirestoreProperty("isDriver")]
    public bool IsDriver { get; set; }

    [Plugin.Firebase.Firestore.FirestoreProperty("timestamp")]
    public DateTime Timestamp { get; set; }
}

// Proxy class required for fetching the Manager's chat list
public class ChatSessionProxy
{
    [Plugin.Firebase.Firestore.FirestoreProperty("driverName")]
    public string DriverName { get; set; }

    [Plugin.Firebase.Firestore.FirestoreProperty("lastMessage")]
    public string LastMessage { get; set; }

    [Plugin.Firebase.Firestore.FirestoreProperty("isUnread")]
    public bool IsUnread { get; set; }

    [Plugin.Firebase.Firestore.FirestoreProperty("timestamp")]
    public DateTime Timestamp { get; set; }
}

public class DriverProfileProxy
{
    [Plugin.Firebase.Firestore.FirestoreProperty("phoneNumber")]
    public string PhoneNumber { get; set; }

    // Map directly to the "fullName" field in your Firestore users collection
    [Plugin.Firebase.Firestore.FirestoreProperty("fullName")]
    public string FullName { get; set; }
}
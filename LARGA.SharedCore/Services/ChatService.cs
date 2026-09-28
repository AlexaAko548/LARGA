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
    Task<bool> SendMessageAsync(string driverId, ChatMessage message);
    IDisposable ListenForMessages(string driverId, Action<IList<ChatMessage>> onMessagesUpdated);
    Task<string> GetDriverPhoneNumberAsync(string driverId);
    IDisposable ListenForChatSessions(Action<IEnumerable<ChatSession>> onSessionsUpdated);
    Task MarkMessagesAsReadAsync(string driverId);
}

public class ChatService : IChatService
{
    public async Task<bool> SendMessageAsync(string driverId, ChatMessage message)
    {
        try
        {
            // 1. Save the actual message to the subcollection (This is succeeding)
            var proxyMessage = new ChatMessageProxy
            {
                Text = message.Text,
                IsDriver = message.IsDriver,
                Timestamp = DateTime.UtcNow
            };

            await CrossFirebaseFirestore.Current
                .GetCollection($"chats/{driverId}/messages")
                .AddDocumentAsync(proxyMessage);

            // 2. Fetch the driver's real name from the users collection using their ID
            // 2. Fetch the driver's real name from the users collection using their ID
            string realDriverName = "Unknown Driver";
            try
            {
                var userDoc = await CrossFirebaseFirestore.Current
                    .GetCollection("users")
                    .GetDocument(driverId)
                    .GetDocumentSnapshotAsync<DriverProfileProxy>();

                if (userDoc != null && userDoc.Data != null && !string.IsNullOrWhiteSpace(userDoc.Data.FullName))
                {
                    realDriverName = userDoc.Data.FullName;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Driver profile fetch error: {ex.Message}");
            }

            // 3. Build the parent session document with the real name
            var session = new ChatSessionProxy
            {
                DriverName = realDriverName,
                LastMessage = message.Text,
                IsUnread = message.IsDriver,
                Timestamp = DateTime.UtcNow
            };

            // 4. Create or overwrite the parent document
            await CrossFirebaseFirestore.Current
                .GetCollection("chats")
                .GetDocument(driverId)
                .SetDataAsync(session);

            return true;
        }
        catch (Exception ex)
        {
            // Prints the exact Firebase rejection reason to the Visual Studio Output window
            System.Diagnostics.Debug.WriteLine($"CRITICAL DATABASE ERROR: {ex.Message}");
            return false;
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
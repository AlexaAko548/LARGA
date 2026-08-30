using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Plugin.Firebase.Firestore;
using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore.Services;

public interface IChatService
{
    Task<bool> SendMessageAsync(string driverId, ChatMessage message);
    IDisposable ListenForMessages(string driverId, Action<IList<ChatMessage>> onMessagesUpdated);
}

public class ChatService : IChatService
{
    public async Task<bool> SendMessageAsync(string driverId, ChatMessage message)
    {
        try
        {
            // Use the mobile-specific proxy class to force Firebase to serialize the fields
            var proxyMessage = new ChatMessageProxy
            {
                Text = message.Text,
                IsDriver = message.IsDriver,
                Timestamp = DateTime.UtcNow
            };

            await CrossFirebaseFirestore.Current
                .GetCollection($"chats/{driverId}/messages")
                .AddDocumentAsync(proxyMessage);

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Chat Send Error: {ex.Message}");
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
                            // FIX: Access Id through the Reference object
                            MessageId = doc.Reference.Id,
                            Text = doc.Data.Text ?? string.Empty,
                            IsDriver = doc.Data.IsDriver,
                            Timestamp = doc.Data.Timestamp
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
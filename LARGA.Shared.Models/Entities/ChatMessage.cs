using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

[FirestoreData]
public class ChatMessage
{
    [FirestoreDocumentId]
    public string MessageId { get; set; } = string.Empty;

    [FirestoreProperty("text")]
    public string Text { get; set; } = string.Empty;

    [FirestoreProperty("isDriver")]
    public bool IsDriver { get; set; }

    [FirestoreProperty("timestamp")]
    public DateTime Timestamp { get; set; }
}
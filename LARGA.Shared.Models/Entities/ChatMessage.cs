using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

[FirestoreData]
public class ChatMessage
{
    /// <summary>senderRole on a message ManagerWeb sends by itself (Garage notices) - not a person.</summary>
    public const string SystemRole = "System";
    public const string SystemSenderId = "system";
    public const string SystemSenderName = "BLM Taxi (automatic)";

    /// <summary>Shown for a staff message sent before messages carried their sender.</summary>
    public const string UnknownStaffLabel = "Manager";

    [FirestoreDocumentId]
    public string MessageId { get; set; } = string.Empty;

    [FirestoreProperty("text")]
    public string Text { get; set; } = string.Empty;

    [FirestoreProperty("isDriver")]
    public bool IsDriver { get; set; }

    [FirestoreProperty("timestamp")]
    public DateTime Timestamp { get; set; }

    // Each driver has one conversation shared by the Manager and every Assistant Manager, so a staff
    // message says who wrote it (users/{uid}, fullName, role at the time). Empty on driver messages
    // and on staff messages written before these existed.
    [FirestoreProperty("senderId")]
    public string? SenderId { get; set; }

    [FirestoreProperty("senderName")]
    public string? SenderName { get; set; }

    [FirestoreProperty("senderRole")]
    public string? SenderRole { get; set; }

    /// <summary>The label over a staff message - "Ana Reyes (Assistant Manager)", "BLM Taxi (automatic)", or
    /// "Manager" for an older message. Empty for the driver's own messages. Not stored.</summary>
    public string SenderLabel => LabelFor(IsDriver, SenderName, SenderRole);

    public bool ShowSender => !IsDriver;

    public static string LabelFor(bool isDriver, string? senderName, string? senderRole)
    {
        if (isDriver)
        {
            return string.Empty;
        }

        if (string.Equals(senderRole, SystemRole, StringComparison.OrdinalIgnoreCase))
        {
            return SystemSenderName;
        }

        string name = senderName?.Trim() ?? string.Empty;
        string role = senderRole?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return role.Length == 0 ? UnknownStaffLabel : role;
        }

        return role.Length == 0 ? name : $"{name} ({role})";
    }
}

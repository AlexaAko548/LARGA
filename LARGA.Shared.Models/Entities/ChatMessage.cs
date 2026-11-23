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

    /// <summary>Sent by whoever is looking at the chat: on the staff side, a staff message whose senderId is
    /// the signed-in user (an older message with no sender isn't anyone's); in the driver app, the
    /// driver's own messages. Set by the screen showing it (<see cref="IsMineFor"/>), not stored.</summary>
    public bool IsMine { get; set; }

    /// <summary>Another staff member's message, seen on the staff side - drawn on the left like the
    /// driver's, in its own colour.</summary>
    public bool IsOtherStaff => !IsDriver && !IsMine;

    public bool ShowSender => !IsDriver && !IsMine;

    /// <summary>Whether this message is the viewer's own: the driver's messages for the driver
    /// (<paramref name="viewerIsDriver"/>), else a staff message sent by <paramref name="viewerUid"/>.</summary>
    public bool IsMineFor(string? viewerUid, bool viewerIsDriver) =>
        viewerIsDriver ? IsDriver : !IsDriver && !string.IsNullOrEmpty(viewerUid) && SenderId == viewerUid;

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

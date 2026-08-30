using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

[FirestoreData]
public class AuditLog
{
    [FirestoreDocumentId]
    public string AuditLogId { get; set; } = string.Empty;

    [FirestoreProperty("userId")]
    public string UserId { get; set; } = string.Empty;

    [FirestoreProperty("actionType")]
    public string ActionType { get; set; } = string.Empty;

    [FirestoreProperty("auditLogDetails")]
    public string AuditLogDetails { get; set; } = string.Empty;

    [FirestoreProperty("ipAddress")]
    public string? IpAddress { get; set; }

    [FirestoreProperty("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
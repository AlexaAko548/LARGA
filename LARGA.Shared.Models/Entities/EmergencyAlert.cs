using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

[FirestoreData]
public class EmergencyAlert
{
    [FirestoreDocumentId]
    public string AlertId { get; set; } = string.Empty;

    [FirestoreProperty("shiftId")]
    public string ShiftId { get; set; } = string.Empty;

    [FirestoreProperty("latitude")]
    public double Latitude { get; set; }

    [FirestoreProperty("longitude")]
    public double Longitude { get; set; }

    [FirestoreProperty("isResolved")]
    public bool IsResolved { get; set; } = false;

    [FirestoreProperty("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
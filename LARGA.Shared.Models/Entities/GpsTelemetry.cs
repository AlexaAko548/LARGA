using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

[FirestoreData]
public class GpsTelemetry
{
    [FirestoreDocumentId]
    public string LogId { get; set; } = string.Empty;

    [FirestoreProperty("shiftId")]
    public string ShiftId { get; set; } = string.Empty;

    [FirestoreProperty("latitude")]
    public double Latitude { get; set; }

    [FirestoreProperty("longitude")]
    public double Longitude { get; set; }

    [FirestoreProperty("speed")]
    public int Speed { get; set; }

    [FirestoreProperty("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
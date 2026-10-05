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

    [FirestoreProperty("driverId")]
    public string DriverId { get; set; } = string.Empty;

    [FirestoreProperty("driverName")]
    public string DriverName { get; set; } = string.Empty;

    [FirestoreProperty("taxiUnit")]
    public string TaxiUnit { get; set; } = string.Empty;

    [FirestoreProperty("latitude")]
    public double Latitude { get; set; }

    [FirestoreProperty("longitude")]
    public double Longitude { get; set; }

    [FirestoreProperty("isResolved")]
    public bool IsResolved { get; set; } = false;

    [FirestoreProperty("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // LAR-82 (ManagerWeb SOS Dispatch). TriggerType: what kind of emergency - "Standard",
    // "Hostile" or "Crash"; empty on alerts sent before the driver app asks (shown as
    // Standard). ResolvedAt: when the manager ticked Resolved.
    public const string Standard = "Standard";
    public const string Hostile = "Hostile";
    public const string Crash = "Crash";

    [FirestoreProperty("triggerType")]
    public string? TriggerType { get; set; }

    [FirestoreProperty("resolvedAt")]
    public DateTime? ResolvedAt { get; set; }
}

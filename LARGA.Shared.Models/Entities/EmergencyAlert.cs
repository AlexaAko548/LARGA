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

    // Reverse-geocoded street address of latitude/longitude (MapTiler), written once by
    // ManagerWeb's SosPushService; addressSource is "maptiler" or "coords" (lookup failed).
    [FirestoreProperty("address")]
    public string? Address { get; set; }

    [FirestoreProperty("addressSource")]
    public string? AddressSource { get; set; }

    // SOS caller validation. The driver app sends the taxi ID, the driver's registered phone
    // (callerPhone) and, when Android exposes it, the SIM's own number (devicePhone).
    // SosPushService checks them against the unit's active shift and the driver's profile and
    // writes callerCheck ("Verified" / "Unverified" / "Rejected" - SosCallerCheck), callerVerified
    // (true only when Verified) and callerVerificationReason; all null = not checked yet. Only a
    // Rejected alert is held back from the push.
    [FirestoreProperty("taxiId")]
    public string? TaxiId { get; set; }

    [FirestoreProperty("callerPhone")]
    public string? CallerPhone { get; set; }

    [FirestoreProperty("devicePhone")]
    public string? DevicePhone { get; set; }

    [FirestoreProperty("callerVerified")]
    public bool? CallerVerified { get; set; }

    [FirestoreProperty("callerCheck")]
    public string? CallerCheck { get; set; }

    [FirestoreProperty("callerVerificationReason")]
    public string? CallerVerificationReason { get; set; }
}

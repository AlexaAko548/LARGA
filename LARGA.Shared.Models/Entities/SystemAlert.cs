using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

/// <summary>
/// A system-generated notification surfaced to managers - both on ManagerWeb's notification
/// bell and the mobile Manager Alert Center. Distinct from EMERGENCY_ALERT (driver-initiated
/// SOS) and MAINTENANCE_RECORD (driver-filed defect reports): this collection is for alerts
/// the *system* raises on its own, starting with "driver has been idle" (see
/// LARGA.SharedCore/Services/AlertService.cs and IdleAlertMonitorService).
/// </summary>
[FirestoreData]
public class SystemAlert
{
    [FirestoreDocumentId]
    public string AlertId { get; set; } = string.Empty;

    /// <summary>"DriverIdle" for now; a plain string (same convention as
    /// MaintenanceRecord.Status/ShiftLog.Status elsewhere) so new alert types don't need a
    /// schema migration.</summary>
    [FirestoreProperty("type")]
    public string Type { get; set; } = string.Empty;

    [FirestoreProperty("driverId")]
    public string DriverId { get; set; } = string.Empty;

    // Denormalized so both ManagerWeb and mobile can render this alert without a second
    // lookup against `users`/`taxis` - acceptable since a driver's name/unit rarely change
    // between when an alert is raised and when it's read.
    [FirestoreProperty("driverName")]
    public string DriverName { get; set; } = string.Empty;

    [FirestoreProperty("taxiId")]
    public string TaxiId { get; set; } = string.Empty;

    [FirestoreProperty("unitLabel")]
    public string UnitLabel { get; set; } = string.Empty;

    [FirestoreProperty("shiftId")]
    public string ShiftId { get; set; } = string.Empty;

    [FirestoreProperty("message")]
    public string Message { get; set; } = string.Empty;

    [FirestoreProperty("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [FirestoreProperty("isRead")]
    public bool IsRead { get; set; }
}

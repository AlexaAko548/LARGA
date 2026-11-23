using Google.Cloud.Firestore;

namespace LARGA.Shared.Models.Entities;

/// <summary>
/// A driver's clock-in held for the manager's evaluation because the pre-shift inspection
/// flagged something (paper Ch. IV, driver clock-in process): a failed checklist item or a
/// reported defect, or fuel below the required half-tank. The driver's phone creates it
/// (status "Pending") and waits; the manager approves - the phone then clocks in as usual - or
/// denies it, escalating the concern to maintenance.
///
/// Collection: clockin_requests.
/// </summary>
[FirestoreData]
public class ClockInRequest
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Denied = "Denied";
    public const string Cancelled = "Cancelled";

    /// <summary>Approved and used: the phone has clocked in (ShiftId is set).</summary>
    public const string ClockedIn = "ClockedIn";

    [FirestoreDocumentId]
    public string RequestId { get; set; } = string.Empty;

    [FirestoreProperty("driverId")]
    public string DriverId { get; set; } = string.Empty;

    [FirestoreProperty("taxiId")]
    public string TaxiId { get; set; } = string.Empty;

    [FirestoreProperty("status")]
    public string Status { get; set; } = Pending;

    /// <summary>Driver-readable reasons, e.g. "Tire Condition failed", "Fuel below half-tank".</summary>
    [FirestoreProperty("flagReasons")]
    public string FlagReasons { get; set; } = string.Empty;

    [FirestoreProperty("tireCondition")]
    public bool TireCondition { get; set; }

    [FirestoreProperty("underTheHood")]
    public bool UnderTheHood { get; set; }

    [FirestoreProperty("lightsCondition")]
    public bool LightsCondition { get; set; }

    [FirestoreProperty("interiorCleanliness")]
    public bool InteriorCleanliness { get; set; }

    [FirestoreProperty("exteriorCondition")]
    public bool ExteriorCondition { get; set; }

    [FirestoreProperty("isBelowHalfTank")]
    public bool IsBelowHalfTank { get; set; }

    [FirestoreProperty("startMileage")]
    public int StartMileage { get; set; }

    [FirestoreProperty("fuelDashboardUrl")]
    public string? FuelDashboardUrl { get; set; }

    [FirestoreProperty("odometerPhotoUrl")]
    public string? OdometerPhotoUrl { get; set; }

    /// <summary>maintenance_logs IDs of defects the driver reported during this inspection,
    /// comma-separated.</summary>
    [FirestoreProperty("defectReportIds")]
    public string DefectReportIds { get; set; } = string.Empty;

    [FirestoreProperty("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [FirestoreProperty("decidedAt")]
    public DateTime? DecidedAt { get; set; }

    [FirestoreProperty("managerNote")]
    public string? ManagerNote { get; set; }

    /// <summary>The shift the phone started after approval.</summary>
    [FirestoreProperty("shiftId")]
    public string? ShiftId { get; set; }
}

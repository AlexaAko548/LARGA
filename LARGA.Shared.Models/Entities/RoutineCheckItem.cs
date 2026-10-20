using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

/// <summary>
/// A scheduled preventive-maintenance trigger for a taxi (Garage page's "Upcoming Routine
/// Checks") - e.g. an oil change due at a certain mileage, or a registration renewal due by
/// a certain date. Distinct from MAINTENANCE_RECORD, which represents an actual repair
/// event (past or in progress); this represents a future thing that hasn't happened yet.
/// Usually one of DueMileage/DueDate is set. A generated oil change for a unit with a mileage
/// interval has both - it's due at whichever comes first.
/// </summary>
[FirestoreData]
public class RoutineCheckItem
{
    [FirestoreDocumentId]
    public string CheckId { get; set; } = string.Empty;

    [FirestoreProperty("taxiId")]
    public string TaxiId { get; set; } = string.Empty;

    [FirestoreProperty("checkName")]
    public string CheckName { get; set; } = string.Empty;

    /// <summary>Absolute odometer reading this check is due at - "due in X km" is computed live as DueMileage - TaxiUnit.CurrentMileage.</summary>
    [FirestoreProperty("dueMileage")]
    public int? DueMileage { get; set; }

    /// <summary>For date-based checks (e.g. registration renewal) instead of mileage-based ones.</summary>
    [FirestoreProperty("dueDate")]
    public DateTime? DueDate { get; set; }

    /// <summary>The recurring type (LARGA.SharedCore.MaintenanceSchedule: WeeklyInspection,
    /// BiweeklyCheckup, MonthlyInspection, OilChange) for checks MaintenanceSchedulerService
    /// generates; null for hand-entered ones.</summary>
    [FirestoreProperty("routineType")]
    public string? RoutineType { get; set; }
}

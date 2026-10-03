using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

#region Enums

public enum MaintenanceType
{
    RoutineCheckup,
    BreakdownRepair,
    AccidentCorrection
}

public enum PriorityLevel
{
    Low,
    Medium,
    High
}

#endregion

#region Firestore Custom Enum Converters

public class MaintenanceTypeConverter : IFirestoreConverter<MaintenanceType>
{
    public object ToFirestore(MaintenanceType value)
    {
        return value switch
        {
            MaintenanceType.BreakdownRepair => "Breakdown Repair",
            MaintenanceType.AccidentCorrection => "Accident Correction",
            _ => "Routine Checkup"
        };
    }

    public MaintenanceType FromFirestore(object? value)
    {
        if (value is string str)
        {
            return str switch
            {
                "Breakdown Repair" or "BreakdownRepair" => MaintenanceType.BreakdownRepair,
                "Accident Correction" or "AccidentCorrection" => MaintenanceType.AccidentCorrection,
                _ => MaintenanceType.RoutineCheckup
            };
        }
        return MaintenanceType.RoutineCheckup;
    }
}

public class PriorityLevelConverter : IFirestoreConverter<PriorityLevel>
{
    public object ToFirestore(PriorityLevel value)
    {
        return value switch
        {
            PriorityLevel.Medium => "Medium",
            PriorityLevel.High => "High",
            _ => "Low"
        };
    }

    public PriorityLevel FromFirestore(object? value)
    {
        if (value is string str)
        {
            return str switch
            {
                "Medium" => PriorityLevel.Medium,
                "High" => PriorityLevel.High,
                _ => PriorityLevel.Low
            };
        }
        return PriorityLevel.Low;
    }
}

#endregion

[FirestoreData]
public class MaintenanceRecord
{
    [FirestoreDocumentId]
    public string MaintenanceId { get; set; } = string.Empty;

    [FirestoreProperty("taxiId")]
    public string TaxiId { get; set; } = string.Empty;

    [FirestoreProperty("managerId")]
    public string ManagerId { get; set; } = string.Empty;

    [FirestoreProperty("shiftId")]
    public string? ShiftId { get; set; }

    [FirestoreProperty("maintenanceType", ConverterType = typeof(MaintenanceTypeConverter))]
    public MaintenanceType MaintenanceType { get; set; } = MaintenanceType.RoutineCheckup;

    [FirestoreProperty("issueTitle")]
    public string IssueTitle { get; set; } = string.Empty;

    [FirestoreProperty("issueDescription")]
    public string IssueDescription { get; set; } = string.Empty;

    [FirestoreProperty("dateLogged")]
    public DateTime DateLogged { get; set; } = DateTime.UtcNow;

    [FirestoreProperty("dateResolved")]
    public DateTime? DateResolved { get; set; }

    [FirestoreProperty("laborCost", ConverterType = typeof(DecimalConverter))]
    public decimal LaborCost { get; set; }

    [FirestoreProperty("totalCost", ConverterType = typeof(DecimalConverter))]
    public decimal TotalCost { get; set; }

    [FirestoreProperty("supportingPhotoUrl")]
    public string? SupportingPhotoUrl { get; set; }

    [FirestoreProperty("priorityLevel", ConverterType = typeof(PriorityLevelConverter))]
    public PriorityLevel PriorityLevel { get; set; } = PriorityLevel.Low;

    // --- Added 2026-09-10 for the Garage / Maintenance Scheduler page ---

    /// <summary>Driver who filed this as a defect report from mobile. Null for a manager- or
    /// system-initiated record (e.g. a routine check turned into a work order).</summary>
    [FirestoreProperty("reportedByDriverId")]
    public string? ReportedByDriverId { get; set; }

    /// <summary>"Reported" (driver flagged it, no ticket yet) / "InProgress" (ticket created,
    /// in the shop) / "Resolved" / "Dismissed". Plain string, same convention as
    /// ShiftLog.Status/TaxiUnit.Status elsewhere in this codebase - not stored redundantly
    /// with DateResolved, since "Dismissed" needs its own state DateResolved can't express.</summary>
    [FirestoreProperty("status")]
    public string Status { get; set; } = "Reported";

    /// <summary>Set when a pending driver report is turned into a work order via "Create Ticket".</summary>
    [FirestoreProperty("mechanicInstructions")]
    public string? MechanicInstructions { get; set; }

    [FirestoreProperty("estimatedCompletionDate")]
    public DateTime? EstimatedCompletionDate { get; set; }
}
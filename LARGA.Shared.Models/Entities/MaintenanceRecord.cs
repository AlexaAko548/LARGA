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

    [FirestoreProperty("laborCost")]
    public decimal LaborCost { get; set; }

    [FirestoreProperty("totalCost")]
    public decimal TotalCost { get; set; }

    [FirestoreProperty("supportingPhotoUrl")]
    public string? SupportingPhotoUrl { get; set; }

    [FirestoreProperty("priorityLevel", ConverterType = typeof(PriorityLevelConverter))]
    public PriorityLevel PriorityLevel { get; set; } = PriorityLevel.Low;
}
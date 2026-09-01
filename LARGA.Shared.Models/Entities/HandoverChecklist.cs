using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

#region Enums

public enum ChecklistType
{
    PreShift,
    EndShift
}

public enum FuelVerification
{
    HalfTank,
    BelowHalfTank
}

#endregion

#region Firestore Custom Enum Converters

public class ChecklistTypeConverter : IFirestoreConverter<ChecklistType>
{
    public object ToFirestore(ChecklistType value)
    {
        return value switch
        {
            ChecklistType.PreShift => "Pre-Shift",
            ChecklistType.EndShift => "End-Shift",
            _ => "Pre-Shift"
        };
    }

    public ChecklistType FromFirestore(object? value)
    {
        if (value is string str)
        {
            return str switch
            {
                "End-Shift" or "EndShift" => ChecklistType.EndShift,
                _ => ChecklistType.PreShift
            };
        }
        return ChecklistType.PreShift;
    }
}

public class FuelVerificationConverter : IFirestoreConverter<FuelVerification>
{
    public object ToFirestore(FuelVerification value)
    {
        return value switch
        {
            FuelVerification.HalfTank => "Half-tank",
            FuelVerification.BelowHalfTank => "Below half-tank",
            _ => "Half-tank"
        };
    }

    public FuelVerification FromFirestore(object? value)
    {
        if (value is string str)
        {
            return str switch
            {
                "Below half-tank" or "BelowHalfTank" => FuelVerification.BelowHalfTank,
                _ => FuelVerification.HalfTank
            };
        }
        return FuelVerification.HalfTank;
    }
}

#endregion

[FirestoreData]
public class HandoverChecklist
{
    [FirestoreDocumentId]
    public string ChecklistId { get; set; } = string.Empty;

    [FirestoreProperty("shiftId")]
    public string ShiftId { get; set; } = string.Empty;

    [FirestoreProperty("checklistType", ConverterType = typeof(ChecklistTypeConverter))]
    public ChecklistType ChecklistType { get; set; } = ChecklistType.PreShift;

    [FirestoreProperty("tireCondition")]
    public bool TireCondition { get; set; }

    [FirestoreProperty("oilLevel")]
    public bool OilLevel { get; set; }

    [FirestoreProperty("coolantLevel")]
    public bool CoolantLevel { get; set; }

    [FirestoreProperty("interiorCleanliness")]
    public bool InteriorCleanliness { get; set; }

    [FirestoreProperty("exteriorScratches")]
    public bool ExteriorScratches { get; set; }

    [FirestoreProperty("fuelVerification", ConverterType = typeof(FuelVerificationConverter))]
    public FuelVerification FuelVerification { get; set; } = FuelVerification.HalfTank;

    [FirestoreProperty("scratchesPhotoUrl")]
    public string? ScratchesPhotoUrl { get; set; }

    [FirestoreProperty("fuelDashboardUrl")]
    public string? FuelDashboardUrl { get; set; }

    [FirestoreProperty("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
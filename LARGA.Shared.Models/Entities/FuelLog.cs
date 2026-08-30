using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

#region Enums

public enum FuelVerificationStatus
{
    Pending,
    Verified,
    Flagged
}

#endregion

#region Firestore Custom Enum Converters

public class FuelVerificationStatusConverter : IFirestoreConverter<FuelVerificationStatus>
{
    public object ToFirestore(FuelVerificationStatus value)
    {
        return value switch
        {
            FuelVerificationStatus.Verified => "Verified",
            FuelVerificationStatus.Flagged => "Flagged",
            _ => "Pending"
        };
    }

    public FuelVerificationStatus FromFirestore(object? value)
    {
        if (value is string str)
        {
            return str switch
            {
                "Verified" => FuelVerificationStatus.Verified,
                "Flagged" => FuelVerificationStatus.Flagged,
                _ => FuelVerificationStatus.Pending
            };
        }
        return FuelVerificationStatus.Pending;
    }
}

#endregion

[FirestoreData]
public class FuelLog
{
    [FirestoreDocumentId]
    public string FuelId { get; set; } = string.Empty;

    [FirestoreProperty("shiftId")]
    public string ShiftId { get; set; } = string.Empty;

    [FirestoreProperty("litersRefueled")]
    public decimal LitersRefueled { get; set; }

    [FirestoreProperty("fuelCost")]
    public decimal FuelCost { get; set; }

    [FirestoreProperty("receiptImageUrl")]
    public string? ReceiptImageUrl { get; set; }

    [FirestoreProperty("verificationStatus", ConverterType = typeof(FuelVerificationStatusConverter))]
    public FuelVerificationStatus VerificationStatus { get; set; } = FuelVerificationStatus.Pending;

    [FirestoreProperty("fuelLogDetails")]
    public string? FuelLogDetails { get; set; }

    [FirestoreProperty("odometerReading")]
    public int OdometerReading { get; set; }

    [FirestoreProperty("odometerPhotoUrl")]
    public string? OdometerPhotoUrl { get; set; }
}
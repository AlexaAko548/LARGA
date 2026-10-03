using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

[FirestoreData]
public class ShiftLog
{
    // Captures the auto-generated Firestore document ID (e.g., 3qHnvbY...)
    [FirestoreDocumentId]
    public string DocumentId { get; set; } = string.Empty;

    [FirestoreProperty("shiftId")]
    public string ShiftId { get; set; } = string.Empty;

    [FirestoreProperty("driverId")]
    public string DriverId { get; set; } = string.Empty;

    [FirestoreProperty("taxiId")]
    public string TaxiId { get; set; } = string.Empty;

    [FirestoreProperty("shiftStart")]
    public DateTime? ShiftStart { get; set; }

    [FirestoreProperty("shiftEnd")]
    public DateTime? ShiftEnd { get; set; }

    [FirestoreProperty("startMileage")]
    public int StartMileage { get; set; }

    [FirestoreProperty("endMileage")]
    public int EndMileage { get; set; }

    [FirestoreProperty("status")]
    public string Status { get; set; } = string.Empty;

    [FirestoreProperty("isOnBreak")]
    public bool IsOnBreak { get; set; }

    [FirestoreProperty("managerNote")]
    public string ManagerNote { get; set; } = string.Empty;

    /// <summary>Late-return fee (₱) worked out at clock-out from ShiftRules. Null when the
    /// shift hasn't ended through a normal clock-out (still active, auto-closed, or older
    /// shifts from before this field existed) - the ledger then falls back to whatever late
    /// fee is on the payment record.</summary>
    [FirestoreProperty("lateFee")]
    public double? LateFee { get; set; }

    /// <summary>Low-fuel penalty set at clock-out when the driver reports below half-tank
    /// (ShiftRules.LowFuelPenalty); 0 when the tank was at least half, null on older shifts.</summary>
    [FirestoreProperty("fuelPenalty")]
    public double? FuelPenalty { get; set; }
}
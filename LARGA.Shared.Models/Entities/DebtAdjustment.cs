using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

/// <summary>
/// A manual correction to what a driver owes - a write-off, a bonus deduction, a fix for a
/// miscounted boundary payment, etc. Deliberately its own document rather than a fake
/// BoundaryPayment: it isn't money a driver handed over, so folding it into BOUNDARY_PAYMENT
/// would misrepresent it as a real collection event. Feeds into the Master Debt Ledger's
/// running balance per driver (see docs/ERD.md).
/// </summary>
[FirestoreData]
public class DebtAdjustment
{
    [FirestoreDocumentId]
    public string AdjustmentId { get; set; } = string.Empty;

    [FirestoreProperty("driverId")]
    public string DriverId { get; set; } = string.Empty;

    /// <summary>Positive = adds to what the driver owes. Negative = a credit/write-off.</summary>
    [FirestoreProperty("amount", ConverterType = typeof(DecimalConverter))]
    public decimal Amount { get; set; }

    [FirestoreProperty("reason")]
    public string Reason { get; set; } = string.Empty;

    [FirestoreProperty("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

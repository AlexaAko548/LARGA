using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

#region Enums

public enum PaymentMethod
{
    Cash,
    EWallet
}

public enum PaymentStatus
{
    Unpaid,
    Partial,
    Paid
}

#endregion

#region Firestore Custom Enum Converters

public class PaymentMethodConverter : IFirestoreConverter<PaymentMethod>
{
    public object ToFirestore(PaymentMethod value)
    {
        return value switch
        {
            PaymentMethod.Cash => "Cash",
            PaymentMethod.EWallet => "E-Wallet",
            _ => "Cash"
        };
    }

    public PaymentMethod FromFirestore(object? value)
    {
        if (value is string str)
        {
            return str switch
            {
                "E-Wallet" or "EWallet" => PaymentMethod.EWallet,
                _ => PaymentMethod.Cash
            };
        }
        return PaymentMethod.Cash;
    }
}

public class PaymentStatusConverter : IFirestoreConverter<PaymentStatus>
{
    public object ToFirestore(PaymentStatus value)
    {
        return value switch
        {
            PaymentStatus.Paid => "Paid",
            PaymentStatus.Partial => "Partial",
            _ => "Unpaid"
        };
    }

    public PaymentStatus FromFirestore(object? value)
    {
        if (value is string str)
        {
            return str switch
            {
                "Paid" => PaymentStatus.Paid,
                "Partial" => PaymentStatus.Partial,
                _ => PaymentStatus.Unpaid
            };
        }
        return PaymentStatus.Unpaid;
    }
}

#endregion

[FirestoreData]
public class BoundaryPayment
{
    [FirestoreDocumentId]
    public string PaymentId { get; set; } = string.Empty;

    [FirestoreProperty("shiftId")]
    public string ShiftId { get; set; } = string.Empty;

    /// <summary>The shift's driver. firestore.rules let a driver read only their own payments,
    /// so every write sets it (older documents are backfilled by ManagerWeb on startup).</summary>
    [FirestoreProperty("driverId")]
    public string? DriverId { get; set; }

    [FirestoreProperty("expectedBoundary", ConverterType = typeof(DecimalConverter))]
    public decimal ExpectedBoundary { get; set; }

    [FirestoreProperty("lateFees", ConverterType = typeof(DecimalConverter))]
    public decimal LateFees { get; set; }

    [FirestoreProperty("fuelPenalty", ConverterType = typeof(DecimalConverter))]
    public decimal FuelPenalty { get; set; }

    [FirestoreProperty("amountPaid", ConverterType = typeof(DecimalConverter))]
    public decimal AmountPaid { get; set; }

    [FirestoreProperty("paymentMethod", ConverterType = typeof(PaymentMethodConverter))]
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;

    [FirestoreProperty("paymentStatus", ConverterType = typeof(PaymentStatusConverter))]
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Unpaid;

    [FirestoreProperty("referenceNumber")]
    public int? ReferenceNumber { get; set; }

    [FirestoreProperty("ePayReceiptPhoto")]
    public string? EPayReceiptPhoto { get; set; }

    /// <summary>The GCash receipt's reference number as printed (13 digits) - text, since it
    /// doesn't fit referenceNumber's int.</summary>
    [FirestoreProperty("receiptReferenceNo")]
    public string? ReceiptReferenceNo { get; set; }

    [FirestoreProperty("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // One document per payment (see SharedCore BoundaryPaymentRules) - the fields the manager
    // app's Quick Ledger writes too.

    /// <summary>Shared by every document written for one handover (a payment split across
    /// several shifts). Empty on old running-total documents.</summary>
    [FirestoreProperty("transactionId")]
    public string? TransactionId { get; set; }

    /// <summary>GCash reference number as the Quick Ledger stores it (see ReceiptReferenceNo).</summary>
    [FirestoreProperty("gcashReferenceNumber")]
    public string? GcashReferenceNumber { get; set; }

    [FirestoreProperty("notes")]
    public string? Notes { get; set; }

    /// <summary>When the payment was recorded, as ISO text - written together with timestamp
    /// and never changed afterwards (older code overwrote timestamp on later payments).</summary>
    [FirestoreProperty("recordedAtUtc")]
    public string? RecordedAtUtc { get; set; }

    /// <summary>recordedAtUtc when present, otherwise timestamp.</summary>
    public DateTime RecordedAt =>
        DateTime.TryParse(RecordedAtUtc, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime at)
            ? at
            : Timestamp;

    /// <summary>Which screen took the payment ("Daily Settlements", "Master Debt Ledger"); not
    /// written by the manager app.</summary>
    [FirestoreProperty("recordedVia")]
    public string? RecordedVia { get; set; }

    /// <summary>Quick Ledger / Financial Ledger target account verification: the GCash number or
    /// bank account the e-receipt was sent to, and how it compared with
    /// system_configs/global.authorizedPayoutAccounts ("Verified", "Mismatch", "NotFound",
    /// "NotConfigured").</summary>
    [FirestoreProperty("receiptTargetAccount")]
    public string? ReceiptTargetAccount { get; set; }

    [FirestoreProperty("targetAccountStatus")]
    public string? TargetAccountStatus { get; set; }
}
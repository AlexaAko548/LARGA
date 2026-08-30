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

    [FirestoreProperty("expectedBoundary")]
    public decimal ExpectedBoundary { get; set; }

    [FirestoreProperty("lateFees")]
    public decimal LateFees { get; set; }

    [FirestoreProperty("amountPaid")]
    public decimal AmountPaid { get; set; }

    [FirestoreProperty("paymentMethod", ConverterType = typeof(PaymentMethodConverter))]
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;

    [FirestoreProperty("paymentStatus", ConverterType = typeof(PaymentStatusConverter))]
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Unpaid;

    [FirestoreProperty("referenceNumber")]
    public int? ReferenceNumber { get; set; }

    [FirestoreProperty("ePayReceiptPhoto")]
    public string? EPayReceiptPhoto { get; set; }

    [FirestoreProperty("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
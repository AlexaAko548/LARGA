using System;
using System.Linq;

namespace LARGA.SharedCore;

/// <summary>
/// BRP-008: a GCash reference number can be recorded against only one payment. Both ledgers (the
/// web's Financial Ledger and the manager app's Quick Ledger) claim payment_references/{normalized
/// reference} in the same transaction that saves the payment's documents - if that document is
/// already there, the payment is refused and nothing is saved. Cash payments carry no reference
/// and are never checked.
/// </summary>
public static class PaymentReferenceRules
{
    public const string Collection = "payment_references";

    public const string DuplicateMessage = "This GCash reference number has already been recorded.";

    /// <summary>The form a reference is compared (and stored as a document ID) in: trimmed, spaces
    /// and dashes removed, upper-case - "1234 567-890" and "1234567890" are the same reference.
    /// A '/' (never in a real reference, but not allowed in a document ID) becomes '_'.
    /// Null when nothing is left.</summary>
    public static string? Normalize(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        string normalized = new string(reference.Trim()
                .Where(c => !char.IsWhiteSpace(c) && c != '-')
                .ToArray())
            .ToUpperInvariant()
            .Replace('/', '_');
        return normalized.Length == 0 || normalized is "." or ".." ? null : normalized;
    }
}

/// <summary>The payment's GCash reference number is already on another payment; nothing was saved.
/// Message is safe to show to the manager.</summary>
public sealed class DuplicatePaymentReferenceException : InvalidOperationException
{
    public DuplicatePaymentReferenceException() : base(PaymentReferenceRules.DuplicateMessage) { }
}

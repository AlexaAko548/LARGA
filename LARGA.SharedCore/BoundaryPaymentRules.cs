using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace LARGA.SharedCore;

/// <summary>
/// How boundary_payments documents are written and added up - shared by ManagerWeb's Financial
/// Ledger, the manager app's Quick Ledger and the driver app's Ledger, so all three agree.
///
/// Every payment is its own new document holding only that payment's amount: an earlier
/// payment is never changed or overwritten. The ID is {shiftKey}_PAY_{yyyyMMddHHmmssfff}
/// (plus _{n} when one payment is split across several shifts in the same instant), and all
/// the documents written for one handover share a transactionId.
///
/// Older data also has "running total" documents ({shiftKey}_PAY, no timestamp) that were
/// overwritten with the shift's whole paid amount, sometimes one under each of the shift's
/// IDs. Those can't be added together without counting the same money twice, so a shift's
/// paid amount is the largest running-total document plus every individual payment document.
/// </summary>
public static class BoundaryPaymentRules
{
    private static readonly Regex StampedIdPattern = new(@"_PAY_\d{17}(?:_\d+)?$", RegexOptions.Compiled);

    /// <summary>One payment (true), or an old running-total document (false).</summary>
    public static bool IsIndividualPayment(string documentId, string? transactionId) =>
        !string.IsNullOrEmpty(transactionId) || StampedIdPattern.IsMatch(documentId ?? string.Empty);

    /// <summary>What a shift has been paid in total across its boundary_payments documents.</summary>
    public static decimal TotalPaid(IEnumerable<(string DocumentId, string? TransactionId, decimal AmountPaid)> documents)
    {
        decimal runningTotal = 0m;
        decimal individual = 0m;
        foreach ((string id, string? txn, decimal amount) in documents)
        {
            if (IsIndividualPayment(id, txn))
            {
                individual += amount;
            }
            else
            {
                runningTotal = Math.Max(runningTotal, amount);
            }
        }
        return runningTotal + individual;
    }

    /// <summary>The key a shift's payment documents are filed under: its shiftId field, or its
    /// document ID when that's empty (the Quick Ledger's convention).</summary>
    public static string ShiftKey(string documentId, string? shiftIdField) =>
        string.IsNullOrEmpty(shiftIdField) ? documentId : shiftIdField;

    /// <summary>A new payment document's ID. <paramref name="index"/> keeps IDs unique when one
    /// handover writes several documents in the same millisecond.</summary>
    public static string NewDocumentId(string shiftKey, DateTime nowUtc, int index = 0)
    {
        string stamp = nowUtc.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        return index == 0 ? $"{shiftKey}_PAY_{stamp}" : $"{shiftKey}_PAY_{stamp}_{index}";
    }

    /// <summary>The ID shared by every document written for one handover.</summary>
    public static string NewTransactionId(string driverId, DateTime nowUtc) =>
        $"{driverId}_TXN_{nowUtc.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)}";

    /// <summary>recordedAtUtc: the same instant as timestamp, as text (the Quick Ledger reads it first).</summary>
    public static string RecordedAtText(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace LARGA.SharedCore.Ledger;

/// <summary>
/// What counts as a "new update" on a driver's Ledger: the red number on the Ledger tab (the mobile app's
/// UnreadBadgeService). A driver's balance changes when
///  - their shift ends (the boundary is added) - the phone knows that one itself, at clock-out;
///  - a manager records a payment (one handover can write several boundary_payments documents - one per shift it
///    paid - all sharing a transactionId, so they count once);
///  - a manager adds a Manual Adjustment / Correction (penalty or credit). The automatic credit a payment
///    creates carries that payment's transactionId and belongs to the payment's update, so it isn't counted twice.
/// An update is unseen while it is newer than the latest one the driver had on screen when they last opened the
/// Ledger ("seen up to"). All times are the documents' own, so the phone's clock can't hide or invent an update.
/// </summary>
public static class LedgerUpdates
{
    /// <summary>One update: a key to tell it apart, and when it happened.</summary>
    public readonly record struct Update(string Key, DateTime AtUtc);

    /// <summary>Payments as updates: one per handover (transactionId), or per document when it has none. Zero-amount
    /// documents and ones with no time are ignored.</summary>
    public static IReadOnlyList<Update> FromPayments(IEnumerable<(string DocumentId, string? TransactionId, decimal Amount, DateTime? AtUtc)> payments) =>
        payments
            .Where(p => p.Amount > 0 && p.AtUtc is not null)
            .GroupBy(p => string.IsNullOrWhiteSpace(p.TransactionId) ? "doc:" + p.DocumentId : "txn:" + p.TransactionId)
            .Select(group => new Update(group.Key, group.Max(p => p.AtUtc!.Value)))
            .ToList();

    /// <summary>Manual adjustments as updates; the automatic credits that come with a payment are left to the payment.</summary>
    public static IReadOnlyList<Update> FromAdjustments(IEnumerable<(string DocumentId, string? TransactionId, DateTime? AtUtc)> adjustments) =>
        adjustments
            .Where(a => string.IsNullOrWhiteSpace(a.TransactionId) && a.AtUtc is not null)
            .Select(a => new Update("adj:" + a.DocumentId, a.AtUtc!.Value))
            .ToList();

    /// <summary>The newest update's time, or null when there are none.</summary>
    public static DateTime? Latest(IEnumerable<Update> updates)
    {
        DateTime? latest = null;
        foreach (Update update in updates)
        {
            if (latest is null || update.AtUtc > latest)
            {
                latest = update.AtUtc;
            }
        }
        return latest;
    }

    /// <summary>
    /// How many updates are newer than <paramref name="seenUpToUtc"/>. Null means nothing has been marked seen on
    /// this phone yet: nothing counts, so old history never shows up as new (the caller then stores
    /// <see cref="Baseline"/>).
    /// </summary>
    public static int CountUnseen(IEnumerable<Update> updates, DateTime? seenUpToUtc) =>
        seenUpToUtc is DateTime seen ? updates.Count(update => update.AtUtc > seen) : 0;

    /// <summary>What to store as "seen up to" the first time a driver's updates are read: the newest one, or the
    /// earliest possible time when there are none (so the first update that ever arrives is new).</summary>
    public static DateTime Baseline(IEnumerable<Update> updates) => Latest(updates) ?? DateTime.MinValue;
}

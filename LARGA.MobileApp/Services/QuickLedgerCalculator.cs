using System;
using System.Collections.Generic;
using System.Linq;
using LARGA.SharedCore;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Pure ledger rules for the manager Quick Ledger. Nothing in here touches Firebase, so the rules stay readable on
/// their own. <see cref="QuickLedgerService"/> does the reads and writes, feeding plain records in and saving the
/// plans produced here.
///
/// A shift can have more than one boundary_payments document (the clock-out row, plus one for each payment). Every
/// rule works from one <see cref="ShiftPaymentState"/> per shift: the total paid across all of its documents, and
/// whether that total covers the expected amount. Full or partial is therefore a property of the shift, not of
/// any one document.
/// </summary>
public static class QuickLedgerCalculator
{
    // Mirrors FinancialLedgerService's default, used when system_configs/global can't be read
    // or has no positive defaultBoundaryRate.
    public const decimal FallbackBoundaryRate = 800m;

    public const string CashMethod = "Cash";
    public const string EWalletMethod = "E-Wallet";

    private const string PaidStatus = "Paid";
    private const string PartialStatus = "Partial";

    /// <param name="philippineToday">The current Philippine calendar day (PhilippineTime.Now).</param>
    public static QuickLedgerResult Build(QuickLedgerInput input, DateTime philippineToday)
    {
        DateTime dayStartUtc = StartOfPhilippineDayUtc(philippineToday);
        DateTime dayEndUtc = dayStartUtc.AddDays(1);
        IReadOnlyList<ShiftPaymentState> states = BuildShiftStates(input);
        var stateByShiftDocId = states.ToDictionary(s => s.Shift.DocumentId);

        // --- Pending & today's totals: every shift that started today --------------------------
        var pending = new List<PendingBoundaryRow>();
        decimal dueToday = 0m;
        decimal collectedToday = 0m;

        foreach (ShiftPaymentState state in states.Where(s => IsWithin(s.Shift.StartUtc, dayStartUtc, dayEndUtc)))
        {
            dueToday += state.Expected;
            collectedToday += state.TotalPaid;

            // Pending is for shifts with nothing paid yet. Once any payment is recorded the shift leaves Pending and is
            // shown in Payment Done Today as Partial or Paid.
            if (state.IsCleared || state.TotalPaid > 0) continue;

            pending.Add(new PendingBoundaryRow(
                ShiftKey: state.ShiftKey,
                DriverId: state.Shift.DriverId,
                DriverName: NameFor(input, state.Shift.DriverId),
                TaxiId: state.Shift.TaxiId,
                TaxiPlate: PlateFor(input, state.Shift.TaxiId),
                ExpectedBoundary: state.ExpectedBoundary,
                LateFees: state.LateFees,
                Paid: state.TotalPaid));
        }

        // --- Payment Done Today: each payment document received today --------------------------
        // Each row shows the payment's own amount. Full or partial is the shift's status, so a second payment on the
        // same shift shows as Paid once the shift is covered.
        // One row per transaction. A payment that was applied across several shifts is saved as one record per shift,
        // all sharing a TransactionId. The row shows the sum of those records, which is the amount that was entered.
        var paymentsDone = input.Payments
            .Where(p => p.AmountPaid > 0 && IsWithin(p.TimestampUtc, dayStartUtc, dayEndUtc))
            .GroupBy(p => string.IsNullOrEmpty(p.TransactionId) ? p.DocumentId : p.TransactionId)
            .Select(group =>
            {
                BoundaryPaymentRecord first = group.First();
                ShiftPaymentState? state = ShiftStateFor(input, stateByShiftDocId, first.ShiftId);
                return new PaymentDoneRow(
                    ShiftId: first.ShiftId,
                    DriverName: NameFor(input, state?.Shift.DriverId ?? string.Empty),
                    Expected: state?.Expected ?? ExpectedFor(first, input.DefaultBoundaryRate),
                    AmountPaid: group.Sum(p => p.AmountPaid),
                    IsCleared: state?.IsCleared ?? false,
                    TimestampUtc: group.Max(p => p.TimestampUtc) ?? DateTime.MinValue,
                    PaymentMethod: first.PaymentMethod);
            })
            .OrderByDescending(r => r.TimestampUtc)
            .ToList();

        // --- Other Payment: what each driver still owes from earlier shifts + debt adjustments ---
        var owedByDriver = new Dictionary<string, decimal>();
        foreach (ShiftPaymentState state in EarlierOutstanding(states, dayStartUtc))
        {
            AddTo(owedByDriver, state.Shift.DriverId, state.Remaining);
        }
        foreach (DebtAdjustmentRecord adjustment in input.Adjustments)
        {
            if (string.IsNullOrEmpty(adjustment.DriverId)) continue;
            AddTo(owedByDriver, adjustment.DriverId, adjustment.Amount);
        }

        var otherPayments = owedByDriver
            .Where(kv => kv.Value > 0)
            .Select(kv => new OtherPaymentRow(kv.Key, NameFor(input, kv.Key), kv.Value))
            .OrderByDescending(r => r.AmountOwed)
            .ThenBy(r => r.DriverName)
            .ToList();

        return new QuickLedgerResult(
            Pending: pending.OrderBy(r => r.DriverName).ToList(),
            PaymentsDone: paymentsDone,
            OtherPayments: otherPayments,
            DueToday: dueToday,
            CollectedToday: collectedToday);
    }

    /// <summary>
    /// Plans the write for one payment against a pending shift. Every payment is a new document holding only its own
    /// amount, so each one shows up separately. The shift's status (Paid or Partial) is decided from the shift total.
    /// </summary>
    public static BoundaryPaymentWrite PlanBoundaryPayment(
        PendingBoundaryRow row,
        decimal amountReceived,
        string paymentMethod,
        PaymentEvidence? evidence,
        DateTime nowUtc)
    {
        decimal shiftTotal = row.Paid + amountReceived;

        string documentId = PaymentDocumentId(row.ShiftKey, nowUtc, 0);

        return new BoundaryPaymentWrite(
            DocumentId: documentId,
            IsNew: true,
            ShiftId: row.ShiftKey,
            ExpectedBoundary: row.ExpectedBoundary,
            LateFees: row.LateFees,
            AmountPaid: amountReceived,
            PaymentStatus: StatusFor(shiftTotal, row.Expected),
            PaymentMethod: paymentMethod,
            TransactionId: documentId,
            Evidence: evidence);
    }

    /// <summary>
    /// A new payment document's ID: {shiftKey}_PAY_{yyyyMMddHHmmssfff}, the convention the earlier design notes used.
    /// The index keeps IDs unique when two payments are written in the same millisecond.
    /// </summary>
    private static string PaymentDocumentId(string shiftKey, DateTime nowUtc, int index)
    {
        string stamp = nowUtc.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        return index == 0 ? $"{shiftKey}_PAY_{stamp}" : $"{shiftKey}_PAY_{stamp}_{index}";
    }

    /// <summary>
    /// Plans a lump-sum settlement against a driver's "Other Payment" amount. Applies the money to the oldest unpaid
    /// earlier shift first, then to positive manual debt adjustments. Anything left over is returned as Unallocated,
    /// so the caller can refuse an overpayment.
    /// </summary>
    public static DebtSettlementPlan PlanDebtSettlement(
        QuickLedgerInput input,
        DateTime philippineToday,
        string driverId,
        decimal amountReceived,
        string paymentMethod,
        PaymentEvidence? evidence,
        DateTime nowUtc)
    {
        DateTime dayStartUtc = StartOfPhilippineDayUtc(philippineToday);
        IReadOnlyList<ShiftPaymentState> states = BuildShiftStates(input);

        string transactionId = $"{driverId}_TXN_{nowUtc:yyyyMMddHHmmssfff}";
        var updates = new List<BoundaryPaymentWrite>();
        decimal remaining = amountReceived;

        foreach (ShiftPaymentState state in EarlierOutstanding(states, dayStartUtc)
                     .Where(s => s.Shift.DriverId == driverId)
                     .OrderBy(s => s.Shift.StartUtc))
        {
            if (remaining <= 0) break;

            decimal apply = Math.Min(remaining, state.Remaining);
            if (apply <= 0) continue;

            // Each allocation is its own payment document, one per shift, all sharing the settlement's transaction ID.
            updates.Add(new BoundaryPaymentWrite(
                DocumentId: PaymentDocumentId(state.ShiftKey, nowUtc, updates.Count),
                IsNew: true,
                ShiftId: state.ShiftKey,
                ExpectedBoundary: state.ExpectedBoundary,
                LateFees: state.LateFees,
                AmountPaid: apply,
                PaymentStatus: StatusFor(state.TotalPaid + apply, state.Expected),
                PaymentMethod: paymentMethod,
                TransactionId: transactionId,
                Evidence: evidence));

            remaining -= apply;
        }

        decimal adjustmentDebt = Math.Max(0, input.Adjustments
            .Where(a => a.DriverId == driverId)
            .Sum(a => a.Amount));

        decimal credit = 0m;
        if (remaining > 0 && adjustmentDebt > 0)
        {
            credit = Math.Min(remaining, adjustmentDebt);
            remaining -= credit;
        }

        return new DebtSettlementPlan(updates, credit, Math.Max(0, remaining), evidence);
    }

    /// <summary>What the manager may collect from a driver under "Other Payment": earlier unpaid boundaries plus net
    /// manual debt, floored at zero. Matches the amount shown on the page.</summary>
    public static decimal TotalOwedBy(QuickLedgerInput input, DateTime philippineToday, string driverId)
    {
        DateTime dayStartUtc = StartOfPhilippineDayUtc(philippineToday);
        IReadOnlyList<ShiftPaymentState> states = BuildShiftStates(input);

        decimal boundaries = EarlierOutstanding(states, dayStartUtc)
            .Where(s => s.Shift.DriverId == driverId)
            .Sum(s => s.Remaining);
        decimal adjustments = input.Adjustments.Where(a => a.DriverId == driverId).Sum(a => a.Amount);
        return Math.Max(0, boundaries + adjustments);
    }

    // ---------------------------------------------------------------------
    // Per-shift state
    // ---------------------------------------------------------------------

    /// <summary>
    /// Builds one state per shift. The target is the shift's most recently recorded payment document. New payments
    /// are added to it, so the target's own amount is what gets updated.
    /// </summary>
    private static IReadOnlyList<ShiftPaymentState> BuildShiftStates(QuickLedgerInput input)
    {
        var paymentsByShiftDocId = new Dictionary<string, List<BoundaryPaymentRecord>>();
        var lookups = new Lookups(input);

        foreach (BoundaryPaymentRecord payment in input.Payments)
        {
            ShiftRecord? shift = lookups.ShiftFor(payment.ShiftId);
            if (shift is null) continue;

            if (!paymentsByShiftDocId.TryGetValue(shift.DocumentId, out List<BoundaryPaymentRecord>? list))
            {
                list = new List<BoundaryPaymentRecord>();
                paymentsByShiftDocId[shift.DocumentId] = list;
            }
            list.Add(payment);
        }

        var states = new List<ShiftPaymentState>();
        foreach (ShiftRecord shift in input.Shifts)
        {
            paymentsByShiftDocId.TryGetValue(shift.DocumentId, out List<BoundaryPaymentRecord>? payments);
            payments ??= new List<BoundaryPaymentRecord>();

            BoundaryPaymentRecord? target = payments
                .OrderByDescending(p => p.TimestampUtc ?? DateTime.MinValue)
                .FirstOrDefault();

            // The expected amount comes from the target if it has one, else the default rate.
            decimal expectedBoundary = target is not null && target.ExpectedBoundary > 0
                ? target.ExpectedBoundary
                : input.DefaultBoundaryRate;
            decimal lateFees = target?.LateFees ?? 0m;
            decimal totalPaid = payments.Sum(p => p.AmountPaid);

            states.Add(new ShiftPaymentState(
                Shift: shift,
                ShiftKey: string.IsNullOrEmpty(shift.ShiftId) ? shift.DocumentId : shift.ShiftId,
                ExpectedBoundary: expectedBoundary,
                LateFees: lateFees,
                TotalPaid: totalPaid,
                Target: target,
                HasPaymentRecord: payments.Count > 0));
        }

        return states;
    }

    // Unpaid balances from shifts that started before today, and that have a payment record. Today's shortfalls belong
    // in Pending. Shifts with no record at all are left out, matching the web's debt ledger.
    private static IEnumerable<ShiftPaymentState> EarlierOutstanding(IReadOnlyList<ShiftPaymentState> states, DateTime dayStartUtc) =>
        states.Where(s =>
            s.HasPaymentRecord
            && s.Shift.StartUtc is DateTime start && start < dayStartUtc
            && s.Remaining > 0);

    private static ShiftPaymentState? ShiftStateFor(
        QuickLedgerInput input,
        IReadOnlyDictionary<string, ShiftPaymentState> stateByShiftDocId,
        string shiftKey)
    {
        ShiftRecord? shift = new Lookups(input).ShiftFor(shiftKey);
        return shift is null ? null : stateByShiftDocId.GetValueOrDefault(shift.DocumentId);
    }

    // The plate number from the taxi document. A taxi with no plate set, or no taxi document, shows its ID instead.
    private static string PlateFor(QuickLedgerInput input, string taxiId)
    {
        if (string.IsNullOrEmpty(taxiId)) return "No taxi assigned";
        string? plate = input.TaxiPlates.GetValueOrDefault(taxiId);
        return string.IsNullOrWhiteSpace(plate) ? taxiId : plate;
    }

    private static string NameFor(QuickLedgerInput input, string driverId) =>
        string.IsNullOrEmpty(driverId)
            ? "Unknown driver"
            : input.DriverNames.GetValueOrDefault(driverId) ?? driverId;

    private static DateTime StartOfPhilippineDayUtc(DateTime philippineToday) =>
        philippineToday.Date - PhilippineTime.Offset;

    private static decimal ExpectedFor(BoundaryPaymentRecord payment, decimal defaultRate) =>
        payment.ExpectedBoundary > 0 ? payment.ExpectedBoundary + payment.LateFees : defaultRate;

    private static string StatusFor(decimal paid, decimal expected) =>
        paid >= expected ? PaidStatus : PartialStatus;

    private static bool IsWithin(DateTime? value, DateTime startUtc, DateTime endUtc) =>
        value is DateTime v && v >= startUtc && v < endUtc;

    private static void AddTo(Dictionary<string, decimal> totals, string key, decimal amount) =>
        totals[key] = totals.GetValueOrDefault(key) + amount;

    // Shift lookups. Shifts are matched on either the business ID or the Firestore document ID, because clock-out can
    // store either one in a payment's shiftId.
    private sealed class Lookups
    {
        private readonly Dictionary<string, ShiftRecord> _shiftByKey = new();

        public Lookups(QuickLedgerInput input)
        {
            foreach (ShiftRecord shift in input.Shifts)
            {
                if (!string.IsNullOrEmpty(shift.ShiftId)) _shiftByKey.TryAdd(shift.ShiftId, shift);
                if (!string.IsNullOrEmpty(shift.DocumentId)) _shiftByKey.TryAdd(shift.DocumentId, shift);
            }
        }

        public ShiftRecord? ShiftFor(string shiftKey) =>
            string.IsNullOrEmpty(shiftKey) ? null : _shiftByKey.GetValueOrDefault(shiftKey);
    }
}

/// <summary>Everything the rules need about one shift's boundary: the total paid across its documents and the
/// document new payments go into.</summary>
public sealed record ShiftPaymentState(
    ShiftRecord Shift,
    string ShiftKey,
    decimal ExpectedBoundary,
    decimal LateFees,
    decimal TotalPaid,
    BoundaryPaymentRecord? Target,
    bool HasPaymentRecord)
{
    public decimal Expected => ExpectedBoundary + LateFees;
    public decimal Remaining => Math.Max(0, Expected - TotalPaid);
    public bool IsCleared => Expected > 0 && TotalPaid >= Expected;
}

// ---------------------------------------------------------------------
// Inputs: plain copies of the Firestore documents, so the rules above never see Firebase types.
// ---------------------------------------------------------------------

public sealed record ShiftRecord(string DocumentId, string ShiftId, string DriverId, string TaxiId, DateTime? StartUtc);

public sealed record BoundaryPaymentRecord(
    string DocumentId,
    string ShiftId,
    decimal ExpectedBoundary,
    decimal LateFees,
    decimal AmountPaid,
    string PaymentStatus,
    string PaymentMethod,
    DateTime? TimestampUtc,
    string TransactionId);

public sealed record DebtAdjustmentRecord(string DriverId, decimal Amount);

public sealed record QuickLedgerInput(
    IReadOnlyList<ShiftRecord> Shifts,
    IReadOnlyList<BoundaryPaymentRecord> Payments,
    IReadOnlyList<DebtAdjustmentRecord> Adjustments,
    IReadOnlyDictionary<string, string> DriverNames,
    IReadOnlyDictionary<string, string> TaxiPlates,
    decimal DefaultBoundaryRate);

// ---------------------------------------------------------------------
// Outputs: one row per thing the page shows.
// ---------------------------------------------------------------------

/// <summary>A shift that still owes its boundary today. Paid is the total of all its payment documents.</summary>
public sealed record PendingBoundaryRow(
    string ShiftKey,
    string DriverId,
    string DriverName,
    string TaxiId,
    string TaxiPlate,
    decimal ExpectedBoundary,
    decimal LateFees,
    decimal Paid)
{
    public decimal Expected => ExpectedBoundary + LateFees;
    public decimal Remaining => Math.Max(0, Expected - Paid);
}

/// <summary>One payment received today. AmountPaid is this payment's own amount. IsCleared is the shift's status.</summary>
public sealed record PaymentDoneRow(
    string ShiftId,
    string DriverName,
    decimal Expected,
    decimal AmountPaid,
    bool IsCleared,
    DateTime TimestampUtc,
    string PaymentMethod);

public sealed record OtherPaymentRow(string DriverId, string DriverName, decimal AmountOwed);

public sealed record QuickLedgerResult(
    IReadOnlyList<PendingBoundaryRow> Pending,
    IReadOnlyList<PaymentDoneRow> PaymentsDone,
    IReadOnlyList<OtherPaymentRow> OtherPayments,
    decimal DueToday,
    decimal CollectedToday);

// ---------------------------------------------------------------------
// Write plans: what to save. The service turns these into Firestore writes.
// ---------------------------------------------------------------------

/// <summary>One boundary_payments document to create or update. AmountPaid is that document's own new amount.</summary>
public sealed record BoundaryPaymentWrite(
    string DocumentId,
    bool IsNew,
    string ShiftId,
    decimal ExpectedBoundary,
    decimal LateFees,
    decimal AmountPaid,
    string PaymentStatus,
    string PaymentMethod,
    string TransactionId,
    PaymentEvidence? Evidence);

/// <summary>
/// What backs a payment, kept for audit: the manager's note, and for E-Wallet the GCash receipt details and the photo
/// stored in Firebase Storage. Every payment document from one settlement carries the same evidence.
/// </summary>
public sealed record PaymentEvidence(
    string? Notes,
    string? ReferenceNumber,
    decimal? ReceiptAmount,
    DateTime? ReceiptDate,
    string? ReceiptPhotoUrl);

public sealed record DebtSettlementPlan(
    IReadOnlyList<BoundaryPaymentWrite> PaymentUpdates,
    decimal AdjustmentCredit,
    decimal Unallocated,
    PaymentEvidence? Evidence);

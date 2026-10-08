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
    // Used when system_configs/global can't be read or has no positive defaultBoundaryRate.
    public const decimal FallbackBoundaryRate = ShiftRules.DefaultBoundaryRate;

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
        CreditView credit = ApplyOverpaymentCredit(states);

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

            // Fully covered by the driver's overpayment credit: the web shows it Cleared too.
            if (state.IsCharge && !credit.OwedByShift.ContainsKey(state.Shift.DocumentId)) continue;

            pending.Add(new PendingBoundaryRow(
                ShiftKey: state.ShiftKey,
                DriverId: state.Shift.DriverId,
                DriverName: NameFor(input, state.Shift.DriverId),
                TaxiId: state.Shift.TaxiId,
                TaxiPlate: PlateFor(input, state.Shift.TaxiId),
                ExpectedBoundary: state.ExpectedBoundary,
                LateFees: state.LateFees,
                FuelPenalty: state.FuelPenalty,
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

        // --- Other Payment: every driver in users, with what each still owes from earlier shifts + debt adjustments.
        // Drivers with no debt are listed too (AmountOwed 0), so a manager can take money from any driver. Debtors
        // come first.
        Dictionary<string, decimal> owedByDriver = OtherOwedByDriver(input, states, credit, dayStartUtc);
        var otherPayments = input.DriverIds
            .Select(driverId => new OtherPaymentRow(driverId, NameFor(input, driverId), owedByDriver.GetValueOrDefault(driverId)))
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
    /// Plans a payment received for a pending shift's boundary (Record Payment). Same allocation as the web's
    /// FinancialLedgerService.RecordPaymentAsync: this shift's balance first, then the driver's older unpaid shifts
    /// (oldest first), then their manual debt, and anything above all of that is kept on this shift as advance credit.
    /// </summary>
    public static PaymentPlan PlanBoundaryPayment(
        QuickLedgerInput input,
        PendingBoundaryRow row,
        decimal amountReceived,
        string paymentMethod,
        PaymentEvidence? evidence,
        DateTime nowUtc)
    {
        IReadOnlyList<ShiftPaymentState> states = BuildShiftStates(input);
        ShiftRecord? shift = new Lookups(input).ShiftFor(row.ShiftKey);
        ShiftPaymentState? first = shift is null ? null : states.FirstOrDefault(s => s.Shift.DocumentId == shift.DocumentId);

        return Allocate(input, states, row.DriverId, first, advanceTarget: first, amountReceived, paymentMethod, evidence, nowUtc);
    }

    /// <summary>
    /// Plans a lump-sum payment against a driver's debt (Record Other Payment). Same allocation as the web's
    /// FinancialLedgerService.SettleDebtAsync: the oldest unpaid shift first, then manual debt, and anything above all
    /// of it is kept as advance credit on the driver's latest shift. With no shift to hold it, the extra is returned as
    /// Unallocated so the caller can refuse it.
    /// </summary>
    public static PaymentPlan PlanDebtSettlement(
        QuickLedgerInput input,
        string driverId,
        decimal amountReceived,
        string paymentMethod,
        PaymentEvidence? evidence,
        DateTime nowUtc)
    {
        IReadOnlyList<ShiftPaymentState> states = BuildShiftStates(input);
        ShiftPaymentState? latest = states
            .Where(s => s.IsCharge && s.Shift.DriverId == driverId)
            .OrderByDescending(s => s.Shift.StartUtc ?? DateTime.MinValue)
            .FirstOrDefault();

        return Allocate(input, states, driverId, first: null, advanceTarget: latest, amountReceived, paymentMethod, evidence, nowUtc);
    }

    // The web's BookPaymentAsync, step for step. Every part becomes a new payment document holding only that part's
    // amount, all sharing one transactionId; money for manual debt becomes an automatic credit adjustment.
    private static PaymentPlan Allocate(
        QuickLedgerInput input,
        IReadOnlyList<ShiftPaymentState> states,
        string driverId,
        ShiftPaymentState? first,
        ShiftPaymentState? advanceTarget,
        decimal amount,
        string paymentMethod,
        PaymentEvidence? evidence,
        DateTime nowUtc)
    {
        var applies = new List<(ShiftPaymentState State, decimal Amount)>();
        decimal remaining = amount;

        decimal toFirst = first is null ? 0m : Math.Min(remaining, first.Remaining);
        if (toFirst > 0)
        {
            applies.Add((first!, toFirst));
            remaining -= toFirst;
        }

        // The driver's other unpaid shifts, oldest first, at the balance their records show.
        decimal toOthers = 0m;
        foreach (ShiftPaymentState state in states
                     .Where(s => s.IsCharge && s.Shift.DriverId == driverId && s != first && s.Remaining > 0)
                     .OrderBy(s => s.Shift.StartUtc ?? DateTime.MinValue))
        {
            if (remaining <= 0) break;

            decimal apply = Math.Min(remaining, state.Remaining);
            applies.Add((state, apply));
            toOthers += apply;
            remaining -= apply;
        }

        decimal adjustmentDebt = Math.Max(0, input.Adjustments.Where(a => a.DriverId == driverId).Sum(a => a.Amount));
        decimal toAdjustments = Math.Min(remaining, adjustmentDebt);
        remaining -= toAdjustments;

        // What's left is advance credit, paid onto the target above its total; the ledger then applies it to the
        // driver's next unpaid boundary (see ApplyOverpaymentCredit).
        decimal advance = advanceTarget is null ? 0m : remaining;
        if (advance > 0)
        {
            int at = applies.FindIndex(a => a.State == advanceTarget);
            if (at >= 0) applies[at] = (advanceTarget!, applies[at].Amount + advance);
            else applies.Add((advanceTarget!, advance));
            remaining -= advance;
        }

        string transactionId = BoundaryPaymentRules.NewTransactionId(driverId, nowUtc);
        var writes = new List<BoundaryPaymentWrite>();
        foreach ((ShiftPaymentState state, decimal apply) in applies)
        {
            writes.Add(new BoundaryPaymentWrite(
                DocumentId: BoundaryPaymentRules.NewDocumentId(state.ShiftKey, nowUtc, writes.Count),
                ShiftId: state.ShiftKey,
                DriverId: state.Shift.DriverId,
                ExpectedBoundary: state.ExpectedBoundary,
                LateFees: state.LateFees,
                FuelPenalty: state.FuelPenalty,
                AmountPaid: apply,
                PaymentStatus: StatusFor(state.TotalPaid + apply, state.Expected),
                PaymentMethod: paymentMethod,
                TransactionId: transactionId,
                Evidence: evidence));
        }

        return new PaymentPlan(writes, transactionId, toFirst, toOthers, toAdjustments, advance, Math.Max(0, remaining), evidence);
    }

    /// <summary>What the manager may collect from a driver under "Other Payment": earlier unpaid boundaries (after
    /// overpayment credit) plus net manual debt, floored at zero. Matches the amount shown on the page.</summary>
    public static decimal TotalOwedBy(QuickLedgerInput input, DateTime philippineToday, string driverId)
    {
        DateTime dayStartUtc = StartOfPhilippineDayUtc(philippineToday);
        IReadOnlyList<ShiftPaymentState> states = BuildShiftStates(input);
        return OtherOwedByDriver(input, states, ApplyOverpaymentCredit(states), dayStartUtc).GetValueOrDefault(driverId);
    }

    // ---------------------------------------------------------------------
    // Debt totals - same rules as the web's Master Debt Ledger (FinancialLedgerService.OwedAfterCredit /
    // TotalOwed), so the phone and the web show the same number.
    // ---------------------------------------------------------------------

    /// <summary>Per shift: what it still owes once the driver's overpayment credit is used up (shifts that owe
    /// nothing are left out). Per driver: credit left over after every shift is covered.</summary>
    private sealed record CreditView(Dictionary<string, decimal> OwedByShift, Dictionary<string, decimal> LeftoverByDriver);

    // Money paid above a shift's total isn't lost: it pays off the driver's oldest unpaid shifts first.
    private static CreditView ApplyOverpaymentCredit(IReadOnlyList<ShiftPaymentState> states)
    {
        var owedByShift = new Dictionary<string, decimal>();
        var leftoverByDriver = new Dictionary<string, decimal>();

        foreach (IGrouping<string, ShiftPaymentState> driver in states.Where(s => s.IsCharge).GroupBy(s => s.Shift.DriverId))
        {
            decimal credit = driver.Sum(s => s.Overpaid);
            foreach (ShiftPaymentState state in driver.Where(s => s.Remaining > 0).OrderBy(s => s.Shift.StartUtc ?? DateTime.MinValue))
            {
                decimal useCredit = Math.Min(credit, state.Remaining);
                credit -= useCredit;
                if (state.Remaining - useCredit > 0)
                {
                    owedByShift[state.Shift.DocumentId] = state.Remaining - useCredit;
                }
            }
            leftoverByDriver[driver.Key] = credit;
        }

        return new CreditView(owedByShift, leftoverByDriver);
    }

    // Earlier shifts' balances after credit, plus net manual adjustments, minus credit left over; floored at zero.
    // Together with today's Pending balances this equals the web's TotalDebt for the driver.
    private static Dictionary<string, decimal> OtherOwedByDriver(
        QuickLedgerInput input, IReadOnlyList<ShiftPaymentState> states, CreditView credit, DateTime dayStartUtc)
    {
        var owedByDriver = new Dictionary<string, decimal>();
        foreach (ShiftPaymentState state in EarlierOutstanding(states, dayStartUtc))
        {
            AddTo(owedByDriver, state.Shift.DriverId, credit.OwedByShift.GetValueOrDefault(state.Shift.DocumentId));
        }
        foreach (DebtAdjustmentRecord adjustment in input.Adjustments)
        {
            if (string.IsNullOrEmpty(adjustment.DriverId)) continue;
            AddTo(owedByDriver, adjustment.DriverId, adjustment.Amount);
        }
        foreach ((string driverId, decimal leftover) in credit.LeftoverByDriver)
        {
            if (leftover > 0) AddTo(owedByDriver, driverId, -leftover);
        }

        return owedByDriver.ToDictionary(kv => kv.Key, kv => Math.Max(0, kv.Value));
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

            // The expected boundary comes from the target if it has one, else the default rate. Late fee and fuel
            // penalty come from the shift itself when clock-out set them, else from the target - the web's ExtrasFor.
            decimal expectedBoundary = target is not null && target.ExpectedBoundary > 0
                ? target.ExpectedBoundary
                : input.DefaultBoundaryRate;
            decimal lateFees = ShiftRules.EffectiveLateFee(shift.LateFee, shift.StartUtc, shift.EndUtc) ?? target?.LateFees ?? 0m;
            decimal fuelPenalty = shift.FuelPenalty ?? target?.FuelPenalty ?? 0m;

            // Added up the same way as the web, so old running-total documents aren't counted twice.
            decimal totalPaid = BoundaryPaymentRules.TotalPaid(
                payments.Select(p => (p.DocumentId, (string?)p.TransactionId, p.AmountPaid)));

            states.Add(new ShiftPaymentState(
                Shift: shift,
                ShiftKey: BoundaryPaymentRules.ShiftKey(shift.DocumentId, shift.ShiftId),
                ExpectedBoundary: expectedBoundary,
                LateFees: lateFees,
                FuelPenalty: fuelPenalty,
                TotalPaid: totalPaid,
                Target: target,
                HasPaymentRecord: payments.Count > 0));
        }

        return states;
    }

    // Unpaid balances from shifts that started before today (or have no start time). Today's shortfalls belong in
    // Pending. An ended shift with no payment record still owes its full amount, matching the web's debt ledger.
    private static IEnumerable<ShiftPaymentState> EarlierOutstanding(IReadOnlyList<ShiftPaymentState> states, DateTime dayStartUtc) =>
        states.Where(s =>
            s.IsCharge
            && (s.Shift.StartUtc is not DateTime start || start < dayStartUtc)
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
        (payment.ExpectedBoundary > 0 ? payment.ExpectedBoundary : defaultRate) + payment.LateFees + payment.FuelPenalty;

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
/// <remarks>Mirrors the web's ShiftCharge (FinancialLedgerService), so both apps agree on every shift.</remarks>
public sealed record ShiftPaymentState(
    ShiftRecord Shift,
    string ShiftKey,
    decimal ExpectedBoundary,
    decimal LateFees,
    decimal FuelPenalty,
    decimal TotalPaid,
    BoundaryPaymentRecord? Target,
    bool HasPaymentRecord)
{
    public decimal Expected => ExpectedBoundary + LateFees + FuelPenalty;

    /// <summary>The most recent document says the shift is paid (e.g. a manager marked it so).</summary>
    public bool IsMarkedPaid => string.Equals(Target?.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase);

    public decimal Remaining => IsMarkedPaid ? 0 : Math.Max(0, Expected - TotalPaid);

    /// <summary>Paid above the shift's total - credit toward the driver's other shifts.</summary>
    public decimal Overpaid => HasPaymentRecord ? Math.Max(0, TotalPaid - Expected) : 0;

    public bool IsCleared => IsMarkedPaid || (TotalPaid > 0 && TotalPaid >= Expected);

    /// <summary>Counts toward the driver's debt: anything already paid on, or an ended shift with a driver. A shift
    /// still Active isn't owed yet (the boundary is paid at the end of the day). Same as the web's BuildCharges.</summary>
    public bool IsCharge => HasPaymentRecord
        || (!string.IsNullOrWhiteSpace(Shift.DriverId) && !string.Equals(Shift.Status, "Active", StringComparison.OrdinalIgnoreCase));
}

// ---------------------------------------------------------------------
// Inputs: plain copies of the Firestore documents, so the rules above never see Firebase types.
// ---------------------------------------------------------------------

/// <param name="LateFee">shifts.lateFee, stored by older app versions or a manager; null when the shift has none
/// (the fee is then computed from StartUtc/EndUtc - ShiftRules.EffectiveLateFee).</param>
/// <param name="FuelPenalty">shifts.fuelPenalty, set at clock-out; null when the shift has none.</param>
/// <param name="EndUtc">shifts.shiftEnd (server-stamped at clock-out); null while the shift is open.</param>
public sealed record ShiftRecord(
    string DocumentId,
    string ShiftId,
    string DriverId,
    string TaxiId,
    DateTime? StartUtc,
    string Status,
    decimal? LateFee,
    decimal? FuelPenalty,
    DateTime? EndUtc = null);

/// <param name="TransactionId">Empty on older records, which have none.</param>
public sealed record BoundaryPaymentRecord(
    string DocumentId,
    string ShiftId,
    decimal ExpectedBoundary,
    decimal LateFees,
    decimal FuelPenalty,
    decimal AmountPaid,
    string PaymentStatus,
    string PaymentMethod,
    DateTime? TimestampUtc,
    string TransactionId);

public sealed record DebtAdjustmentRecord(string DriverId, decimal Amount);

/// <param name="DriverNames">Full name by user ID, for every user document.</param>
/// <param name="DriverIds">User IDs with role "Driver". The Other Payment list is built from these, so it matches the web's roster.</param>
public sealed record QuickLedgerInput(
    IReadOnlyList<ShiftRecord> Shifts,
    IReadOnlyList<BoundaryPaymentRecord> Payments,
    IReadOnlyList<DebtAdjustmentRecord> Adjustments,
    IReadOnlyDictionary<string, string> DriverNames,
    IReadOnlyList<string> DriverIds,
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
    decimal FuelPenalty,
    decimal Paid)
{
    public decimal Expected => ExpectedBoundary + LateFees + FuelPenalty;
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

/// <param name="OtherPayments">Every driver, debtors first. AmountOwed is 0 for a driver who owes nothing.</param>
public sealed record QuickLedgerResult(
    IReadOnlyList<PendingBoundaryRow> Pending,
    IReadOnlyList<PaymentDoneRow> PaymentsDone,
    IReadOnlyList<OtherPaymentRow> OtherPayments,
    decimal DueToday,
    decimal CollectedToday);

// ---------------------------------------------------------------------
// Write plans: what to save. The service turns these into Firestore writes.
// ---------------------------------------------------------------------

/// <summary>One new boundary_payments document. AmountPaid is that document's own amount.</summary>
public sealed record BoundaryPaymentWrite(
    string DocumentId,
    string ShiftId,
    string DriverId,
    decimal ExpectedBoundary,
    decimal LateFees,
    decimal FuelPenalty,
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

/// <summary>
/// Where one payment goes, mirroring the web's PaymentBooking: <paramref name="ToFirst"/> to the shift being paid,
/// <paramref name="ToOthers"/> to older unpaid shifts, <paramref name="AdjustmentCredit"/> to manual debt (saved as a
/// credit adjustment) and <paramref name="Advance"/> kept as credit toward the next boundary.
/// <paramref name="Unallocated"/> is money with nowhere to go (a driver with no shifts); the caller refuses it.
/// </summary>
public sealed record PaymentPlan(
    IReadOnlyList<BoundaryPaymentWrite> PaymentUpdates,
    string TransactionId,
    decimal ToFirst,
    decimal ToOthers,
    decimal AdjustmentCredit,
    decimal Advance,
    decimal Unallocated,
    PaymentEvidence? Evidence);

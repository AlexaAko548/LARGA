using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.SharedCore.Models.FinancialLedger;
using LARGA.Shared.Models.Entities;
using Microsoft.Extensions.Logging;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Backs the ManagerWeb Financial Ledger &amp; Debts page's "Daily Settlements (Today)" tab
/// and the "+ Adjustment" modal. Same Lazy&lt;FirestoreDb&gt; pattern as FleetReportingService
/// and DriverManagementService, for the same reason (credential failures should surface
/// inside a page's try/catch, not at DI-construction time).
///
/// The "Master Debt Ledger" tab (a driver's running unpaid balance across all history, not
/// just today) has no mockup yet as of 2026-09-10, so it isn't built here - the sidebar tab
/// stays disabled until one exists, same treatment as the still-unbuilt nav pages.
/// </summary>
public class FinancialLedgerService
{
    private const decimal DefaultBoundaryRate = ShiftRules.DefaultBoundaryRate;

    private readonly Lazy<FirestoreDb> _dbLazy;
    private readonly Lazy<PhotoStorageTarget> _storageLazy;
    private readonly ILogger<FinancialLedgerService> _logger;

    private FirestoreDb Db => _dbLazy.Value;

    public FinancialLedgerService(Lazy<FirestoreDb> dbLazy, Lazy<PhotoStorageTarget> storageLazy, ILogger<FinancialLedgerService> logger)
    {
        _dbLazy = dbLazy;
        _storageLazy = storageLazy;
        _logger = logger;
    }

    /// <summary>A receipt's link fields for a boundary_payments document, after uploading its
    /// image (receipts/{driverId}/...). Null when there's no receipt (a cash payment).</summary>
    private async Task<(string Url, string? ReferenceNo)?> UploadReceiptAsync(string driverId, PaymentReceipt? receipt)
    {
        if (receipt is null)
        {
            return null;
        }

        string folder = $"receipts/{(string.IsNullOrWhiteSpace(driverId) ? "unknown" : driverId)}";
        string url = await _storageLazy.Value.UploadImageAsync(folder, receipt.Image, receipt.ContentType);
        return (url, string.IsNullOrWhiteSpace(receipt.ReferenceNo) ? null : receipt.ReferenceNo.Trim());
    }

    // ---------------------------------------------------------------------
    // Daily Settlements
    // ---------------------------------------------------------------------

    public async Task<DailySettlementSnapshot> GetDailySettlementAsync(DateTime dateUtc)
    {
        // "Today" means the Philippine calendar day, not the UTC one - PH is 8 hours ahead,
        // so a shift starting at, say, 6 AM Philippine time is stored with a UTC shiftStart of
        // 22:00 the *previous* UTC calendar date. Truncating dateUtc.Date directly (the old
        // behavior) would silently drop that shift from "today" for a chunk of the business
        // day. Converting to PH time first, then truncating, gets the boundary right; the
        // query itself still runs in UTC (Firestore timestamps are UTC), only the boundary
        // instants are computed differently.
        DateTime phDate = dateUtc.ToPhilippineTime().Date;

        // The full history is needed, not just this day's shifts: overpayment credit from any
        // day pays off a driver's oldest unpaid shifts first (OwedAfterCredit), and this view
        // has to agree with the Master Debt Ledger about which shifts that covers.
        SettlementContext context = await LoadSettlementContextAsync();
        return BuildDailySettlement(phDate, context);
    }

    /// <summary>
    /// The Daily Settlements totals for each of the last <paramref name="days"/> Philippine
    /// calendar days (oldest first, ending today) - the Executive Dashboard's boundary
    /// collection chart. Same figures as opening each day in Daily Settlements.
    /// </summary>
    public async Task<List<DailySettlementSnapshot>> GetCollectionTrendAsync(int days = 7)
    {
        SettlementContext context = await LoadSettlementContextAsync();
        DateTime today = ShiftClock.UtcNow.ToPhilippineTime().Date;
        return Enumerable.Range(0, Math.Max(1, days))
            .Select(i => BuildDailySettlement(today.AddDays(i - days + 1), context))
            .ToList();
    }

    /// <summary>Everything a day's settlement is computed from, loaded once.</summary>
    private sealed record SettlementContext(
        List<ShiftLog> Shifts,
        List<BoundaryPayment> Payments,
        List<UserProfile> Drivers,
        decimal DefaultRate,
        Dictionary<string, ShiftCharge> ChargeByShift,
        Dictionary<string, decimal> OwedAfterCredit);

    private async Task<SettlementContext> LoadSettlementContextAsync()
    {
        (List<ShiftLog> allShifts, List<BoundaryPayment> payments, _, List<UserProfile> drivers) = await GetFullLedgerDataAsync();
        decimal defaultRate = await GetDefaultBoundaryRateAsync();

        // Per shift: what it still owes after credit (shifts not listed owe nothing).
        List<ShiftCharge> charges = BuildCharges(allShifts, payments, defaultRate);
        var owedAfterCredit = new Dictionary<string, decimal>();
        foreach (IGrouping<string, ShiftCharge> driverCharges in charges.GroupBy(c => c.DriverId))
        {
            foreach ((ShiftCharge charge, decimal owed) in OwedAfterCredit(driverCharges))
            {
                owedAfterCredit[charge.Shift!.DocumentId] = owed;
            }
        }
        Dictionary<string, ShiftCharge> chargeByShift = charges
            .Where(c => c.Shift is not null)
            .ToDictionary(c => c.Shift!.DocumentId);

        return new SettlementContext(allShifts, payments, drivers, defaultRate, chargeByShift, owedAfterCredit);
    }

    private static DailySettlementSnapshot BuildDailySettlement(DateTime phDate, SettlementContext context)
    {
        DateTime dayStart = phDate - PhilippineTime.Offset;
        DateTime dayEnd = dayStart.AddDays(1).AddTicks(-1);

        (List<ShiftLog> allShifts, List<BoundaryPayment> payments, List<UserProfile> drivers, decimal defaultRate,
            Dictionary<string, ShiftCharge> chargeByShift, Dictionary<string, decimal> owedAfterCredit) = context;
        List<ShiftLog> todaysShifts = allShifts
            .Where(s => s.ShiftStart is DateTime start && start >= dayStart && start <= dayEnd)
            .ToList();

        var rows = todaysShifts.Select(shift =>
        {
            UserProfile? driver = drivers.FirstOrDefault(d => d.UserId == shift.DriverId);
            // The shift's charge covers all of its payment documents (one per payment); a shift
            // still on the road with nothing paid has none yet.
            bool isCharge = chargeByShift.TryGetValue(shift.DocumentId, out ShiftCharge? existing);
            ShiftCharge charge = existing ?? NewCharge(shift, Array.Empty<BoundaryPayment>(), defaultRate);

            ExtraCharges extras = charge.Extras;
            decimal expected = charge.Expected;
            decimal paid = charge.Paid;

            // The part of this shift's balance the driver's overpayment credit covers. Only real
            // charges get credit - a shift still on the road with nothing paid isn't owed yet, and
            // owedAfterCredit has no entry for it, which would otherwise read as fully covered.
            decimal credit = isCharge && charge.Outstanding > 0
                ? charge.Outstanding - owedAfterCredit.GetValueOrDefault(shift.DocumentId)
                : 0m;

            SettlementStatus status = ComputeStatus(charge.Payment?.PaymentStatus, paid, expected);
            if (status != SettlementStatus.Cleared && credit > 0)
            {
                status = paid + credit >= expected ? SettlementStatus.Cleared : SettlementStatus.Partial;
            }

            return new SettlementRow
            {
                ShiftId = shift.DocumentId,
                DriverId = shift.DriverId,
                DriverName = driver?.FullName ?? shift.DriverId,
                TaxiId = shift.TaxiId,
                ShiftEnd = shift.ShiftEnd,
                ExpectedTotal = expected,
                LateFee = extras.LateFee,
                FuelPenalty = extras.FuelPenalty,
                AmountPaid = paid,
                CreditApplied = credit,
                Status = status,
            };
        })
        .OrderBy(r => r.Status == SettlementStatus.Cleared ? 1 : 0)
        .ThenBy(r => r.DriverName)
        .ToList();

        return new DailySettlementSnapshot
        {
            Date = phDate, // the PH calendar date this covers - dayStart/dayEnd are UTC query bounds, not for display
            ExpectedCollection = rows.Sum(r => r.ExpectedTotal),
            CollectedSoFar = rows.Sum(r => r.AmountPaid),
            CreditApplied = rows.Sum(r => r.CreditApplied),
            ClearedCount = rows.Count(r => r.Status == SettlementStatus.Cleared),
            PartialCount = rows.Count(r => r.Status == SettlementStatus.Partial),
            WaitingCount = rows.Count(r => r.Status == SettlementStatus.Waiting),
            Rows = rows,
        };
    }

    private static SettlementStatus ComputeStatus(PaymentStatus? status, decimal paid, decimal expected)
    {
        if (status == PaymentStatus.Paid || (paid > 0 && paid >= expected))
        {
            return SettlementStatus.Cleared;
        }

        return paid > 0 ? SettlementStatus.Partial : SettlementStatus.Waiting;
    }

    /// <summary>
    /// Records a payment handed over for a shift. Per the paper (Ch. IV), what a driver owes on
    /// a day is that day's rent plus any debt carried from previous shifts - so the payment
    /// goes to this shift's balance first, and anything above it pays off the driver's older
    /// unpaid shifts, oldest first, then their manual-adjustment debt (same allocation as
    /// Settle Debt). Anything above everything the driver owes is kept as advance credit.
    ///
    /// Every payment is saved as new boundary_payments document(s) - one per shift it pays -
    /// never by changing an existing one (see BookPaymentAsync).
    /// </summary>
    /// <param name="shiftId">The shift's document ID (SettlementRow.ShiftId); a shiftId field
    /// value also works.</param>
    public async Task<RecordPaymentResult> RecordPaymentAsync(string shiftId, decimal amountReceived, string paymentMethod, PaymentReceipt? receipt = null)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
        {
            return new RecordPaymentResult { Ok = false, ErrorMessage = "Missing shift." };
        }

        if (InputValidator.ValidateAmount(amountReceived, "the amount received", 100_000m) is string amountError)
        {
            return new RecordPaymentResult { Ok = false, ErrorMessage = amountError };
        }

        PaymentMethod method = string.Equals(paymentMethod, "EWallet", StringComparison.OrdinalIgnoreCase)
            ? PaymentMethod.EWallet
            : PaymentMethod.Cash;

        try
        {
            ShiftLog? shift = await FindShiftAsync(shiftId);
            if (shift is null)
            {
                return new RecordPaymentResult { Ok = false, ErrorMessage = "This shift no longer exists." };
            }

            (List<ShiftLog> shifts, List<BoundaryPayment> payments, List<DebtAdjustment> adjustments, _) = await GetFullLedgerDataAsync();
            decimal defaultRate = await GetDefaultBoundaryRateAsync();
            List<ShiftCharge> driverCharges = BuildCharges(shifts, payments, defaultRate).Where(c => c.DriverId == shift.DriverId).ToList();

            // This shift's charge. A still-Active shift nobody has paid on isn't a charge yet
            // (see BuildCharges), but the driver can still pay ahead for it.
            ShiftCharge thisCharge = driverCharges.FirstOrDefault(c => c.Shift?.DocumentId == shift.DocumentId)
                ?? NewCharge(shift, Array.Empty<BoundaryPayment>(), defaultRate);
            List<DebtAdjustment> driverAdjustments = adjustments.Where(a => a.DriverId == shift.DriverId).ToList();

            // Booked onto actual unpaid records - this shift first, then the oldest others, then
            // adjustment debt - so every peso received sits on a document (credit keeps covering
            // whatever those records still show unpaid). Anything above all of it stays on this
            // shift's record as advance credit toward the driver's next boundary.
            DateTime now = DateTime.UtcNow;
            var receiptLink = await UploadReceiptAsync(shift.DriverId, receipt);
            PaymentBooking booking = await BookPaymentAsync(shift.DriverId, thisCharge, driverCharges.Where(c => c != thisCharge),
                driverAdjustments, amountReceived, advanceTarget: thisCharge, method, now, receiptLink, SourceDailySettlements);

            decimal thisPaid = thisCharge.Paid + booking.ToFirst + booking.Advance;
            return new RecordPaymentResult
            {
                Ok = true,
                NewStatus = thisPaid >= thisCharge.Expected ? SettlementStatus.Cleared : thisPaid > 0 ? SettlementStatus.Partial : SettlementStatus.Waiting,
                RemainingAfterPayment = Math.Max(0, thisCharge.Expected - thisPaid),
                AppliedToOlderDebt = booking.ToOthers + booking.ToAdjustments,
                AdvanceCredit = booking.Advance,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record payment for shift {ShiftId}", shiftId);
            return new RecordPaymentResult { Ok = false, ErrorMessage = "Could not save this payment. Please try again." };
        }
    }

    /// <summary>What the driver of this shift owes apart from what this shift still owes (after
    /// credit): the Master Debt Ledger total minus this row's balance. Record Payment uses it to
    /// preview where money above this shift's balance goes (older debt, then advance credit).</summary>
    public async Task<decimal> GetOtherDebtForShiftAsync(string shiftId)
    {
        ShiftLog? shift = await FindShiftAsync(shiftId);
        if (shift is null)
        {
            return 0m;
        }

        (List<ShiftLog> shifts, List<BoundaryPayment> payments, List<DebtAdjustment> adjustments, _) = await GetFullLedgerDataAsync();
        decimal defaultRate = await GetDefaultBoundaryRateAsync();
        List<ShiftCharge> driverCharges = BuildCharges(shifts, payments, defaultRate)
            .Where(c => c.DriverId == shift.DriverId)
            .ToList();
        ShiftCharge thisCharge = driverCharges.FirstOrDefault(c => c.Shift?.DocumentId == shift.DocumentId)
            ?? NewCharge(shift, Array.Empty<BoundaryPayment>(), defaultRate);
        List<ShiftCharge> withThis = driverCharges.Contains(thisCharge) ? driverCharges : driverCharges.Append(thisCharge).ToList();

        decimal thisOwed = OwedAfterCredit(withThis).Where(o => o.Charge == thisCharge).Sum(o => o.Owed);
        return Math.Max(0, TotalOwed(withThis, adjustments.Where(a => a.DriverId == shift.DriverId)) - thisOwed);
    }

    // ---------------------------------------------------------------------
    // Manual adjustments
    // ---------------------------------------------------------------------

    public async Task<AdjustmentResult> AddAdjustmentAsync(string driverId, decimal amount, string reason)
    {
        if (string.IsNullOrWhiteSpace(driverId))
        {
            return new AdjustmentResult { Ok = false, ErrorMessage = "Select a driver." };
        }

        if (amount == 0)
        {
            return new AdjustmentResult { Ok = false, ErrorMessage = "Amount can't be zero." };
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return new AdjustmentResult { Ok = false, ErrorMessage = "A reason is required for every adjustment." };
        }

        if ((InputValidator.ValidateAmount(Math.Abs(amount), "the amount", 50_000m)
             ?? InputValidator.ValidateText(reason, "a reason", 300, required: true, minLength: 5)) is string inputError)
        {
            return new AdjustmentResult { Ok = false, ErrorMessage = inputError };
        }

        try
        {
            var adjustment = new DebtAdjustment
            {
                DriverId = driverId,
                Amount = amount,
                Reason = reason.Trim(),
                Timestamp = DateTime.UtcNow,
            };

            await Db.Collection("debt_adjustments").AddAsync(adjustment);
            return new AdjustmentResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save debt adjustment for driver {DriverId}", driverId);
            return new AdjustmentResult { Ok = false, ErrorMessage = "Could not save this adjustment. Please try again." };
        }
    }

    // ---------------------------------------------------------------------
    // Master Debt Ledger
    //
    // Unlike Daily Settlements/Dashboard, this tab's entire purpose is an all-time
    // reckoning of what each driver owes - windowing by date would defeat the feature. So,
    // deliberately and unlike the rest of this service, GetFullLedgerDataAsync below fetches
    // `shifts` and `boundary_payments` in FULL rather than narrowly (see docs/ERD.md
    // "Dashboard read-cost notes" for why those two normally stay windowed). At this
    // project's fleet/testing scale that's inexpensive; if real shift history ever grows
    // large enough for this to matter, the fix is a precomputed running balance per driver
    // updated incrementally on each write, not a full re-summing of history on every load.
    //
    // Debt is worked out per shift (see BuildCharges), the same way Daily Settlements does:
    // every ended shift owes its boundary + late fee, whether or not a boundary_payments
    // document exists for it yet - a shift nobody has paid anything on has no document, and
    // owes the full amount. Paying from either tab writes the same per-shift document, so the
    // two always agree.
    // ---------------------------------------------------------------------

    public async Task<DebtLedgerSnapshot> GetDebtLedgerAsync()
    {
        (List<ShiftLog> shifts, List<BoundaryPayment> payments, List<DebtAdjustment> adjustments, List<UserProfile> drivers) = await GetFullLedgerDataAsync();
        List<ShiftCharge> charges = BuildCharges(shifts, payments, await GetDefaultBoundaryRateAsync());

        List<DebtLedgerRow> rows = drivers.Select(d =>
        {
            List<ShiftCharge> driverCharges = charges.Where(c => c.DriverId == d.UserId).ToList();
            decimal adjustmentTotal = adjustments.Where(a => a.DriverId == d.UserId).Sum(a => a.Amount);
            List<(ShiftCharge Charge, decimal Owed)> owed = OwedAfterCredit(driverCharges);

            int daysUnpaid = DaysUnpaidFor(driverCharges, DateTime.UtcNow);

            BoundaryPayment? lastPaid = driverCharges
                .SelectMany(c => c.Payments)
                .Where(p => p.AmountPaid > 0)
                .OrderByDescending(p => p.Timestamp)
                .FirstOrDefault();

            return new DebtLedgerRow
            {
                DriverId = d.UserId,
                DriverName = d.FullName,
                TaxiId = string.IsNullOrWhiteSpace(d.AssignedTaxiId) ? null : d.AssignedTaxiId,
                UnpaidCount = owed.Count,
                LastPaymentDate = lastPaid?.Timestamp,
                LastPaymentAmount = lastPaid?.AmountPaid,
                TotalDebt = Math.Max(0, NetShiftBalance(driverCharges) + adjustmentTotal),
                AdvanceCredit = Math.Max(0, -(NetShiftBalance(driverCharges) + adjustmentTotal)),
                DaysUnpaid = daysUnpaid,
                IsDebtFlagged = daysUnpaid >= ShiftRules.DebtFlagDays,
            };
        })
        .OrderByDescending(r => r.TotalDebt)
        .ThenBy(r => r.DriverName)
        .ToList();

        return new DebtLedgerSnapshot
        {
            DriversWithDebtCount = rows.Count(r => r.TotalDebt > 0),
            TotalOutstandingDebt = rows.Sum(r => r.TotalDebt),
            DebtFreeDriverCount = rows.Count(r => r.TotalDebt <= 0),
            Rows = rows,
        };
    }

    /// <summary>
    /// A driver's full transaction history for the "Financial History" modal, oldest-to-newest
    /// replayed into a running balance:
    ///  - Boundary Due: one row per shift, on the day it was driven.
    ///  - Payment: one row per handover - its boundary_payments documents grouped by
    ///    transactionId (one document per shift it paid), plus the automatic credit it made
    ///    against manual debt - listing which days it went to.
    ///  - Old running-total documents (from before every payment was its own document) can't
    ///    be split into separate payments, so each shows as one "recorded before itemized
    ///    history" payment.
    ///  - Manual penalties / credits.
    /// The newest row's RunningDebt always equals GetDebtLedgerAsync's TotalDebt for the driver.
    /// </summary>
    public async Task<DriverLedgerHistory> GetDriverHistoryAsync(string driverId)
    {
        (List<ShiftLog> shifts, List<BoundaryPayment> payments, List<DebtAdjustment> adjustments, List<UserProfile> drivers) = await GetFullLedgerDataAsync();
        UserProfile? driver = drivers.FirstOrDefault(d => d.UserId == driverId);
        List<ShiftCharge> charges = BuildCharges(shifts, payments, await GetDefaultBoundaryRateAsync())
            .Where(c => c.DriverId == driverId)
            .ToList();
        List<DebtAdjustment> driverAdjustments = adjustments.Where(a => a.DriverId == driverId).ToList();

        var transactions = new List<LedgerTransaction>();

        // What each shift owes, on the day it was driven.
        foreach (ShiftCharge c in charges.Where(c => c.Expected != 0))
        {
            var parts = new List<string> { $"Boundary ₱{c.Expected - c.Extras.Total:N0}" };
            if (c.Extras.LateFee > 0) parts.Add($"late fee ₱{c.Extras.LateFee:N0}");
            if (c.Extras.FuelPenalty > 0) parts.Add($"low-fuel penalty ₱{c.Extras.FuelPenalty:N0}");

            transactions.Add(new LedgerTransaction
            {
                Timestamp = c.Date,
                TransactionType = "Boundary Due",
                Subtitle = $"{DayLabel(c.Date)}{UnitSuffix(c)} · {string.Join(" + ", parts)}",
                Expected = c.Expected,
                DebtChange = c.Expected,
            });
        }

        // Each shift's money in the order it arrived: an old running total first (it predates
        // every individual payment), then each payment. Whatever goes past the shift's total is
        // advance credit.
        var pieces = new List<(string Txn, BoundaryPayment Doc, ShiftCharge Charge, decimal ToShift, decimal ToCredit)>();
        foreach (ShiftCharge c in charges)
        {
            decimal paidSoFar = 0m;
            BoundaryPayment? runningTotal = c.Payments
                .Where(d => !BoundaryPaymentRules.IsIndividualPayment(d.PaymentId, d.TransactionId))
                .OrderByDescending(d => d.AmountPaid)
                .FirstOrDefault();
            IEnumerable<BoundaryPayment> ordered = c.Payments
                .Where(d => BoundaryPaymentRules.IsIndividualPayment(d.PaymentId, d.TransactionId))
                .OrderBy(d => d.RecordedAt);
            if (runningTotal is not null)
            {
                ordered = ordered.Prepend(runningTotal);
            }

            foreach (BoundaryPayment doc in ordered)
            {
                if (doc.AmountPaid == 0)
                {
                    continue;
                }

                decimal toShift = Math.Clamp(c.Expected - paidSoFar, 0, doc.AmountPaid);
                paidSoFar += doc.AmountPaid;
                string txn = doc == runningTotal ? $"LEGACY:{doc.PaymentId}"
                    : !string.IsNullOrEmpty(doc.TransactionId) ? doc.TransactionId : doc.PaymentId;
                pieces.Add((txn, doc, c, toShift, doc.AmountPaid - toShift));
            }
        }

        // The automatic credits payments made against manual debt, by the payment they belong to.
        Dictionary<string, List<DebtAdjustment>> creditsByTxn = driverAdjustments
            .Where(a => !string.IsNullOrEmpty(a.TransactionId))
            .GroupBy(a => a.TransactionId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (IGrouping<string, (string Txn, BoundaryPayment Doc, ShiftCharge Charge, decimal ToShift, decimal ToCredit)> handover in pieces.GroupBy(p => p.Txn))
        {
            BoundaryPayment first = handover.First().Doc;
            bool isLegacy = handover.Key.StartsWith("LEGACY:", StringComparison.Ordinal);

            var details = handover
                .OrderBy(p => p.Charge.Date)
                .SelectMany(p => new[]
                {
                    p.ToShift > 0 ? $"₱{p.ToShift:N0} → {DayLabel(p.Charge.Date)} boundary{UnitSuffix(p.Charge)}" : null,
                    p.ToCredit > 0 ? $"₱{p.ToCredit:N0} → advance credit (covers their next boundary)" : null,
                })
                .OfType<string>()
                .ToList();

            decimal toAdjustments = 0m;
            if (creditsByTxn.Remove(handover.Key, out List<DebtAdjustment>? credits))
            {
                toAdjustments = -credits.Sum(a => a.Amount);
                details.Add($"₱{toAdjustments:N0} → penalties / manual adjustments");
            }

            decimal amount = handover.Sum(p => p.Doc.AmountPaid) + toAdjustments;
            string? reference = first.GcashReferenceNumber ?? first.ReceiptReferenceNo;
            string where = isLegacy ? "recorded before itemized history"
                : !string.IsNullOrEmpty(first.RecordedVia) ? $"recorded in {first.RecordedVia}"
                : string.IsNullOrEmpty(first.TransactionId) ? "recorded in the manager app" : "recorded in the manager app's Quick Ledger";
            if (!string.IsNullOrWhiteSpace(first.Notes))
            {
                details.Add($"Note: {first.Notes}");
            }

            transactions.Add(new LedgerTransaction
            {
                Timestamp = handover.Max(p => p.Doc.RecordedAt),
                TransactionType = "Payment",
                Subtitle = $"{(first.PaymentMethod == PaymentMethod.EWallet ? "GCash" : "Cash")} · {where}",
                AmountPaid = amount,
                DebtChange = -amount,
                ReceiptUrl = string.IsNullOrWhiteSpace(first.EPayReceiptPhoto) ? null : first.EPayReceiptPhoto,
                ReceiptReferenceNo = reference,
                Details = details,
            });
        }

        // Manual penalties/credits (and any automatic credit whose payment wasn't found).
        HashSet<DebtAdjustment> unmatchedCredits = creditsByTxn.Values.SelectMany(l => l).ToHashSet();
        foreach (DebtAdjustment a in driverAdjustments.Where(a => string.IsNullOrEmpty(a.TransactionId) || unmatchedCredits.Contains(a)))
        {
            transactions.Add(new LedgerTransaction
            {
                Timestamp = a.Timestamp,
                TransactionType = a.Amount >= 0 ? "Penalty" : "Credit / Write-off",
                Subtitle = string.IsNullOrWhiteSpace(a.Reason) ? null : a.Reason,
                AdjustmentAmount = a.Amount,
                DebtChange = a.Amount,
            });
        }

        // Replayed in order: a boundary adds what it costs, a payment takes off what was paid,
        // an adjustment adds/subtracts its amount. Credit isn't lost when it arrives before a
        // debt, so the running total isn't floored until it's displayed - the final figure
        // then matches the Master Debt Ledger's TotalDebt. On the same instant, charges count
        // before the payment that covered them.
        List<LedgerTransaction> chronological = transactions
            .OrderBy(t => t.Timestamp)
            .ThenBy(t => t.DebtChange < 0 ? 1 : 0)
            .ToList();
        decimal running = 0;
        foreach (LedgerTransaction tx in chronological)
        {
            running += tx.DebtChange ?? 0;
            tx.RunningDebt = Math.Max(0, running);
        }

        return new DriverLedgerHistory
        {
            DriverId = driverId,
            DriverName = driver?.FullName ?? driverId,
            CurrentOutstandingDebt = Math.Max(0, running),
            Transactions = Enumerable.Reverse(chronological).ToList(),
        };
    }

    private static string DayLabel(DateTime utc) => utc == DateTime.MinValue ? "Unknown day" : utc.ToPhilippineTime().ToString("ddd, MMM d");

    private static string UnitSuffix(ShiftCharge c) => string.IsNullOrEmpty(c.Shift?.TaxiId) ? string.Empty : $" ({c.Shift!.TaxiId})";

    /// <summary>
    /// Applies a lump-sum payment against a driver's total debt: oldest outstanding shift
    /// first (standard oldest-debt-first allocation), then their net manual-adjustment debt via
    /// an automatic offsetting credit. Anything above what's owed is kept as advance credit on
    /// their latest shift, covering their next boundary.
    /// </summary>
    public async Task<SettleDebtResult> SettleDebtAsync(string driverId, decimal amountReceived, string paymentMethod, PaymentReceipt? receipt = null)
    {
        if (string.IsNullOrWhiteSpace(driverId))
        {
            return new SettleDebtResult { Ok = false, ErrorMessage = "Missing driver." };
        }

        if (InputValidator.ValidateAmount(amountReceived, "the amount received", 100_000m) is string amountError)
        {
            return new SettleDebtResult { Ok = false, ErrorMessage = amountError };
        }

        try
        {
            (List<ShiftLog> shifts, List<BoundaryPayment> payments, List<DebtAdjustment> adjustments, _) = await GetFullLedgerDataAsync();
            List<ShiftCharge> driverCharges = BuildCharges(shifts, payments, await GetDefaultBoundaryRateAsync())
                .Where(c => c.DriverId == driverId)
                .ToList();

            List<DebtAdjustment> driverAdjustments = adjustments.Where(a => a.DriverId == driverId).ToList();

            // Anything above what's owed stays on the driver's latest shift as advance credit.
            ShiftCharge? latest = driverCharges.OrderByDescending(c => c.Date).FirstOrDefault();
            if (latest is null && amountReceived > TotalOwed(driverCharges, driverAdjustments))
            {
                return new SettleDebtResult { Ok = false, ErrorMessage = "This driver has no shifts yet to hold an advance payment - record only what they owe." };
            }

            // Booked onto the oldest unpaid shifts first, then adjustment debt - a new payment
            // document for each shift it pays, all sharing one transactionId. One receipt can pay
            // off several shifts - each document links it.
            var receiptLink = await UploadReceiptAsync(driverId, receipt);
            PaymentBooking booking = await BookPaymentAsync(driverId, null, driverCharges, driverAdjustments, amountReceived,
                advanceTarget: latest, PaymentMethodOf(paymentMethod), DateTime.UtcNow, receiptLink, SourceMasterLedger);
            decimal remaining = booking.Advance;

            DebtLedgerSnapshot refreshed = await GetDebtLedgerAsync();
            decimal newTotal = refreshed.Rows.FirstOrDefault(r => r.DriverId == driverId)?.TotalDebt ?? 0;

            return new SettleDebtResult
            {
                Ok = true,
                RemainingDebt = newTotal,
                AdvanceCredit = remaining,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to settle debt for driver {DriverId}", driverId);
            return new SettleDebtResult { Ok = false, ErrorMessage = "Could not save this settlement. Please try again." };
        }
    }

    /// <summary>A shift's IDs: its document ID and its shiftId field. They should be equal,
    /// but shifts clocked in by older mobile builds got a made-up "SHIFT_yyyyMMdd_nnn" field
    /// value - and payments were written against either one (the web ledger used the field,
    /// the mobile quick ledger the document ID). Matching on both links them all.</summary>
    private static List<string> IdsOf(ShiftLog shift) =>
        new[] { shift.DocumentId, shift.ShiftId }.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();

    private static PaymentMethod PaymentMethodOf(string paymentMethod) =>
        string.Equals(paymentMethod, "EWallet", StringComparison.OrdinalIgnoreCase) ? PaymentMethod.EWallet : PaymentMethod.Cash;

    // boundary_payments.recordedVia - which screen took the payment, shown in the history.
    private const string SourceDailySettlements = "Daily Settlements";
    private const string SourceMasterLedger = "Master Debt Ledger";

    /// <summary>How a payment was booked (see BookPaymentAsync).</summary>
    private readonly record struct PaymentBooking(decimal ToFirst, decimal ToOthers, decimal ToAdjustments, decimal Advance);

    /// <summary>
    /// Books a payment onto the driver's records: <paramref name="first"/>'s unpaid balance
    /// (Record Payment's own shift), then <paramref name="others"/>' unpaid balances oldest
    /// first, then net adjustment debt (as an offsetting credit adjustment). Whatever is left
    /// is advance credit, paid onto <paramref name="advanceTarget"/> - above its total, which
    /// the ledger then applies to the driver's next unpaid boundary.
    ///
    /// Every part is written as a NEW boundary_payments document holding only that part's
    /// amount (BoundaryPaymentRules) - nothing already saved is updated or overwritten, so
    /// each payment stays in the history. All the documents of one handover share a
    /// transactionId, which the automatic credit adjustment carries too.
    /// </summary>
    private async Task<PaymentBooking> BookPaymentAsync(string driverId, ShiftCharge? first, IEnumerable<ShiftCharge> others,
        IEnumerable<DebtAdjustment> driverAdjustments, decimal amount, ShiftCharge? advanceTarget,
        PaymentMethod method, DateTime now, (string Url, string? ReferenceNo)? receiptLink, string source)
    {
        var applies = new Dictionary<ShiftCharge, decimal>();
        decimal remaining = amount;

        decimal toFirst = first is null ? 0 : Math.Min(remaining, first.Outstanding);
        if (toFirst > 0)
        {
            applies[first!] = toFirst;
            remaining -= toFirst;
        }

        decimal toOthers = 0;
        foreach ((ShiftCharge charge, decimal owed) in UnpaidOldestFirst(others))
        {
            if (remaining <= 0)
            {
                break;
            }

            decimal apply = Math.Min(remaining, owed);
            applies[charge] = apply;
            toOthers += apply;
            remaining -= apply;
        }

        decimal toAdjustments = Math.Min(remaining, Math.Max(0, driverAdjustments.Sum(a => a.Amount)));
        remaining -= toAdjustments;

        decimal advance = advanceTarget is null ? 0 : remaining;
        if (advance > 0)
        {
            applies[advanceTarget!] = applies.GetValueOrDefault(advanceTarget!) + advance;
        }

        string transactionId = BoundaryPaymentRules.NewTransactionId(driverId, now);
        int index = 0;
        foreach ((ShiftCharge charge, decimal apply) in applies)
        {
            await CreatePaymentAsync(charge, apply, method, now, receiptLink, transactionId, index++, source);
        }

        if (toAdjustments > 0)
        {
            await Db.Collection("debt_adjustments").AddAsync(new DebtAdjustment
            {
                DriverId = driverId,
                Amount = -toAdjustments,
                Reason = "Automatic credit from a debt payment.",
                Timestamp = now,
                TransactionId = transactionId,
            });
        }

        return new PaymentBooking(toFirst, toOthers, toAdjustments, advance);
    }

    /// <summary>
    /// The driver's shifts that still owe something, oldest first, with how much each still
    /// owes once overpayment credit is used up. Money paid above a shift's total (possible on
    /// records made before Record Payment passed extra on to older debt) isn't lost - it's
    /// credit that pays off the oldest unpaid shifts first.
    /// </summary>
    private static List<(ShiftCharge Charge, decimal Owed)> OwedAfterCredit(IEnumerable<ShiftCharge> driverCharges)
    {
        List<ShiftCharge> charges = driverCharges.ToList();
        decimal credit = charges.Sum(c => c.Overpaid);
        var owed = new List<(ShiftCharge, decimal)>();

        foreach (ShiftCharge charge in charges.Where(c => c.Outstanding > 0).OrderBy(c => c.Date))
        {
            decimal useCredit = Math.Min(credit, charge.Outstanding);
            credit -= useCredit;
            if (charge.Outstanding - useCredit > 0)
            {
                owed.Add((charge, charge.Outstanding - useCredit));
            }
        }

        return owed;
    }

    /// <summary>Each shift's unpaid balance as its record shows it (before credit), oldest
    /// first - where new money is booked.</summary>
    private static List<(ShiftCharge Charge, decimal Owed)> UnpaidOldestFirst(IEnumerable<ShiftCharge> driverCharges) =>
        driverCharges.Where(c => c.Outstanding > 0).OrderBy(c => c.Date).Select(c => (c, c.Outstanding)).ToList();

    /// <summary>Everything the driver owes - the Master Debt Ledger's TotalDebt: shift balances
    /// minus overpayment credit, plus net manual adjustments.</summary>
    private static decimal TotalOwed(IEnumerable<ShiftCharge> driverCharges, IEnumerable<DebtAdjustment> driverAdjustments) =>
        Math.Max(0, NetShiftBalance(driverCharges) + driverAdjustments.Sum(a => a.Amount));

    /// <summary>What the driver's shifts owe in total, minus overpayment credit (negative when
    /// the credit is bigger - it then offsets adjustment debt).</summary>
    private static decimal NetShiftBalance(IEnumerable<ShiftCharge> driverCharges)
    {
        List<ShiftCharge> charges = driverCharges.ToList();
        return charges.Sum(c => c.Outstanding) - charges.Sum(c => c.Overpaid);
    }

    /// <summary>Days the driver's oldest still-unpaid shift has gone unpaid (ShiftRules.DaysUnpaid);
    /// 0 when every shift is paid. Manual adjustments don't count - the paper's rule is about
    /// unpaid boundary.</summary>
    private static int DaysUnpaidFor(IEnumerable<ShiftCharge> driverCharges, DateTime nowUtc)
    {
        ShiftCharge? oldest = OldestOwed(driverCharges);
        return oldest is null ? 0 : ShiftRules.DaysUnpaid(oldest.Date, nowUtc);
    }

    // The oldest shift still owing something after overpayment credit.
    private static ShiftCharge? OldestOwed(IEnumerable<ShiftCharge> driverCharges) =>
        OwedAfterCredit(driverCharges).Select(o => o.Charge).FirstOrDefault(c => c.Date != DateTime.MinValue);

    // ---------------------------------------------------------------------
    // Unpaid-debt flag (paper Ch. IV: 3+ consecutive days unpaid -> flag + notify manager)
    // ---------------------------------------------------------------------

    public const string UnpaidDebtAlertType = "UnpaidDebt";

    /// <summary>
    /// Run periodically by ManagerWeb's background monitor. A driver whose oldest unpaid shift
    /// is ShiftRules.DebtFlagDays or more days old gets users/{id}.debtFlaggedSince set and one
    /// manager alert (per unpaid episode - keyed on that oldest shift, so paying it off and
    /// falling behind again later raises a new one). Once nothing is that overdue any more, the
    /// flag is cleared. Only writes when the flag actually changes.
    /// </summary>
    public async Task ProcessDebtFlagsAsync()
    {
        (List<ShiftLog> shifts, List<BoundaryPayment> payments, _, List<UserProfile> drivers) = await GetFullLedgerDataAsync();
        List<ShiftCharge> charges = BuildCharges(shifts, payments, await GetDefaultBoundaryRateAsync());
        DateTime now = DateTime.UtcNow;

        foreach (UserProfile driver in drivers)
        {
            try
            {
                List<ShiftCharge> driverCharges = charges.Where(c => c.DriverId == driver.UserId).ToList();
                int daysUnpaid = DaysUnpaidFor(driverCharges, now);
                bool overdue = daysUnpaid >= ShiftRules.DebtFlagDays;
                DocumentReference userRef = Db.Collection("users").Document(driver.UserId);

                if (!overdue)
                {
                    if (driver.DebtFlaggedSince is not null)
                    {
                        await userRef.UpdateAsync("debtFlaggedSince", FieldValue.Delete);
                    }
                    continue;
                }

                if (driver.DebtFlaggedSince is null)
                {
                    await userRef.UpdateAsync("debtFlaggedSince", now);
                }

                ShiftCharge oldest = OldestOwed(driverCharges)!;
                decimal owed = OwedAfterCredit(driverCharges).Sum(o => o.Owed);
                DocumentReference alertRef = Db.Collection("system_alerts").Document($"{oldest.Shift?.DocumentId ?? driver.UserId}_DEBT");
                if (!(await alertRef.GetSnapshotAsync()).Exists)
                {
                    await alertRef.SetAsync(new SystemAlert
                    {
                        Type = UnpaidDebtAlertType,
                        DriverId = driver.UserId,
                        DriverName = driver.FullName,
                        TaxiId = oldest.Shift?.TaxiId ?? string.Empty,
                        UnitLabel = oldest.Shift?.TaxiId ?? string.Empty,
                        ShiftId = oldest.Shift?.DocumentId ?? string.Empty,
                        Message = $"{driver.FullName} hasn't settled their balance for {daysUnpaid} days (since {oldest.Date.ToPhilippineTime():MMM d}) - ₱{owed:N0} unpaid boundary. Their account is flagged for review.",
                        Timestamp = now,
                        IsRead = false,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update the debt flag for driver {DriverId}", driver.UserId);
            }
        }
    }

    /// <summary>What one shift owes and has paid, for the Master Debt Ledger / Settle Debt.
    /// Payment is null when nobody has paid anything on the shift yet (no document).</summary>
    /// <param name="Payment">The shift's most recent payment document - where its expected
    /// boundary and Paid/Partial status come from.</param>
    /// <param name="Payments">All of its payment documents (one per payment).</param>
    /// <param name="Paid">The total paid across them (BoundaryPaymentRules.TotalPaid).</param>
    private sealed record ShiftCharge(string DriverId, ShiftLog? Shift, BoundaryPayment? Payment, IReadOnlyList<BoundaryPayment> Payments,
        DateTime Date, decimal Expected, ExtraCharges Extras, decimal Paid)
    {
        public decimal Outstanding => Payment?.PaymentStatus == PaymentStatus.Paid ? 0 : Math.Max(0, Expected - Paid);

        /// <summary>Paid above the shift's total - credit toward the driver's other debt.</summary>
        public decimal Overpaid => Math.Max(0, Paid - Expected);
    }

    private static ShiftCharge NewCharge(ShiftLog shift, IReadOnlyList<BoundaryPayment> documents, decimal defaultRate)
    {
        // The most recent document carries the shift's current expected boundary and status
        // (the Quick Ledger's "target" document); the default rate when it has none.
        BoundaryPayment? latest = documents.OrderByDescending(d => d.RecordedAt).FirstOrDefault();
        ExtraCharges extras = ExtrasFor(shift, latest);
        decimal boundary = latest is not null && latest.ExpectedBoundary > 0 ? latest.ExpectedBoundary : defaultRate;
        decimal paid = BoundaryPaymentRules.TotalPaid(documents.Select(d => (d.PaymentId, d.TransactionId, d.AmountPaid)));
        DateTime date = shift.ShiftStart ?? latest?.RecordedAt ?? shift.ShiftEnd ?? DateTime.MinValue;
        return new ShiftCharge(shift.DriverId, shift, latest, documents, date, boundary + extras.Total, extras, paid);
    }

    /// <summary>
    /// Every charge a driver has: one per shift with payment documents (all of them - one per
    /// payment - added up by BoundaryPaymentRules), plus one per ended shift with no document
    /// yet - owing the default boundary + its extra charges, nothing paid. A shift still Active
    /// isn't owed yet (the boundary is paid at the end of the day), unless something was
    /// already paid on it.
    /// </summary>
    private static List<ShiftCharge> BuildCharges(List<ShiftLog> shifts, List<BoundaryPayment> payments, decimal defaultRate)
    {
        Dictionary<string, ShiftLog> shiftsById = BuildShiftLookup(shifts);
        var documentsByShift = new Dictionary<string, List<BoundaryPayment>>();
        foreach (BoundaryPayment p in payments)
        {
            if (shiftsById.TryGetValue(p.ShiftId, out ShiftLog? shift))
            {
                if (!documentsByShift.TryGetValue(shift.DocumentId, out List<BoundaryPayment>? list))
                {
                    documentsByShift[shift.DocumentId] = list = new List<BoundaryPayment>();
                }
                list.Add(p);
            }
        }

        var charges = new List<ShiftCharge>();
        foreach (ShiftLog shift in shifts)
        {
            bool hasDocuments = documentsByShift.TryGetValue(shift.DocumentId, out List<BoundaryPayment>? documents);
            if (!hasDocuments && (string.IsNullOrWhiteSpace(shift.DriverId)
                || string.Equals(shift.Status, "Active", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            charges.Add(NewCharge(shift, documents ?? new List<BoundaryPayment>(), defaultRate));
        }

        return charges;
    }

    /// <summary>A new payment document for one payment on one shift - holding only this
    /// payment's amount (see BoundaryPaymentRules). Same fields as the Quick Ledger writes.</summary>
    private Task CreatePaymentAsync(ShiftCharge charge, decimal amount, PaymentMethod method, DateTime now,
        (string Url, string? ReferenceNo)? receipt, string transactionId, int index, string source)
    {
        ShiftLog shift = charge.Shift!;
        string shiftKey = BoundaryPaymentRules.ShiftKey(shift.DocumentId, shift.ShiftId);
        string docId = BoundaryPaymentRules.NewDocumentId(shiftKey, now, index);
        PaymentStatus status = charge.Paid + amount >= charge.Expected ? PaymentStatus.Paid : PaymentStatus.Partial;

        var fields = new Dictionary<string, object>
        {
            ["shiftId"] = shiftKey,
            ["driverId"] = charge.DriverId,
            ["expectedBoundary"] = (double)(charge.Expected - charge.Extras.Total),
            ["lateFees"] = (double)charge.Extras.LateFee,
            ["fuelPenalty"] = (double)charge.Extras.FuelPenalty,
            ["amountPaid"] = (double)amount,
            ["paymentMethod"] = new PaymentMethodConverter().ToFirestore(method),
            ["paymentStatus"] = new PaymentStatusConverter().ToFirestore(status),
            ["timestamp"] = now,
            ["recordedAtUtc"] = BoundaryPaymentRules.RecordedAtText(now),
            ["transactionId"] = transactionId,
            ["recordedVia"] = source,
        };
        if (receipt is { } r)
        {
            fields["ePayReceiptPhoto"] = r.Url;
            if (r.ReferenceNo is not null)
            {
                fields["gcashReferenceNumber"] = r.ReferenceNo;
                fields["receiptReferenceNo"] = r.ReferenceNo;
            }
        }

        // Create, not Set: refuses to touch a document that already exists, so a payment can
        // never overwrite another one.
        return Db.Collection("boundary_payments").Document(docId).CreateAsync(fields);
    }

    /// <summary>
    /// Stamps driverId on every boundary_payments document that doesn't have one yet, from the
    /// shift it belongs to. firestore.rules let a driver read only payments carrying their own
    /// driverId, so documents written before that field existed would otherwise disappear from
    /// the driver's ledger. Only touches documents missing it, so running it again is harmless.
    /// Returns how many documents were updated.
    /// </summary>
    public async Task<int> BackfillPaymentDriverIdsAsync()
    {
        (List<ShiftLog> shifts, List<BoundaryPayment> payments, _, _) = await GetFullLedgerDataAsync();
        Dictionary<string, ShiftLog> shiftsById = BuildShiftLookup(shifts);

        var updates = payments
            .Where(p => string.IsNullOrEmpty(p.DriverId) && !string.IsNullOrEmpty(p.PaymentId))
            .Select(p => (Payment: p, Shift: shiftsById.GetValueOrDefault(p.ShiftId)))
            .Where(x => !string.IsNullOrEmpty(x.Shift?.DriverId))
            .ToList();

        // Firestore batches hold at most 500 writes.
        foreach (var chunk in updates.Chunk(400))
        {
            WriteBatch batch = Db.StartBatch();
            foreach ((BoundaryPayment payment, ShiftLog? shift) in chunk)
            {
                batch.Update(Db.Collection("boundary_payments").Document(payment.PaymentId), "driverId", shift!.DriverId);
            }
            await batch.CommitAsync();
        }

        return updates.Count;
    }

    /// <summary>What a shift owes on top of the boundary (ShiftRules), set at clock-out.</summary>
    private readonly record struct ExtraCharges(decimal LateFee, decimal FuelPenalty)
    {
        public decimal Total => LateFee + FuelPenalty;
    }

    /// <summary>A shift's extra charges: the ones worked out at clock-out (shifts.lateFee /
    /// shifts.fuelPenalty) when the shift has them; otherwise whatever the payment record
    /// carries (older shifts, seed data, or an amount set by hand).</summary>
    private static ExtraCharges ExtrasFor(ShiftLog? shift, BoundaryPayment? payment) => new(
        shift?.LateFee is double late ? (decimal)late : payment?.LateFees ?? 0m,
        shift?.FuelPenalty is double fuel ? (decimal)fuel : payment?.FuelPenalty ?? 0m);


    // Each of a shift's IDs -> the shift (see IdsOf).
    private static Dictionary<string, ShiftLog> BuildShiftLookup(List<ShiftLog> shifts)
    {
        var map = new Dictionary<string, ShiftLog>();
        foreach (ShiftLog shift in shifts)
        {
            foreach (string id in IdsOf(shift))
            {
                map.TryAdd(id, shift);
            }
        }
        return map;
    }

    /// <summary>By document ID first; failing that, by shiftId field value.</summary>
    private async Task<ShiftLog?> FindShiftAsync(string shiftId)
    {
        DocumentSnapshot doc = await Db.Collection("shifts").Document(shiftId).GetSnapshotAsync();
        if (doc.Exists)
        {
            return doc.ConvertTo<ShiftLog>();
        }

        QuerySnapshot byField = await Db.Collection("shifts").WhereEqualTo("shiftId", shiftId).Limit(1).GetSnapshotAsync();
        return byField.Documents.Count > 0 ? byField.Documents[0].ConvertTo<ShiftLog>() : null;
    }

    // Firestore caps WhereIn at 30 values, so larger lists are queried in chunks.
    private async Task<List<BoundaryPayment>> GetPaymentsForShiftIdsAsync(List<string> shiftIds)
    {
        var results = new List<BoundaryPayment>();
        foreach (string[] chunk in shiftIds.Chunk(30))
        {
            results.AddRange(await GetWhereInAsync<BoundaryPayment>("boundary_payments", "shiftId", chunk.ToList()));
        }
        return results;
    }

    private async Task<(List<ShiftLog> Shifts, List<BoundaryPayment> Payments, List<DebtAdjustment> Adjustments, List<UserProfile> Drivers)> GetFullLedgerDataAsync()
    {
        List<ShiftLog> shifts = await GetAllAsync<ShiftLog>("shifts");
        List<BoundaryPayment> payments = await GetAllAsync<BoundaryPayment>("boundary_payments");
        List<DebtAdjustment> adjustments = await GetAllAsync<DebtAdjustment>("debt_adjustments");
        List<UserProfile> drivers = await GetDriversAsync();
        return (shifts, payments, adjustments, drivers);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private async Task<decimal> GetDefaultBoundaryRateAsync()
    {
        DocumentSnapshot snapshot = await Db.Collection("system_configs").Document("global").GetSnapshotAsync();
        if (!snapshot.Exists)
        {
            return DefaultBoundaryRate;
        }

        try
        {
            SystemConfig config = snapshot.ConvertTo<SystemConfig>();
            return config.DefaultBoundaryRate > 0 ? (decimal)config.DefaultBoundaryRate : DefaultBoundaryRate;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read system_configs/global, falling back to default boundary rate");
            return DefaultBoundaryRate;
        }
    }

    private async Task<List<UserProfile>> GetDriversAsync() =>
        (await GetAllAsync<UserProfile>("users"))
            .Where(u => string.Equals(u.Role, "Driver", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private async Task<List<T>> GetAllAsync<T>(string collection) where T : class
    {
        QuerySnapshot snapshot = await Db.Collection(collection).GetSnapshotAsync();
        return ConvertDocuments<T>(snapshot, collection);
    }

    private async Task<List<T>> GetBetweenAsync<T>(string collection, string dateField, DateTime fromUtc, DateTime toUtc) where T : class
    {
        QuerySnapshot snapshot = await Db.Collection(collection)
            .WhereGreaterThanOrEqualTo(dateField, fromUtc)
            .WhereLessThanOrEqualTo(dateField, toUtc)
            .GetSnapshotAsync();
        return ConvertDocuments<T>(snapshot, collection);
    }

    private async Task<List<T>> GetWhereInAsync<T>(string collection, string field, List<string> values) where T : class
    {
        QuerySnapshot snapshot = await Db.Collection(collection).WhereIn(field, values).GetSnapshotAsync();
        return ConvertDocuments<T>(snapshot, collection);
    }

    private List<T> ConvertDocuments<T>(QuerySnapshot snapshot, string collection) where T : class
    {
        var results = new List<T>(snapshot.Documents.Count);
        foreach (DocumentSnapshot doc in snapshot.Documents)
        {
            try
            {
                results.Add(doc.ConvertTo<T>());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping {Collection}/{DocumentId}: failed to convert to {Type}",
                    collection, doc.Id, typeof(T).Name);
            }
        }
        return results;
    }
}

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
    private const decimal DefaultBoundaryRate = 800m;

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
        DateTime dayStart = phDate - PhilippineTime.Offset;
        DateTime dayEnd = dayStart.AddDays(1).AddTicks(-1);

        List<ShiftLog> todaysShifts = await GetBetweenAsync<ShiftLog>("shifts", "shiftStart", dayStart, dayEnd);
        List<UserProfile> drivers = await GetDriversAsync();
        decimal defaultRate = await GetDefaultBoundaryRateAsync();

        // A payment can point at a shift by either of its IDs (see IdsOf), so look both up.
        List<string> shiftIds = todaysShifts.SelectMany(IdsOf).Distinct().ToList();
        List<BoundaryPayment> payments = await GetPaymentsForShiftIdsAsync(shiftIds);

        var rows = todaysShifts.Select(shift =>
        {
            UserProfile? driver = drivers.FirstOrDefault(d => d.UserId == shift.DriverId);
            BoundaryPayment? payment = PaymentFor(shift, payments);

            decimal expected = (payment?.ExpectedBoundary ?? defaultRate) + LateFeeFor(shift, payment);
            decimal paid = payment?.AmountPaid ?? 0m;

            return new SettlementRow
            {
                ShiftId = shift.DocumentId,
                DriverId = shift.DriverId,
                DriverName = driver?.FullName ?? shift.DriverId,
                TaxiId = shift.TaxiId,
                ShiftEnd = shift.ShiftEnd,
                ExpectedTotal = expected,
                AmountPaid = paid,
                Status = ComputeStatus(payment?.PaymentStatus, paid, expected),
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
    /// Records a payment against a shift's boundary. Adds to the shift's existing
    /// boundary_payments document if it has one - found by either of the shift's IDs, so a
    /// document the mobile app created under the other ID is reused rather than duplicated -
    /// otherwise creates one ({shiftDocumentId}_PAY, the seed data's convention).
    /// </summary>
    /// <param name="shiftId">The shift's document ID (SettlementRow.ShiftId); a shiftId field
    /// value also works.</param>
    public async Task<RecordPaymentResult> RecordPaymentAsync(string shiftId, decimal amountReceived, string paymentMethod, PaymentReceipt? receipt = null)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
        {
            return new RecordPaymentResult { Ok = false, ErrorMessage = "Missing shift." };
        }

        if (amountReceived <= 0)
        {
            return new RecordPaymentResult { Ok = false, ErrorMessage = "Amount received must be greater than zero." };
        }

        PaymentMethod method = string.Equals(paymentMethod, "EWallet", StringComparison.OrdinalIgnoreCase)
            ? PaymentMethod.EWallet
            : PaymentMethod.Cash;

        try
        {
            ShiftLog? shift = await FindShiftAsync(shiftId);
            var receiptLink = await UploadReceiptAsync(shift?.DriverId ?? string.Empty, receipt);
            List<string> ids = shift is null ? new List<string> { shiftId } : IdsOf(shift);
            BoundaryPayment? existing = shift is null
                ? (await GetPaymentsForShiftIdsAsync(ids)).FirstOrDefault()
                : PaymentFor(shift, await GetPaymentsForShiftIdsAsync(ids));

            decimal expected;
            decimal newAmountPaid;
            PaymentStatus newStatus;

            if (existing is not null)
            {
                decimal lateFee = LateFeeFor(shift, existing);
                expected = existing.ExpectedBoundary + lateFee;
                newAmountPaid = existing.AmountPaid + amountReceived;
                newStatus = newAmountPaid >= expected ? PaymentStatus.Paid : PaymentStatus.Partial;

                // Only the fields a payment changes - the boundary and anything else on the
                // document (reference number, receipt photo, fields the mobile app adds) stay as
                // they are. The late fee is written through so the record carries it too.
                await Db.Collection("boundary_payments").Document(existing.PaymentId).UpdateAsync(PaymentUpdate(newAmountPaid, method, newStatus, lateFees: lateFee, receipt: receiptLink));
            }
            else
            {
                decimal lateFee = LateFeeFor(shift, null);
                expected = await GetDefaultBoundaryRateAsync() + lateFee;
                newAmountPaid = amountReceived;
                newStatus = newAmountPaid >= expected ? PaymentStatus.Paid : PaymentStatus.Partial;

                await CreatePaymentAsync(shift?.DocumentId ?? shiftId, expected - lateFee, lateFee, newAmountPaid, method, newStatus, DateTime.UtcNow, receiptLink);
            }

            return new RecordPaymentResult
            {
                Ok = true,
                NewStatus = newStatus == PaymentStatus.Paid ? SettlementStatus.Cleared : SettlementStatus.Partial,
                RemainingAfterPayment = Math.Max(0, expected - newAmountPaid),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record payment for shift {ShiftId}", shiftId);
            return new RecordPaymentResult { Ok = false, ErrorMessage = "Could not save this payment. Please try again." };
        }
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
            decimal outstandingFromShifts = driverCharges.Sum(c => c.Outstanding);

            BoundaryPayment? lastPaid = driverCharges
                .Select(c => c.Payment)
                .OfType<BoundaryPayment>()
                .Where(p => p.AmountPaid > 0)
                .OrderByDescending(p => p.Timestamp)
                .FirstOrDefault();

            return new DebtLedgerRow
            {
                DriverId = d.UserId,
                DriverName = d.FullName,
                TaxiId = string.IsNullOrWhiteSpace(d.AssignedTaxiId) ? null : d.AssignedTaxiId,
                UnpaidCount = driverCharges.Count(c => c.Outstanding > 0),
                LastPaymentDate = lastPaid?.Timestamp,
                LastPaymentAmount = lastPaid?.AmountPaid,
                TotalDebt = Math.Max(0, outstandingFromShifts + adjustmentTotal),
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
    /// A driver's full transaction history for the "Financial History" modal. Each
    /// boundary_payments document becomes one row at its current state (Expected/AmountPaid) -
    /// note a shift that received two separate partial payments over time only shows its
    /// latest combined state as a single row, not two, since RecordPaymentAsync/SettleDebtAsync
    /// both update the same document in place rather than keeping a payment-by-payment log.
    /// Each debt_adjustments document (append-only, unlike boundary_payments) becomes its own
    /// row. RunningDebt is reconstructed by replaying every row in chronological order - it
    /// isn't stored anywhere, and by construction the newest (first, since sorted newest-first
    /// for display) row's RunningDebt always equals GetDebtLedgerAsync's TotalDebt for this
    /// driver.
    /// </summary>
    public async Task<DriverLedgerHistory> GetDriverHistoryAsync(string driverId)
    {
        (List<ShiftLog> shifts, List<BoundaryPayment> payments, List<DebtAdjustment> adjustments, List<UserProfile> drivers) = await GetFullLedgerDataAsync();
        UserProfile? driver = drivers.FirstOrDefault(d => d.UserId == driverId);
        List<ShiftCharge> charges = BuildCharges(shifts, payments, await GetDefaultBoundaryRateAsync());

        var transactions = new List<LedgerTransaction>();

        foreach (ShiftCharge c in charges.Where(c => c.DriverId == driverId))
        {
            transactions.Add(new LedgerTransaction
            {
                Timestamp = c.Payment?.Timestamp ?? c.Date,
                TransactionType = c.Payment is null ? "Boundary (unpaid)" : "Boundary Payment",
                Expected = c.Expected,
                AmountPaid = c.Paid,
                ReceiptUrl = string.IsNullOrWhiteSpace(c.Payment?.EPayReceiptPhoto) ? null : c.Payment.EPayReceiptPhoto,
                ReceiptReferenceNo = c.Payment?.ReceiptReferenceNo,
            });
        }

        foreach (DebtAdjustment a in adjustments.Where(a => a.DriverId == driverId))
        {
            transactions.Add(new LedgerTransaction
            {
                Timestamp = a.Timestamp,
                TransactionType = a.Amount >= 0 ? "Penalty" : "Credit / Write-off",
                AdjustmentAmount = a.Amount,
            });
        }

        List<LedgerTransaction> chronological = transactions.OrderBy(t => t.Timestamp).ToList();
        decimal running = 0;
        foreach (LedgerTransaction tx in chronological)
        {
            running += tx.Expected.HasValue
                ? Math.Max(0, tx.Expected.Value - (tx.AmountPaid ?? 0))
                : tx.AdjustmentAmount ?? 0;
            running = Math.Max(0, running);
            tx.RunningDebt = running;
        }

        return new DriverLedgerHistory
        {
            DriverId = driverId,
            DriverName = driver?.FullName ?? driverId,
            CurrentOutstandingDebt = chronological.Count > 0 ? chronological[^1].RunningDebt : 0,
            Transactions = chronological.OrderByDescending(t => t.Timestamp).ToList(),
        };
    }

    /// <summary>
    /// Applies a lump-sum payment against a driver's total debt: oldest outstanding
    /// boundary_payment first (standard oldest-debt-first allocation), then any leftover
    /// against their net manual-adjustment debt via an automatic offsetting credit. Caps at
    /// what's actually owed - an overpayment beyond total debt is reported back
    /// (UnallocatedAmount) rather than silently recorded as a negative balance/credit.
    /// </summary>
    public async Task<SettleDebtResult> SettleDebtAsync(string driverId, decimal amountReceived, string paymentMethod, PaymentReceipt? receipt = null)
    {
        if (string.IsNullOrWhiteSpace(driverId))
        {
            return new SettleDebtResult { Ok = false, ErrorMessage = "Missing driver." };
        }

        if (amountReceived <= 0)
        {
            return new SettleDebtResult { Ok = false, ErrorMessage = "Amount received must be greater than zero." };
        }

        PaymentMethod method = string.Equals(paymentMethod, "EWallet", StringComparison.OrdinalIgnoreCase)
            ? PaymentMethod.EWallet
            : PaymentMethod.Cash;

        try
        {
            (List<ShiftLog> shifts, List<BoundaryPayment> payments, List<DebtAdjustment> adjustments, _) = await GetFullLedgerDataAsync();

            // Oldest shift first. A shift with no payment document yet gets one created - the
            // same {shiftDocumentId}_PAY document Record Payment in Daily Settlements writes.
            List<ShiftCharge> outstanding = BuildCharges(shifts, payments, await GetDefaultBoundaryRateAsync())
                .Where(c => c.DriverId == driverId && c.Outstanding > 0)
                .OrderBy(c => c.Date)
                .ToList();

            decimal remaining = amountReceived;
            DateTime now = DateTime.UtcNow;

            // One receipt can pay off several shifts - each one's payment document links it.
            var receiptLink = await UploadReceiptAsync(driverId, receipt);

            foreach (ShiftCharge charge in outstanding)
            {
                if (remaining <= 0)
                {
                    break;
                }

                decimal apply = Math.Min(remaining, charge.Outstanding);
                decimal newAmountPaid = charge.Paid + apply;
                PaymentStatus newStatus = newAmountPaid >= charge.Expected ? PaymentStatus.Paid : PaymentStatus.Partial;

                if (charge.Payment is not null)
                {
                    await Db.Collection("boundary_payments").Document(charge.Payment.PaymentId)
                        .UpdateAsync(PaymentUpdate(newAmountPaid, method, newStatus, now, lateFees: charge.LateFee, receipt: receiptLink));
                }
                else
                {
                    await CreatePaymentAsync(charge.Shift!.DocumentId, charge.Expected - charge.LateFee, charge.LateFee, newAmountPaid, method, newStatus, now, receiptLink);
                }

                remaining -= apply;
            }

            decimal adjustmentDebt = Math.Max(0, adjustments.Where(a => a.DriverId == driverId).Sum(a => a.Amount));
            if (remaining > 0 && adjustmentDebt > 0)
            {
                decimal creditApplied = Math.Min(remaining, adjustmentDebt);
                await Db.Collection("debt_adjustments").AddAsync(new DebtAdjustment
                {
                    DriverId = driverId,
                    Amount = -creditApplied,
                    Reason = "Automatic credit from a lump-sum debt settlement.",
                    Timestamp = now,
                });
                remaining -= creditApplied;
            }

            DebtLedgerSnapshot refreshed = await GetDebtLedgerAsync();
            decimal newTotal = refreshed.Rows.FirstOrDefault(r => r.DriverId == driverId)?.TotalDebt ?? 0;

            return new SettleDebtResult
            {
                Ok = true,
                RemainingDebt = newTotal,
                UnallocatedAmount = Math.Max(0, remaining),
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

    /// <summary>What one shift owes and has paid, for the Master Debt Ledger / Settle Debt.
    /// Payment is null when nobody has paid anything on the shift yet (no document).</summary>
    private sealed record ShiftCharge(string DriverId, ShiftLog? Shift, BoundaryPayment? Payment, DateTime Date, decimal Expected, decimal LateFee, decimal Paid)
    {
        public decimal Outstanding => Payment?.PaymentStatus == PaymentStatus.Paid ? 0 : Math.Max(0, Expected - Paid);
    }

    /// <summary>
    /// Every charge a driver has: one per boundary_payments document whose shift is known,
    /// plus one per ended shift with no document yet - owing the default boundary + its late
    /// fee, nothing paid. A shift still Active isn't owed yet (the boundary is paid at the end
    /// of the day), unless something was already paid on it.
    /// </summary>
    private static List<ShiftCharge> BuildCharges(List<ShiftLog> shifts, List<BoundaryPayment> payments, decimal defaultRate)
    {
        Dictionary<string, ShiftLog> shiftsById = BuildShiftLookup(shifts);
        var charges = new List<ShiftCharge>();
        var paidShiftIds = new HashSet<string>();

        foreach (BoundaryPayment p in payments)
        {
            if (!shiftsById.TryGetValue(p.ShiftId, out ShiftLog? shift))
            {
                continue;
            }

            paidShiftIds.Add(shift.DocumentId);
            decimal lateFee = LateFeeFor(shift, p);
            charges.Add(new ShiftCharge(shift.DriverId, shift, p, shift.ShiftStart ?? p.Timestamp, p.ExpectedBoundary + lateFee, lateFee, p.AmountPaid));
        }

        foreach (ShiftLog shift in shifts)
        {
            if (paidShiftIds.Contains(shift.DocumentId)
                || string.IsNullOrWhiteSpace(shift.DriverId)
                || string.Equals(shift.Status, "Active", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            decimal lateFee = LateFeeFor(shift, null);
            DateTime date = shift.ShiftStart ?? shift.ShiftEnd ?? DateTime.MinValue;
            charges.Add(new ShiftCharge(shift.DriverId, shift, null, date, defaultRate + lateFee, lateFee, 0m));
        }

        return charges;
    }

    /// <summary>A shift's first payment document ({shiftDocumentId}_PAY, the seed data's convention).</summary>
    private Task CreatePaymentAsync(string shiftDocumentId, decimal expectedBoundary, decimal lateFee, decimal amountPaid, PaymentMethod method, PaymentStatus status, DateTime timestamp, (string Url, string? ReferenceNo)? receipt = null)
    {
        string docId = $"{shiftDocumentId}_PAY";
        return Db.Collection("boundary_payments").Document(docId).SetAsync(new BoundaryPayment
        {
            PaymentId = docId,
            ShiftId = shiftDocumentId,
            ExpectedBoundary = expectedBoundary,
            LateFees = lateFee,
            AmountPaid = amountPaid,
            PaymentMethod = method,
            PaymentStatus = status,
            Timestamp = timestamp,
            EPayReceiptPhoto = receipt?.Url,
            ReceiptReferenceNo = receipt?.ReferenceNo,
        });
    }

    /// <summary>The shift's payment document. Normally at most one; if a shift ever ended up
    /// with two (one under each ID, from before both IDs were matched), the one holding the
    /// money is the one that counts.</summary>
    private static BoundaryPayment? PaymentFor(ShiftLog shift, List<BoundaryPayment> payments)
    {
        List<string> ids = IdsOf(shift);
        return payments
            .Where(p => ids.Contains(p.ShiftId))
            .OrderByDescending(p => p.AmountPaid)
            .ThenByDescending(p => p.Timestamp)
            .FirstOrDefault();
    }

    private static Dictionary<string, object> PaymentUpdate(decimal amountPaid, PaymentMethod method, PaymentStatus status, DateTime? timestamp = null, decimal? lateFees = null, (string Url, string? ReferenceNo)? receipt = null)
    {
        var update = new Dictionary<string, object>
        {
            ["amountPaid"] = (double)amountPaid,
            ["paymentMethod"] = new PaymentMethodConverter().ToFirestore(method),
            ["paymentStatus"] = new PaymentStatusConverter().ToFirestore(status),
            ["timestamp"] = timestamp ?? DateTime.UtcNow,
        };
        if (lateFees.HasValue)
        {
            update["lateFees"] = (double)lateFees.Value;
        }
        // The latest receipt paid on this shift; a cash payment leaves an earlier one in place.
        if (receipt is { } r)
        {
            update["ePayReceiptPhoto"] = r.Url;
            if (r.ReferenceNo is not null)
            {
                update["receiptReferenceNo"] = r.ReferenceNo;
            }
        }
        return update;
    }

    /// <summary>A shift's late-return fee: the one worked out at clock-out (shifts.lateFee,
    /// ShiftRules) when there is one; otherwise whatever the payment record carries (older
    /// shifts, seed data, or an amount set by hand).</summary>
    private static decimal LateFeeFor(ShiftLog? shift, BoundaryPayment? payment) =>
        shift?.LateFee is double fee ? (decimal)fee : payment?.LateFees ?? 0m;


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

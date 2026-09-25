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
    private readonly ILogger<FinancialLedgerService> _logger;

    private FirestoreDb Db => _dbLazy.Value;

    public FinancialLedgerService(Lazy<FirestoreDb> dbLazy, ILogger<FinancialLedgerService> logger)
    {
        _dbLazy = dbLazy;
        _logger = logger;
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

        // Bounded by "shifts today" (a handful of documents for this fleet size), so a
        // WhereIn lookup stays cheap - Firestore caps WhereIn at 30 values, which is far
        // more than one day's shift count will ever realistically be.
        List<string> shiftIds = todaysShifts.Select(s => s.ShiftId).Where(id => !string.IsNullOrEmpty(id)).ToList();
        List<BoundaryPayment> payments = shiftIds.Count == 0
            ? new List<BoundaryPayment>()
            : await GetWhereInAsync<BoundaryPayment>("boundary_payments", "shiftId", shiftIds);

        var rows = todaysShifts.Select(shift =>
        {
            UserProfile? driver = drivers.FirstOrDefault(d => d.UserId == shift.DriverId);
            BoundaryPayment? payment = payments.FirstOrDefault(p => p.ShiftId == shift.ShiftId);

            decimal expected = payment is not null
                ? payment.ExpectedBoundary + payment.LateFees
                : defaultRate;
            decimal paid = payment?.AmountPaid ?? 0m;

            return new SettlementRow
            {
                ShiftId = shift.ShiftId,
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
    /// Records a payment against a shift's boundary. Creates the BoundaryPayment document on
    /// its first payment (deterministic ID, matching the seed data's own {shiftId}_PAY
    /// convention) or adds to an existing one's AmountPaid on a subsequent partial payment.
    /// </summary>
    public async Task<RecordPaymentResult> RecordPaymentAsync(string shiftId, decimal amountReceived, string paymentMethod)
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

        string docId = $"{shiftId}_PAY";
        DocumentReference docRef = Db.Collection("boundary_payments").Document(docId);

        try
        {
            DocumentSnapshot snapshot = await docRef.GetSnapshotAsync();
            decimal expected;
            decimal newAmountPaid;

            if (snapshot.Exists)
            {
                BoundaryPayment existing = snapshot.ConvertTo<BoundaryPayment>();
                expected = existing.ExpectedBoundary + existing.LateFees;
                newAmountPaid = existing.AmountPaid + amountReceived;
            }
            else
            {
                expected = await GetDefaultBoundaryRateAsync();
                newAmountPaid = amountReceived;
            }

            PaymentStatus newStatus = newAmountPaid >= expected ? PaymentStatus.Paid : PaymentStatus.Partial;

            var payment = new BoundaryPayment
            {
                PaymentId = docId,
                ShiftId = shiftId,
                ExpectedBoundary = expected,
                LateFees = 0m,
                AmountPaid = newAmountPaid,
                PaymentMethod = method,
                PaymentStatus = newStatus,
                Timestamp = DateTime.UtcNow,
            };

            await docRef.SetAsync(payment, SetOptions.Overwrite);

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
    // A debt total only counts shifts that actually have a boundary_payments document
    // (Unpaid/Partial) - same lazy-creation limitation as Daily Settlements (see that tab's
    // note): a shift nobody ever attempted to pay, with no document at all, isn't visible to
    // a query over boundary_payments and so doesn't surface here either.
    // ---------------------------------------------------------------------

    public async Task<DebtLedgerSnapshot> GetDebtLedgerAsync()
    {
        (List<ShiftLog> shifts, List<BoundaryPayment> payments, List<DebtAdjustment> adjustments, List<UserProfile> drivers) = await GetFullLedgerDataAsync();
        Dictionary<string, string> shiftToDriver = BuildShiftToDriverMap(shifts);

        List<DebtLedgerRow> rows = drivers.Select(d =>
        {
            List<BoundaryPayment> driverPayments = payments.Where(p => shiftToDriver.TryGetValue(p.ShiftId, out string? did) && did == d.UserId).ToList();
            decimal adjustmentTotal = adjustments.Where(a => a.DriverId == d.UserId).Sum(a => a.Amount);

            decimal outstandingFromPayments = driverPayments
                .Where(p => p.PaymentStatus != PaymentStatus.Paid)
                .Sum(p => Math.Max(0, p.ExpectedBoundary + p.LateFees - p.AmountPaid));

            BoundaryPayment? lastPaid = driverPayments
                .Where(p => p.AmountPaid > 0)
                .OrderByDescending(p => p.Timestamp)
                .FirstOrDefault();

            return new DebtLedgerRow
            {
                DriverId = d.UserId,
                DriverName = d.FullName,
                TaxiId = string.IsNullOrWhiteSpace(d.AssignedTaxiId) ? null : d.AssignedTaxiId,
                UnpaidCount = driverPayments.Count(p => p.PaymentStatus != PaymentStatus.Paid),
                LastPaymentDate = lastPaid?.Timestamp,
                LastPaymentAmount = lastPaid?.AmountPaid,
                TotalDebt = Math.Max(0, outstandingFromPayments + adjustmentTotal),
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
        Dictionary<string, string> shiftToDriver = BuildShiftToDriverMap(shifts);

        var transactions = new List<LedgerTransaction>();

        foreach (BoundaryPayment p in payments.Where(p => shiftToDriver.TryGetValue(p.ShiftId, out string? did) && did == driverId))
        {
            transactions.Add(new LedgerTransaction
            {
                Timestamp = p.Timestamp,
                TransactionType = "Boundary Payment",
                Expected = p.ExpectedBoundary + p.LateFees,
                AmountPaid = p.AmountPaid,
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
    public async Task<SettleDebtResult> SettleDebtAsync(string driverId, decimal amountReceived, string paymentMethod)
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
            Dictionary<string, string> shiftToDriver = BuildShiftToDriverMap(shifts);

            List<BoundaryPayment> outstanding = payments
                .Where(p => shiftToDriver.TryGetValue(p.ShiftId, out string? did) && did == driverId && p.PaymentStatus != PaymentStatus.Paid)
                .OrderBy(p => p.Timestamp)
                .ToList();

            decimal remaining = amountReceived;
            DateTime now = DateTime.UtcNow;

            foreach (BoundaryPayment payment in outstanding)
            {
                if (remaining <= 0)
                {
                    break;
                }

                decimal expected = payment.ExpectedBoundary + payment.LateFees;
                decimal shortfall = Math.Max(0, expected - payment.AmountPaid);
                if (shortfall <= 0)
                {
                    continue;
                }

                decimal apply = Math.Min(remaining, shortfall);
                decimal newAmountPaid = payment.AmountPaid + apply;
                PaymentStatus newStatus = newAmountPaid >= expected ? PaymentStatus.Paid : PaymentStatus.Partial;

                var updated = new BoundaryPayment
                {
                    PaymentId = payment.PaymentId,
                    ShiftId = payment.ShiftId,
                    ExpectedBoundary = payment.ExpectedBoundary,
                    LateFees = payment.LateFees,
                    AmountPaid = newAmountPaid,
                    PaymentMethod = method,
                    PaymentStatus = newStatus,
                    ReferenceNumber = payment.ReferenceNumber,
                    EPayReceiptPhoto = payment.EPayReceiptPhoto,
                    Timestamp = now,
                };
                await Db.Collection("boundary_payments").Document(payment.PaymentId).SetAsync(updated, SetOptions.Overwrite);

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

    private static Dictionary<string, string> BuildShiftToDriverMap(List<ShiftLog> shifts) =>
        shifts.Where(s => !string.IsNullOrEmpty(s.ShiftId)).ToDictionary(s => s.ShiftId, s => s.DriverId);

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

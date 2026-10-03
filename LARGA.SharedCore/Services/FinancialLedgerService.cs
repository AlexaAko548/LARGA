using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.SharedCore.Models.FinancialLedger;
using LARGA.Shared.Models.Entities;
using Microsoft.Extensions.Logging;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Backs the ManagerWeb Financial Ledger &amp; Debts page and the "+ Adjustment" modal. Same Lazy&lt;FirestoreDb&gt;
/// pattern as FleetReportingService and DriverManagementService, for the same reason (credential failures should
/// surface inside a page's try/catch, not at DI-construction time).
///
/// <b>Payments per shift.</b> A shift can have several boundary_payments documents. Each payment is its own document
/// holding its own amount: the clock-out row, plus one <c>{shiftKey}_PAY_{yyyyMMddHHmmssfff}</c> per payment (the same
/// convention the mobile Quick Ledger writes). A shift's paid total is the sum of its documents, and whether it is
/// cleared is decided against the expected amount. Every rule in this service works from that per-shift total.
///
/// <b>debt_adjustments</b> are corrections (write-offs, fixes), plus the automatic credit a lump-sum settlement leaves
/// when it covers manual debt. They are not used to record money a driver handed over.
/// </summary>
public class FinancialLedgerService
{
    private const decimal DefaultBoundaryRate = 800m;

    // Firestore allows at most 30 values in a WhereIn clause.
    private const int WhereInLimit = 30;

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
        // 22:00 the *previous* UTC calendar date. Converting to PH time first, then truncating,
        // gets the boundary right; the query itself still runs in UTC.
        DateTime phDate = dateUtc.ToPhilippineTime().Date;
        DateTime dayStart = phDate - PhilippineTime.Offset;
        DateTime dayEnd = dayStart.AddDays(1).AddTicks(-1);

        List<ShiftLog> todaysShifts = await GetBetweenAsync<ShiftLog>("shifts", "shiftStart", dayStart, dayEnd);
        List<UserProfile> drivers = await GetDriversAsync();
        decimal defaultRate = await GetDefaultBoundaryRateAsync();

        // A shift's payment documents are matched by business ID or document ID, because clock-out can store either.
        List<string> keys = todaysShifts
            .SelectMany(s => new[] { s.ShiftId, s.DocumentId })
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct()
            .ToList();
        List<BoundaryPayment> payments = await GetWhereInChunkedAsync<BoundaryPayment>("boundary_payments", "shiftId", keys);

        var rows = todaysShifts.Select(shift =>
        {
            UserProfile? driver = drivers.FirstOrDefault(d => d.UserId == shift.DriverId);
            ShiftBoundary boundary = BoundaryFor(shift, payments, defaultRate);

            return new SettlementRow
            {
                ShiftId = shift.ShiftId,
                DriverId = shift.DriverId,
                DriverName = driver?.FullName ?? shift.DriverId,
                TaxiId = shift.TaxiId,
                ShiftEnd = shift.ShiftEnd,
                ExpectedTotal = boundary.Expected,
                AmountPaid = boundary.Paid,
                Status = ComputeStatus(boundary.Paid, boundary.Expected),
            };
        })
        .OrderBy(r => r.Status == SettlementStatus.Cleared ? 1 : 0)
        .ThenBy(r => r.DriverName)
        .ToList();

        return new DailySettlementSnapshot
        {
            Date = phDate,
            ExpectedCollection = rows.Sum(r => r.ExpectedTotal),
            CollectedSoFar = rows.Sum(r => r.AmountPaid),
            ClearedCount = rows.Count(r => r.Status == SettlementStatus.Cleared),
            PartialCount = rows.Count(r => r.Status == SettlementStatus.Partial),
            WaitingCount = rows.Count(r => r.Status == SettlementStatus.Waiting),
            Rows = rows,
        };
    }

    private static SettlementStatus ComputeStatus(decimal paid, decimal expected)
    {
        if (expected > 0 && paid >= expected)
        {
            return SettlementStatus.Cleared;
        }

        return paid > 0 ? SettlementStatus.Partial : SettlementStatus.Waiting;
    }

    /// <summary>
    /// Records a payment against a shift's boundary. Every payment is a new document holding its own amount, so no
    /// earlier payment is overwritten. The shift's status is decided from the total of all its documents.
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

        try
        {
            (List<ShiftLog> shifts, List<BoundaryPayment> payments, _, _) = await GetFullLedgerDataAsync();
            decimal defaultRate = await GetDefaultBoundaryRateAsync();

            ShiftLog? shift = shifts.FirstOrDefault(s => s.ShiftId == shiftId);
            ShiftBoundary boundary = shift is null
                ? new ShiftBoundary(defaultRate, 0m, false, null, Array.Empty<BoundaryPayment>())
                : BoundaryFor(shift, payments, defaultRate);

            decimal paidAfter = boundary.Paid + amountReceived;
            PaymentStatus newStatus = boundary.Expected > 0 && paidAfter >= boundary.Expected ? PaymentStatus.Paid : PaymentStatus.Partial;

            DateTime now = DateTime.UtcNow;
            string docId = $"{shiftId}_PAY_{now:yyyyMMddHHmmssfff}";

            var payment = new BoundaryPayment
            {
                PaymentId = docId,
                ShiftId = shiftId,
                ExpectedBoundary = boundary.Expected,
                LateFees = 0m,
                AmountPaid = amountReceived,
                PaymentMethod = method,
                PaymentStatus = newStatus,
                Timestamp = now,
            };

            await Db.Collection("boundary_payments").Document(docId).SetAsync(payment, SetOptions.Overwrite);

            return new RecordPaymentResult
            {
                Ok = true,
                NewStatus = newStatus == PaymentStatus.Paid ? SettlementStatus.Cleared : SettlementStatus.Partial,
                RemainingAfterPayment = Math.Max(0, boundary.Expected - paidAfter),
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
    // This tab's purpose is an all-time reckoning of what each driver owes, so it reads shifts and payments in full.
    // A driver's debt is the unpaid balance of each shift that has payment records, plus net manual adjustments.
    // A shift with no payment record at all is not counted, the same limitation the mobile Other Payment list has.
    // ---------------------------------------------------------------------

    public async Task<DebtLedgerSnapshot> GetDebtLedgerAsync()
    {
        (List<ShiftLog> shifts, List<BoundaryPayment> payments, List<DebtAdjustment> adjustments, List<UserProfile> drivers) = await GetFullLedgerDataAsync();
        decimal defaultRate = await GetDefaultBoundaryRateAsync();

        List<DebtLedgerRow> rows = drivers.Select(d =>
        {
            List<ShiftBoundary> driverShifts = ShiftBoundariesFor(shifts, payments, defaultRate)
                .Where(b => b.Shift?.DriverId == d.UserId && b.HasRecord)
                .ToList();
            decimal adjustmentTotal = adjustments.Where(a => a.DriverId == d.UserId).Sum(a => a.Amount);

            decimal outstandingFromShifts = driverShifts.Sum(b => Math.Max(0, b.Expected - b.Paid));

            // The most recent payment with a positive amount, across every document of this driver's shifts.
            BoundaryPayment? lastPaid = payments
                .Where(p => p.AmountPaid > 0 && driverShifts.Any(b => b.Docs.Any(doc => doc.PaymentId == p.PaymentId)))
                .OrderByDescending(p => p.Timestamp)
                .FirstOrDefault();

            return new DebtLedgerRow
            {
                DriverId = d.UserId,
                DriverName = d.FullName,
                TaxiId = string.IsNullOrWhiteSpace(d.AssignedTaxiId) ? null : d.AssignedTaxiId,
                UnpaidCount = driverShifts.Count(b => !b.IsCleared),
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
    /// A driver's transaction history for the "Financial History" modal. Each boundary_payments document is one row with
    /// its own amount, so each payment shows separately. Each debt_adjustments document is one row. RunningDebt is
    /// replayed in time order: a shift adds its expected amount when its first record appears, each payment reduces it,
    /// and each adjustment moves it. The newest row's RunningDebt matches GetDebtLedgerAsync's TotalDebt for the driver.
    /// </summary>
    public async Task<DriverLedgerHistory> GetDriverHistoryAsync(string driverId)
    {
        (List<ShiftLog> shifts, List<BoundaryPayment> payments, List<DebtAdjustment> adjustments, List<UserProfile> drivers) = await GetFullLedgerDataAsync();
        decimal defaultRate = await GetDefaultBoundaryRateAsync();
        UserProfile? driver = drivers.FirstOrDefault(d => d.UserId == driverId);

        var events = new List<(DateTime Time, int Order, LedgerTransaction Row, decimal? ShiftExpectedOnFirstRecord, string? ShiftKey)>();
        var driverBoundaries = ShiftBoundariesFor(shifts, payments, defaultRate)
            .Where(b => b.Shift?.DriverId == driverId && b.HasRecord)
            .ToList();

        foreach (ShiftBoundary boundary in driverBoundaries)
        {
            string shiftKey = boundary.Shift!.ShiftId;
            foreach (BoundaryPayment p in boundary.Docs)
            {
                events.Add((p.Timestamp, 0, new LedgerTransaction
                {
                    Timestamp = p.Timestamp,
                    TransactionType = "Boundary Payment",
                    Expected = boundary.Expected,
                    AmountPaid = p.AmountPaid,
                }, null, shiftKey));
            }
        }

        foreach (DebtAdjustment a in adjustments.Where(a => a.DriverId == driverId))
        {
            events.Add((a.Timestamp, 1, new LedgerTransaction
            {
                Timestamp = a.Timestamp,
                TransactionType = a.Amount >= 0 ? "Penalty" : "Credit / Write-off",
                AdjustmentAmount = a.Amount,
            }, null, null));
        }

        // Replay: the first record of a shift brings its expected amount into the running debt, once.
        var chronological = events.OrderBy(e => e.Time).ThenBy(e => e.Order).ToList();
        var shiftsSeen = new HashSet<string>();
        decimal running = 0;
        foreach (var e in chronological)
        {
            LedgerTransaction tx = e.Row;
            if (e.ShiftKey is not null && shiftsSeen.Add(e.ShiftKey))
            {
                running += tx.Expected ?? 0;
            }

            running += tx.Expected.HasValue
                ? -(tx.AmountPaid ?? 0)
                : tx.AdjustmentAmount ?? 0;
            running = Math.Max(0, running);
            tx.RunningDebt = running;
        }

        List<LedgerTransaction> rows = chronological.Select(e => e.Row).ToList();

        return new DriverLedgerHistory
        {
            DriverId = driverId,
            DriverName = driver?.FullName ?? driverId,
            CurrentOutstandingDebt = rows.Count > 0 ? rows[^1].RunningDebt : 0,
            Transactions = rows.OrderByDescending(t => t.Timestamp).ToList(),
        };
    }

    /// <summary>
    /// Applies a lump-sum payment against a driver's total debt: the oldest unpaid shift first, then any leftover against
    /// the driver's positive manual debt, via an automatic credit. Each amount applied is written as a new payment
    /// document, the same as a single payment. Caps at what's owed; any overpayment is reported as UnallocatedAmount.
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
            decimal defaultRate = await GetDefaultBoundaryRateAsync();

            List<ShiftBoundary> outstanding = ShiftBoundariesFor(shifts, payments, defaultRate)
                .Where(b => b.Shift?.DriverId == driverId && b.HasRecord && !b.IsCleared)
                .OrderBy(b => b.Shift!.ShiftStart ?? DateTime.MaxValue)
                .ToList();

            decimal remaining = amountReceived;
            DateTime now = DateTime.UtcNow;
            int index = 0;

            foreach (ShiftBoundary boundary in outstanding)
            {
                if (remaining <= 0) break;

                decimal shortfall = Math.Max(0, boundary.Expected - boundary.Paid);
                decimal apply = Math.Min(remaining, shortfall);
                if (apply <= 0) continue;

                string shiftKey = boundary.Shift!.ShiftId;
                string docId = $"{shiftKey}_PAY_{now:yyyyMMddHHmmssfff}_{index++}";
                bool cleared = boundary.Paid + apply >= boundary.Expected;

                await Db.Collection("boundary_payments").Document(docId).SetAsync(new BoundaryPayment
                {
                    PaymentId = docId,
                    ShiftId = shiftKey,
                    ExpectedBoundary = boundary.Expected,
                    LateFees = 0m,
                    AmountPaid = apply,
                    PaymentMethod = method,
                    PaymentStatus = cleared ? PaymentStatus.Paid : PaymentStatus.Partial,
                    Timestamp = now,
                }, SetOptions.Overwrite);

                remaining -= apply;
            }

            // Leftover goes against positive manual debt, as an automatic credit (a correction, not a collection).
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

    // ---------------------------------------------------------------------
    // Per-shift boundary
    // ---------------------------------------------------------------------

    /// <summary>One shift's boundary: its expected amount, the total paid across its documents, and those documents.</summary>
    private sealed record ShiftBoundary(decimal Expected, decimal Paid, bool HasRecord, ShiftLog? Shift, IReadOnlyList<BoundaryPayment> Docs)
    {
        public bool IsCleared => Expected > 0 && Paid >= Expected;
    }

    private static ShiftBoundary BoundaryFor(ShiftLog shift, IReadOnlyList<BoundaryPayment> payments, decimal defaultRate)
    {
        List<BoundaryPayment> docs = PaymentsFor(shift, payments);
        return BuildBoundary(shift, docs, defaultRate);
    }

    private static IEnumerable<ShiftBoundary> ShiftBoundariesFor(IEnumerable<ShiftLog> shifts, IReadOnlyList<BoundaryPayment> payments, decimal defaultRate) =>
        shifts.Select(shift => BuildBoundary(shift, PaymentsFor(shift, payments), defaultRate));

    private static ShiftBoundary BuildBoundary(ShiftLog shift, List<BoundaryPayment> docs, decimal defaultRate)
    {
        // The expected amount comes from the newest document that has one, else the default rate.
        BoundaryPayment? target = docs.OrderByDescending(p => p.Timestamp).FirstOrDefault();
        decimal expected = target is not null && target.ExpectedBoundary > 0
            ? target.ExpectedBoundary + target.LateFees
            : defaultRate;

        decimal paid = docs.Sum(p => p.AmountPaid);
        return new ShiftBoundary(expected, paid, docs.Count > 0, shift, docs);
    }

    /// <summary>A shift's payment documents, matched by business ID or document ID.</summary>
    private static List<BoundaryPayment> PaymentsFor(ShiftLog shift, IReadOnlyList<BoundaryPayment> payments) =>
        payments
            .Where(p => (!string.IsNullOrEmpty(shift.ShiftId) && p.ShiftId == shift.ShiftId)
                     || (!string.IsNullOrEmpty(shift.DocumentId) && p.ShiftId == shift.DocumentId))
            .ToList();

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

    private async Task<(List<ShiftLog> Shifts, List<BoundaryPayment> Payments, List<DebtAdjustment> Adjustments, List<UserProfile> Drivers)> GetFullLedgerDataAsync()
    {
        List<ShiftLog> shifts = await GetAllAsync<ShiftLog>("shifts");
        List<BoundaryPayment> payments = await GetAllAsync<BoundaryPayment>("boundary_payments");
        List<DebtAdjustment> adjustments = await GetAllAsync<DebtAdjustment>("debt_adjustments");
        List<UserProfile> drivers = await GetDriversAsync();
        return (shifts, payments, adjustments, drivers);
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

    private async Task<List<T>> GetWhereInChunkedAsync<T>(string collection, string field, List<string> values) where T : class
    {
        var results = new List<T>();
        for (int i = 0; i < values.Count; i += WhereInLimit)
        {
            List<string> chunk = values.Skip(i).Take(WhereInLimit).ToList();
            QuerySnapshot snapshot = await Db.Collection(collection).WhereIn(field, chunk).GetSnapshotAsync();
            results.AddRange(ConvertDocuments<T>(snapshot, collection));
        }
        return results;
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LARGA.SharedCore.Ledger;

namespace LARGA.MobileApp.Services;

/// <summary>
/// The signed-in driver's debt for the Ledger, Debt Details and Payment History screens. The
/// math is <see cref="QuickLedgerCalculator.DriverDebt"/> - the same calculator the manager's
/// Quick Ledger uses, following ManagerWeb's Master Debt Ledger (FinancialLedgerService) - so the
/// driver, the manager app and the web all show the same number: every ended shift (or one
/// already paid on) owes its boundary + late fee + fuel penalty, minus what its boundary_payments
/// documents add up to; overpayment credit pays off the oldest unpaid shifts first; manual
/// debt_adjustments are added on.
///
/// This class only reads and reshapes. It reads just what the Firestore rules let a driver read:
/// their own shifts, boundary_payments and debt_adjustments (each by driverId), and
/// system_configs - through QuickLedgerService's readers, filtered to this driver.
/// </summary>
public static class DriverDebtCalculator
{
    public sealed class ShiftEntry
    {
        public DateTime ShiftStartUtc { get; init; }
        public string TaxiId { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public decimal Expected { get; init; }
        public decimal Paid { get; init; }
        public bool HasPayment { get; init; }

        /// <summary>"Paid", "Partial" or "Unpaid" (boundary_payments.paymentStatus).</summary>
        public string PaymentStatus { get; init; } = "Unpaid";
        public DateTime? PaymentTimestampUtc { get; init; }

        /// <summary>What this shift's own records still show unpaid (before overpayment credit).</summary>
        public decimal Outstanding { get; init; }
    }

    public sealed class AdjustmentEntry
    {
        public DateTime TimestampUtc { get; init; }
        public decimal Amount { get; init; }
    }

    public sealed class Result
    {
        public List<ShiftEntry> Shifts { get; } = new();
        public List<AdjustmentEntry> Adjustments { get; } = new();

        /// <summary>Overpayment credit, which pays off the oldest unpaid shifts first.</summary>
        public decimal Credit { get; init; }

        /// <summary>Everything the driver owes - the web's Master Debt Ledger TotalDebt.</summary>
        public decimal TotalDebt { get; init; }
    }

    // Ledger, Debt Details and Payment History show the same records, usually opened one after another:
    // one load serves all three for a minute (PERFORMANCE_REPORT.md P6). Pull-to-refresh passes
    // forceRefresh, and logout clears it (SignOutService), so no stale or other driver's data is shown.
    private static readonly TimeSpan SharedResultLifetime = TimeSpan.FromSeconds(60);
    private static readonly object CacheLock = new();
    private static (string DriverId, DateTime LoadedUtc, Task<Result> Load)? _cached;

    /// <summary>The driver's debt picture - shared with the other ledger screens for a minute.</summary>
    public static Task<Result> LoadAsync(string driverId, bool forceRefresh = false)
    {
        lock (CacheLock)
        {
            if (!forceRefresh && _cached is { } cached && cached.DriverId == driverId
                && DateTime.UtcNow - cached.LoadedUtc < SharedResultLifetime && !cached.Load.IsFaulted)
            {
                return cached.Load;
            }

            Task<Result> load = ReadAsync(driverId);
            _cached = (driverId, DateTime.UtcNow, load);
            return load;
        }
    }

    public static void ClearCache()
    {
        lock (CacheLock)
        {
            _cached = null;
        }
    }

    private static async Task<Result> ReadAsync(string driverId)
    {
        // The four reads don't depend on each other, so they run together; List<T> isn't thread-safe,
        // so each reader gets its own warnings list.
        var shiftWarnings = new List<string>();
        var paymentWarnings = new List<string>();
        var adjustmentWarnings = new List<string>();
        Task<IReadOnlyList<ShiftRecord>> shifts = QuickLedgerService.ReadShiftsAsync(shiftWarnings, driverId);
        Task<IReadOnlyList<BoundaryPaymentRecord>> payments = QuickLedgerService.ReadPaymentsAsync(paymentWarnings, driverId);
        Task<IReadOnlyList<DebtAdjustmentRecord>> adjustments = QuickLedgerService.ReadAdjustmentsAsync(adjustmentWarnings, driverId);
        Task<decimal> rate = QuickLedgerService.ReadDefaultBoundaryRateAsync();
        await Task.WhenAll(shifts, payments, adjustments, rate);

        var warnings = shiftWarnings.Concat(paymentWarnings).Concat(adjustmentWarnings).ToList();
        var input = new QuickLedgerInput(
            Shifts: shifts.Result,
            Payments: payments.Result,
            Adjustments: adjustments.Result,
            DriverNames: new Dictionary<string, string>(),
            DriverIds: new[] { driverId },
            TaxiPlates: new Dictionary<string, string>(),
            DefaultBoundaryRate: rate.Result);

        foreach (string warning in warnings)
        {
            System.Diagnostics.Debug.WriteLine($"Driver ledger: {warning}");
        }

        DriverDebtResult debt = QuickLedgerCalculator.DriverDebt(input, driverId);

        var result = new Result { Credit = debt.OverpaymentCredit, TotalDebt = debt.TotalDebt };
        result.Shifts.AddRange(debt.Shifts.Select(s => new ShiftEntry
        {
            ShiftStartUtc = s.Shift.StartUtc is DateTime start && start.Year > 2000 ? start : DateTime.MinValue,
            TaxiId = s.Shift.TaxiId,
            IsActive = string.Equals(s.Shift.Status, "Active", StringComparison.OrdinalIgnoreCase),
            Expected = s.Expected,
            Paid = s.Paid,
            HasPayment = s.HasPayment,
            PaymentStatus = s.PaymentStatus,
            PaymentTimestampUtc = s.LatestPaymentUtc is DateTime paid && paid.Year > 2000 ? paid : null,
            Outstanding = s.Remaining,
        }));
        result.Adjustments.AddRange(debt.Adjustments.Select(a => new AdjustmentEntry
        {
            TimestampUtc = a.TimestampUtc is DateTime at && at.Year > 2000 ? at : DateTime.MinValue,
            Amount = a.Amount,
        }));

        return result;
    }
}

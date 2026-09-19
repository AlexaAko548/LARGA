using System;
using System.Collections.Generic;
using System.Linq;

namespace LARGA.SharedCore.Models.FinancialLedger;

public enum SettlementStatus
{
    /// <summary>No payment recorded yet for this shift - includes shifts still "on road".</summary>
    Waiting,
    Partial,
    Cleared,
}

public class SettlementRow
{
    public string ShiftId { get; set; } = string.Empty;
    public string DriverId { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;

    /// <summary>Shift start used by mobile quick-ledger due-window logic.</summary>
    public DateTime? ShiftStart { get; set; }

    /// <summary>Null while the shift is still active ("On road" in the UI).</summary>
    public DateTime? ShiftEnd { get; set; }

    /// <summary>ExpectedBoundary + LateFees folded together - the mockup shows one "Boundary" column, not two.</summary>
    public decimal ExpectedTotal { get; set; }

    public decimal AmountPaid { get; set; }
    public decimal Balance => Math.Max(0, ExpectedTotal - AmountPaid);
    public SettlementStatus Status { get; set; }
}

public class DailySettlementSnapshot
{
    public DateTime Date { get; set; }
    public decimal ExpectedCollection { get; set; }
    public decimal CollectedSoFar { get; set; }
    public decimal StillOutstanding => Math.Max(0, ExpectedCollection - CollectedSoFar);
    public double PercentCollected => ExpectedCollection <= 0 ? 0 : (double)(CollectedSoFar / ExpectedCollection) * 100;

    public int ClearedCount { get; set; }
    public int PartialCount { get; set; }
    public int WaitingCount { get; set; }

    public List<SettlementRow> Rows { get; set; } = new();
}

public class RecordPaymentResult
{
    public bool Ok { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>What the row's status became after this payment was applied - drives the modal's live preview.</summary>
    public SettlementStatus NewStatus { get; set; }
    public decimal RemainingAfterPayment { get; set; }
}

public class AdjustmentResult
{
    public bool Ok { get; set; }
    public string? ErrorMessage { get; set; }
}

// ---------------------------------------------------------------------
// Master Debt Ledger
// ---------------------------------------------------------------------

public class DebtLedgerRow
{
    public string DriverId { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string? TaxiId { get; set; }

    /// <summary>Count of this driver's boundary_payments not yet fully Paid, across all history.</summary>
    public int UnpaidCount { get; set; }

    /// <summary>Most recent payment with AmountPaid > 0, across ALL this driver's payments (not just outstanding ones) - null if they've never paid anything.</summary>
    public DateTime? LastPaymentDate { get; set; }
    public decimal? LastPaymentAmount { get; set; }

    /// <summary>Outstanding boundary balance + net manual adjustments, floored at 0.</summary>
    public decimal TotalDebt { get; set; }
}

public class DebtLedgerSnapshot
{
    public int DriversWithDebtCount { get; set; }
    public decimal TotalOutstandingDebt { get; set; }
    public int DebtFreeDriverCount { get; set; }
    public List<DebtLedgerRow> Rows { get; set; } = new();
}

public class LedgerTransaction
{
    public DateTime Timestamp { get; set; }

    /// <summary>"Boundary Payment", "Penalty", or "Credit / Write-off".</summary>
    public string TransactionType { get; set; } = string.Empty;

    /// <summary>Null for adjustment rows - only boundary-payment rows have an expected amount.</summary>
    public decimal? Expected { get; set; }
    public decimal? AmountPaid { get; set; }

    /// <summary>Null for boundary-payment rows. Signed: positive = penalty, negative = credit.</summary>
    public decimal? AdjustmentAmount { get; set; }

    /// <summary>Cumulative outstanding debt after this transaction, floored at 0 (see FinancialLedgerService for how it's built).</summary>
    public decimal RunningDebt { get; set; }
}

public class DriverLedgerHistory
{
    public string DriverId { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public decimal CurrentOutstandingDebt { get; set; }

    /// <summary>Newest first.</summary>
    public List<LedgerTransaction> Transactions { get; set; } = new();
}

public class SettleDebtResult
{
    public bool Ok { get; set; }
    public string? ErrorMessage { get; set; }
    public decimal RemainingDebt { get; set; }

    /// <summary>Amount left over after paying off every boundary_payment record and adjustment credit - a manager-side overpayment beyond what was actually owed.</summary>
    public decimal UnallocatedAmount { get; set; }
}

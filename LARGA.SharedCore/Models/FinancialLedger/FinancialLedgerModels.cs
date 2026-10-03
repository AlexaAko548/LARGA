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

    /// <summary>Null while the shift is still active ("On road" in the UI).</summary>
    public DateTime? ShiftEnd { get; set; }

    /// <summary>ExpectedBoundary + late fee + fuel penalty folded together - the mockup shows one "Boundary" column.</summary>
    public decimal ExpectedTotal { get; set; }

    /// <summary>The parts of ExpectedTotal on top of the boundary, for the Record Payment breakdown.</summary>
    public decimal LateFee { get; set; }
    public decimal FuelPenalty { get; set; }

    public decimal AmountPaid { get; set; }

    /// <summary>The part of this shift's balance covered by the driver's overpayment credit
    /// (paid above another shift's total) - the same credit the Master Debt Ledger applies.</summary>
    public decimal CreditApplied { get; set; }

    public decimal Balance => Status == SettlementStatus.Cleared ? 0 : Math.Max(0, ExpectedTotal - AmountPaid - CreditApplied);
    public SettlementStatus Status { get; set; }
}

public class DailySettlementSnapshot
{
    public DateTime Date { get; set; }
    public decimal ExpectedCollection { get; set; }
    public decimal CollectedSoFar { get; set; }

    /// <summary>Covered by overpayment credit rather than money received on these shifts.</summary>
    public decimal CreditApplied { get; set; }

    public decimal StillOutstanding => Rows.Sum(r => r.Balance);
    public double PercentCollected => ExpectedCollection <= 0 ? 0 : (double)((CollectedSoFar + CreditApplied) / ExpectedCollection) * 100;

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

    /// <summary>The part of the payment above this shift's balance, applied to the driver's
    /// older unpaid shifts / adjustment debt.</summary>
    public decimal AppliedToOlderDebt { get; set; }

    /// <summary>Paid above everything the driver owed - kept as advance credit toward their
    /// next boundary.</summary>
    public decimal AdvanceCredit { get; set; }
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

    /// <summary>Days since the oldest shift that still has a balance (0 when none).</summary>
    public int DaysUnpaid { get; set; }

    /// <summary>Unpaid for ShiftRules.DebtFlagDays or more - flagged for the manager to review.</summary>
    public bool IsDebtFlagged { get; set; }

    /// <summary>Paid in advance, not yet used by a boundary (0 when the driver owes anything).</summary>
    public decimal AdvanceCredit { get; set; }
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

    /// <summary>Boundary rows: how this shift moves the driver's debt - what it still owes, or
    /// minus its overpayment credit. Null for adjustment rows (see AdjustmentAmount).</summary>
    public decimal? DebtChange { get; set; }

    /// <summary>Cumulative outstanding debt after this transaction, floored at 0 (see FinancialLedgerService for how it's built).</summary>
    public decimal RunningDebt { get; set; }

    /// <summary>The GCash receipt image the payment was recorded with, if any.</summary>
    public string? ReceiptUrl { get; set; }
    public string? ReceiptReferenceNo { get; set; }
}

/// <summary>A GCash receipt attached to an E-Wallet payment: the uploaded image, stored in
/// Firebase Storage and linked from the boundary_payments document(s) it paid.</summary>
public record PaymentReceipt(byte[] Image, string ContentType, string? ReferenceNo);

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

    /// <summary>Paid above the driver's total debt - kept as advance credit toward their next boundary.</summary>
    public decimal AdvanceCredit { get; set; }
}

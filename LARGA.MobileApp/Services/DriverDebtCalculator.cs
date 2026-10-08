using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using LARGA.SharedCore;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.Services;

/// <summary>
/// The signed-in driver's debt, worked out the same way as ManagerWeb's Master Debt Ledger
/// (FinancialLedgerService.BuildCharges) so the phone and the manager see the same number:
/// every ended shift owes its boundary + late fee + fuel penalty, minus what's been paid on
/// its boundary_payments documents - one per payment, added up by BoundaryPaymentRules (a
/// shift nobody has paid anything on has no document and owes it all); minus any overpayment
/// credit (paid above a shift's total); plus the net of the
/// driver's manual debt_adjustments. A shift still Active
/// isn't owed yet.
///
/// Reads only what the Firestore rules let a driver read: their own shifts, boundary_payments
/// and debt_adjustments (each by driverId), and system_configs.
/// </summary>
public static class DriverDebtCalculator
{
    private const decimal FallbackBoundaryRate = ShiftRules.DefaultBoundaryRate;

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

        public decimal Outstanding => IsActive || PaymentStatus == "Paid" ? 0m : Math.Max(0m, Expected - Paid);

        /// <summary>Paid above the shift's total - credit toward older debt (web: ShiftCharge.Overpaid).</summary>
        public decimal Overpaid => HasPayment ? Math.Max(0m, Paid - Expected) : 0m;
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
        public decimal Credit => Shifts.Sum(s => s.Overpaid);

        public decimal TotalDebt => Math.Max(0m, Shifts.Sum(s => s.Outstanding) - Credit + Adjustments.Sum(a => a.Amount));
    }

    public static async Task<Result> LoadAsync(string driverId)
    {
        var result = new Result();
        IFirebaseFirestore db = CrossFirebaseFirestore.Current;
        decimal defaultRate = await GetDefaultBoundaryRateAsync(db);

        // Typed proxies, not Dictionary<string, object>: Plugin.Firebase hands back an empty
        // dictionary for the latter, which read every shift as unpaid and every payment as ₱0.
        // Their fields are object?, not double/DateTime: see the proxy classes below.
        var shifts = await db.GetCollection("shifts")
            .WhereEqualsTo("driverId", driverId)
            .GetDocumentsAsync<ShiftProxy>();

        // All of this driver's payment documents in one read. firestore.rules only let a driver
        // read payments carrying their own driverId (stamped on every write, and backfilled on
        // older documents by ManagerWeb's PaymentDriverIdBackfillService).
        var myPayments = await db.GetCollection("boundary_payments")
            .WhereEqualsTo("driverId", driverId)
            .GetDocumentsAsync<PaymentProxy>();

        foreach (var shiftDoc in shifts.Documents)
        {
            ShiftProxy? shift = shiftDoc.Data;
            if (shift == null)
            {
                System.Diagnostics.Debug.WriteLine($"Driver ledger: shift {shiftDoc.Reference.Id} could not be read");
                continue;
            }

            // A payment can point at either of the shift's IDs (document ID, or the shiftId
            // field older builds filled with a made-up value) - same matching as the web.
            var ids = new[] { shiftDoc.Reference.Id, StringOf(shift.ShiftId) }
                .Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();

            // Every payment is its own document, so a shift can have several; they're added up
            // the same way as on the web (BoundaryPaymentRules.TotalPaid), and the most recent one
            // carries the expected boundary and Paid/Partial status.
            var documents = new Dictionary<string, PaymentProxy>();
            foreach (var doc in myPayments.Documents)
            {
                if (doc.Data != null && ids.Contains(StringOf(doc.Data.ShiftId))) documents[doc.Reference.Id] = doc.Data;
            }

            PaymentProxy? payment = documents.Values
                .OrderByDescending(d => PaidAtUtc(d) ?? DateTime.MinValue)
                .FirstOrDefault();
            decimal totalPaid = BoundaryPaymentRules.TotalPaid(
                documents.Select(kv => (kv.Key, (string?)StringOf(kv.Value.TransactionId), ToDecimal(kv.Value.AmountPaid) ?? 0m)));

            bool isActive = StringOf(shift.Status) == "Active";
            if (isActive && payment == null) continue;

            // Charges from the shift itself when it has them (set at clock-out, even when 0);
            // otherwise whatever the payment record carries - same as the web's ExtrasFor.
            // Late fee: stored, else computed from the server-stamped start/end (ShiftRules).
            decimal lateFee = ShiftRules.EffectiveLateFee(ToDecimal(shift.LateFee), ToUtc(shift.ShiftStart), ToUtc(shift.ShiftEnd))
                ?? ToDecimal(payment?.LateFees) ?? 0m;
            decimal fuelPenalty = ToDecimal(shift.FuelPenalty) ?? ToDecimal(payment?.FuelPenalty) ?? 0m;
            decimal expectedBoundary = ToDecimal(payment?.ExpectedBoundary) ?? 0m;
            decimal boundary = expectedBoundary > 0 ? expectedBoundary : defaultRate;

            DateTime? start = ToUtc(shift.ShiftStart);
            DateTime? paidAt = payment == null ? null : PaidAtUtc(payment);
            string paymentStatus = StringOf(payment?.PaymentStatus);

            result.Shifts.Add(new ShiftEntry
            {
                ShiftStartUtc = start is DateTime s && s.Year > 2000 ? s : DateTime.MinValue,
                TaxiId = StringOf(shift.TaxiId),
                IsActive = isActive,
                Expected = boundary + lateFee + fuelPenalty,
                Paid = totalPaid,
                HasPayment = payment != null,
                PaymentStatus = string.IsNullOrEmpty(paymentStatus) ? "Unpaid" : paymentStatus,
                PaymentTimestampUtc = paidAt,
            });
        }

        var adjustments = await db.GetCollection("debt_adjustments")
            .WhereEqualsTo("driverId", driverId)
            .GetDocumentsAsync<AdjustmentProxy>();
        foreach (var doc in adjustments.Documents)
        {
            if (doc.Data == null) continue;
            DateTime? at = ToUtc(doc.Data.Timestamp);
            result.Adjustments.Add(new AdjustmentEntry
            {
                TimestampUtc = at is DateTime a && a.Year > 2000 ? a : DateTime.MinValue,
                Amount = ToDecimal(doc.Data.Amount) ?? 0m,
            });
        }

        return result;
    }

    // Firestore hands back a whole number as an integer and Plugin.Firebase then fails to fill a
    // double property (the whole document reads as null, and the shift or payment silently drops
    // out of the ledger), so every proxy field is object? - see QuickLedgerService. Null means the
    // document has no such field.
    private static decimal? ToDecimal(object? value) => value switch
    {
        null => null,
        double d => (decimal)d,
        float f => (decimal)f,
        long l => l,
        int i => i,
        decimal m => m,
        string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed) => parsed,
        _ => null,
    };

    private static string StringOf(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>
    /// A Firestore timestamp as UTC. Plugin.Firebase can hand back a DateTimeOffset, a DateTime
    /// (with the Android 1601 problem, see FirestoreDateTimeFix), or an ISO string.
    /// </summary>
    private static DateTime? ToUtc(object? value) => value switch
    {
        DateTimeOffset offset => offset.UtcDateTime,
        DateTime dt => ToUtcKind(FirestoreDateTimeFix.Apply(dt)),
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed) => parsed,
        _ => null,
    };

    private static DateTime ToUtcKind(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>
    /// When a payment was made. The Quick Ledger also writes recordedAtUtc as an ISO string because
    /// its timestamp write wasn't reliable on Android, so that one wins (same as QuickLedgerService).
    /// </summary>
    private static DateTime? PaidAtUtc(PaymentProxy payment)
    {
        DateTime? at = ToUtc(payment.RecordedAtUtc) ?? ToUtc(payment.Timestamp);
        return at is DateTime d && d.Year > 2000 ? d : null;
    }

    private static async Task<decimal> GetDefaultBoundaryRateAsync(IFirebaseFirestore db)
    {
        try
        {
            var config = await db.GetCollection("system_configs").GetDocument("global")
                .GetDocumentSnapshotAsync<ConfigProxy>();
            decimal rate = ToDecimal(config?.Data?.DefaultBoundaryRate) ?? 0m;
            if (rate > 0)
            {
                return rate;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Boundary Rate Error: {ex.Message}");
        }
        return FallbackBoundaryRate;
    }

    private class ShiftProxy
    {
        [FirestoreProperty("shiftId")] public object? ShiftId { get; set; }
        [FirestoreProperty("taxiId")] public object? TaxiId { get; set; }
        [FirestoreProperty("status")] public object? Status { get; set; }
        [FirestoreProperty("shiftStart")] public object? ShiftStart { get; set; }
        [FirestoreProperty("shiftEnd")] public object? ShiftEnd { get; set; }
        [FirestoreProperty("lateFee")] public object? LateFee { get; set; }
        [FirestoreProperty("fuelPenalty")] public object? FuelPenalty { get; set; }
    }

    private class PaymentProxy
    {
        [FirestoreProperty("shiftId")] public object? ShiftId { get; set; }
        [FirestoreProperty("expectedBoundary")] public object? ExpectedBoundary { get; set; }
        [FirestoreProperty("lateFees")] public object? LateFees { get; set; }
        [FirestoreProperty("fuelPenalty")] public object? FuelPenalty { get; set; }
        [FirestoreProperty("amountPaid")] public object? AmountPaid { get; set; }
        [FirestoreProperty("paymentStatus")] public object? PaymentStatus { get; set; }
        [FirestoreProperty("timestamp")] public object? Timestamp { get; set; }
        [FirestoreProperty("recordedAtUtc")] public object? RecordedAtUtc { get; set; }
        [FirestoreProperty("transactionId")] public object? TransactionId { get; set; }
    }

    private class AdjustmentProxy
    {
        [FirestoreProperty("amount")] public object? Amount { get; set; }
        [FirestoreProperty("timestamp")] public object? Timestamp { get; set; }
    }

    private class ConfigProxy
    {
        [FirestoreProperty("defaultBoundaryRate")] public object? DefaultBoundaryRate { get; set; }
    }
}

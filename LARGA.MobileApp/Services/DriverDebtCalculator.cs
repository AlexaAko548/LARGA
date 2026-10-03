using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.Services;

/// <summary>
/// The signed-in driver's debt, worked out the same way as ManagerWeb's Master Debt Ledger
/// (FinancialLedgerService.BuildCharges) so the phone and the manager see the same number:
/// every ended shift owes its boundary + late fee + fuel penalty, minus what's been paid on
/// its boundary_payments document (a shift nobody has paid anything on has no document and
/// owes it all); minus any overpayment credit (paid above a shift's total); plus the net of the
/// driver's manual debt_adjustments. A shift still Active
/// isn't owed yet.
///
/// Reads only what the Firestore rules let a driver read: their own shifts (by driverId),
/// boundary_payments by shiftId, their own debt_adjustments (by driverId), system_configs.
/// </summary>
public static class DriverDebtCalculator
{
    private const decimal FallbackBoundaryRate = 800m;

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
        var shifts = await db.GetCollection("shifts")
            .WhereEqualsTo("driverId", driverId)
            .GetDocumentsAsync<ShiftProxy>();

        foreach (var shiftDoc in shifts.Documents)
        {
            ShiftProxy? shift = shiftDoc.Data;
            if (shift == null) continue;

            // A payment can point at either of the shift's IDs (document ID, or the shiftId
            // field older builds filled with a made-up value) - same matching as the web.
            var ids = new[] { shiftDoc.Reference.Id, shift.ShiftId }
                .Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();

            PaymentProxy? payment = null;
            foreach (string id in ids)
            {
                var found = await db.GetCollection("boundary_payments")
                    .WhereEqualsTo("shiftId", id)
                    .GetDocumentsAsync<PaymentProxy>();

                // If a shift ever got two documents, the one holding the money counts.
                foreach (var doc in found.Documents)
                {
                    if (doc.Data != null && (payment == null || doc.Data.AmountPaid > payment.AmountPaid))
                    {
                        payment = doc.Data;
                    }
                }
            }

            bool isActive = shift.Status == "Active";
            if (isActive && payment == null) continue;

            // Charges from the shift itself when it has them (set at clock-out); otherwise
            // whatever the payment record carries - same as the web's ExtrasFor.
            decimal lateFee = (decimal)(shift.LateFee > 0 ? shift.LateFee : payment?.LateFees ?? 0);
            decimal fuelPenalty = (decimal)(shift.FuelPenalty > 0 ? shift.FuelPenalty : payment?.FuelPenalty ?? 0);
            decimal boundary = payment != null && payment.ExpectedBoundary > 0 ? (decimal)payment.ExpectedBoundary : defaultRate;

            DateTime start = FirestoreDateTimeFix.Apply(shift.ShiftStart);
            DateTime? paidAt = payment == null || payment.Timestamp == default ? null : FirestoreDateTimeFix.Apply(payment.Timestamp);

            result.Shifts.Add(new ShiftEntry
            {
                ShiftStartUtc = start.Year > 2000 ? start : DateTime.MinValue,
                TaxiId = shift.TaxiId ?? string.Empty,
                IsActive = isActive,
                Expected = boundary + lateFee + fuelPenalty,
                Paid = (decimal)(payment?.AmountPaid ?? 0),
                HasPayment = payment != null,
                PaymentStatus = string.IsNullOrEmpty(payment?.PaymentStatus) ? "Unpaid" : payment.PaymentStatus,
                PaymentTimestampUtc = paidAt,
            });
        }

        var adjustments = await db.GetCollection("debt_adjustments")
            .WhereEqualsTo("driverId", driverId)
            .GetDocumentsAsync<AdjustmentProxy>();
        foreach (var doc in adjustments.Documents)
        {
            if (doc.Data == null) continue;
            DateTime at = FirestoreDateTimeFix.Apply(doc.Data.Timestamp);
            result.Adjustments.Add(new AdjustmentEntry
            {
                TimestampUtc = at.Year > 2000 ? at : DateTime.MinValue,
                Amount = (decimal)doc.Data.Amount,
            });
        }

        return result;
    }

    private static async Task<decimal> GetDefaultBoundaryRateAsync(IFirebaseFirestore db)
    {
        try
        {
            var config = await db.GetCollection("system_configs").GetDocument("global")
                .GetDocumentSnapshotAsync<ConfigProxy>();
            if (config?.Data != null && config.Data.DefaultBoundaryRate > 0)
            {
                return (decimal)config.Data.DefaultBoundaryRate;
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
        [FirestoreProperty("shiftId")] public string ShiftId { get; set; }
        [FirestoreProperty("taxiId")] public string TaxiId { get; set; }
        [FirestoreProperty("status")] public string Status { get; set; }
        [FirestoreProperty("shiftStart")] public DateTime ShiftStart { get; set; }
        [FirestoreProperty("lateFee")] public double LateFee { get; set; }
        [FirestoreProperty("fuelPenalty")] public double FuelPenalty { get; set; }
    }

    private class PaymentProxy
    {
        [FirestoreProperty("shiftId")] public string ShiftId { get; set; }
        [FirestoreProperty("expectedBoundary")] public double ExpectedBoundary { get; set; }
        [FirestoreProperty("lateFees")] public double LateFees { get; set; }
        [FirestoreProperty("fuelPenalty")] public double FuelPenalty { get; set; }
        [FirestoreProperty("amountPaid")] public double AmountPaid { get; set; }
        [FirestoreProperty("paymentStatus")] public string PaymentStatus { get; set; }
        [FirestoreProperty("timestamp")] public DateTime Timestamp { get; set; }
    }

    private class AdjustmentProxy
    {
        [FirestoreProperty("amount")] public double Amount { get; set; }
        [FirestoreProperty("timestamp")] public DateTime Timestamp { get; set; }
    }

    private class ConfigProxy
    {
        [FirestoreProperty("defaultBoundaryRate")] public double DefaultBoundaryRate { get; set; }
    }
}

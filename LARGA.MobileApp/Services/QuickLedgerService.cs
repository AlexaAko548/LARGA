using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using LARGA.SharedCore;
using LARGA.SharedCore.Ledger;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
using Plugin.Firebase.Storage;

namespace LARGA.MobileApp.Services;

/// <summary>
/// The data the Quick Ledger page was built from, kept so the payment forms can plan writes against it.
/// <paramref name="Warnings"/> lists documents that could not be read, so the page can say so instead of silently
/// leaving them out.
/// </summary>
public sealed record QuickLedgerSnapshot(
    QuickLedgerInput Input,
    QuickLedgerResult Result,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, long> LedgerVersions)
{
    /// <summary>ledger_locks/{driverId}.version when this snapshot was read (0 if the driver has none yet) - what
    /// <see cref="QuickLedgerService.SavePaymentPlanAsync"/> checks so a payment planned on stale balances isn't saved.</summary>
    public long LedgerVersionFor(string driverId) => LedgerVersions.TryGetValue(driverId, out long version) ? version : 0;
}

/// <summary>Another payment for the driver was recorded after this page loaded its balances; nothing was saved.</summary>
public sealed class LedgerConflictException : InvalidOperationException
{
    public LedgerConflictException()
        : base("Another payment for this driver was recorded since this page loaded, so nothing was saved. Pull to refresh, check the balance, and try again.")
    {
    }
}

/// <summary>
/// Reads what the manager Quick Ledger needs, hands it to <see cref="QuickLedgerCalculator"/>, and saves the write
/// plans that come back.
///
/// Plugin.Firebase cannot read into Dictionary&lt;string, object&gt;. Every collection is read through a proxy with
/// [FirestoreProperty] attributes. Plain values are typed <c>object?</c>, not <c>double?</c>, because Firestore
/// returns whole numbers as integers. A <c>double?</c> property then fails to convert, Plugin returns no data for
/// the whole document, and the document disappears from the ledger without an error. Converting the raw value
/// avoids that.
/// </summary>
public class QuickLedgerService
{
    public async Task<QuickLedgerSnapshot> LoadAsync()
    {
        var warnings = new List<string>();

        // Versions first, before any balance: a payment recorded while the reads below run then shows up as a newer
        // version at save time, never as balances that silently miss it.
        IReadOnlyDictionary<string, long> ledgerVersions = await Timed(ReadLedgerVersionsAsync(warnings), "Reading payment versions");

        // Each read has a timeout. A read that never returns would otherwise keep the page loading forever.
        IReadOnlyList<ShiftRecord> shifts = await Timed(ReadShiftsAsync(warnings), "Reading shifts");
        IReadOnlyList<BoundaryPaymentRecord> payments = await Timed(ReadPaymentsAsync(warnings), "Reading boundary payments");
        IReadOnlyList<DebtAdjustmentRecord> adjustments = await Timed(ReadAdjustmentsAsync(warnings), "Reading debt adjustments");
        UserDirectory users = await Timed(ReadUsersAsync(), "Reading users");
        decimal defaultRate = await Timed(ReadDefaultBoundaryRateAsync(), "Reading boundary rate");

        IReadOnlyDictionary<string, string> taxiPlates = await Timed(ReadTaxiPlatesAsync(), "Reading taxis");

        var input = new QuickLedgerInput(shifts, payments, adjustments, users.Names, users.DriverIds, taxiPlates, defaultRate);
        QuickLedgerResult result = QuickLedgerCalculator.Build(input, PhilippineTime.Now);

        foreach (string warning in warnings)
        {
            Debug.WriteLine($"Quick Ledger: {warning}");
        }

        return new QuickLedgerSnapshot(input, result, warnings, ledgerVersions);
    }

    /// <summary>
    /// The fields of one payment's new boundary_payments document. Each payment is its own document, so an earlier
    /// payment is never changed. The evidence fields (note, GCash reference and receipt details, photo URL) are written
    /// only when present.
    /// </summary>
    private static Dictionary<object, object> PaymentFields(BoundaryPaymentWrite write, DateTime nowUtc)
    {
        string method = write.PaymentMethod == QuickLedgerCalculator.EWalletMethod ? "E-Wallet" : "Cash";

        // Dictionary + Merge creates the document. The tuple-array overload stored no amount on the document (caught by
        // the read-back check), so it is not used.
        var fields = new Dictionary<object, object>
        {
            ["shiftId"] = write.ShiftId,
            // firestore.rules let a driver read only payments carrying their own driverId.
            ["driverId"] = write.DriverId,
            ["expectedBoundary"] = (double)write.ExpectedBoundary,
            ["lateFees"] = (double)write.LateFees,
            ["fuelPenalty"] = (double)write.FuelPenalty,
            ["amountPaid"] = (double)write.AmountPaid,
            ["paymentMethod"] = method,
            ["paymentStatus"] = write.PaymentStatus,
            ["timestamp"] = nowUtc,
            [RecordedAtField] = RecordedAtText(nowUtc),
            ["transactionId"] = write.TransactionId,
            ["recordedVia"] = RecordedVia,
        };
        AddEvidence(fields, write.Evidence);
        return fields;
    }

    /// <summary>
    /// Saves a payment plan (Record Payment or Record Other Payment): each allocation as its own payment document, then
    /// the automatic credit against manual debt - the same records the web's BookPaymentAsync writes.
    ///
    /// Everything is written in one transaction, so a failure part-way can't leave a half-booked payment. It only
    /// commits while ledger_locks/{driverId}.version is still <paramref name="expectedLedgerVersion"/> - the version the
    /// plan's balances were read at (<see cref="QuickLedgerSnapshot.LedgerVersionFor"/>). If another payment for this
    /// driver was recorded since (on the web or another phone - they bump the same version), nothing is saved and
    /// <see cref="LedgerConflictException"/> is thrown, so the same debt can't be paid twice.
    /// </summary>
    public async Task SavePaymentPlanAsync(PaymentPlan plan, string driverId, DateTime nowUtc, long expectedLedgerVersion)
    {
        IFirebaseFirestore firestore = CrossFirebaseFirestore.Current;

        var payments = plan.PaymentUpdates
            .Select(write => (Write: write,
                              Doc: firestore.GetCollection("boundary_payments").GetDocument(write.DocumentId),
                              Fields: PaymentFields(write, nowUtc)))
            .ToList();

        Dictionary<object, object>? credit = null;
        if (plan.AdjustmentCredit > 0)
        {
            credit = new Dictionary<object, object>
            {
                ["driverId"] = driverId,
                ["amount"] = (double)-plan.AdjustmentCredit,
                ["reason"] = "Automatic credit from a debt payment.",
                ["timestamp"] = nowUtc,
                // Ties it to the payment's documents, so the web's history shows it as part of that payment.
                ["transactionId"] = plan.TransactionId,
            };
            if (!string.IsNullOrEmpty(plan.Evidence?.Notes))
            {
                credit["notes"] = plan.Evidence!.Notes!;
            }
        }

        IDocumentReference creditDoc = firestore.GetCollection("debt_adjustments").CreateDocument();
        IDocumentReference lockDoc = firestore.GetCollection("ledger_locks").GetDocument(driverId);

        // The transaction body runs on a native thread and may be retried by Firestore, so it returns a flag rather than
        // throwing: a .NET exception thrown in there doesn't reliably come back out as itself.
        bool committed = await Timed(firestore.RunTransactionAsync(transaction =>
        {
            long current = LedgerVersionOf(transaction.GetDocument<LedgerLockProxy>(lockDoc)?.Data);
            if (current != expectedLedgerVersion)
            {
                return false; // nothing written - the whole transaction is a no-op
            }

            // Dictionary + Merge creates the document. The tuple-array overload stored no amount on the document
            // (caught by the read-back check), so it is not used.
            foreach (var payment in payments)
            {
                transaction.SetData(payment.Doc, payment.Fields, SetOptions.Merge());
            }

            if (credit is not null)
            {
                transaction.SetData(creditDoc, credit, SetOptions.Merge());
            }

            transaction.SetData(lockDoc, new Dictionary<object, object>
            {
                ["version"] = current + 1,
                ["updatedAt"] = FieldValue.ServerTimestamp(),
            }, SetOptions.Merge());
            return true;
        }), "Saving the payment");

        if (!committed)
        {
            throw new LedgerConflictException();
        }

        foreach (var payment in payments)
        {
            await VerifySavedAsync(payment.Doc, payment.Write);
        }
    }

    private static long LedgerVersionOf(LedgerLockProxy? data) =>
        data?.Version is null ? 0 : Convert.ToInt64(data.Version, CultureInfo.InvariantCulture);

    /// <summary>Every driver's ledger_locks version, read before the balances so a plan built from them can be checked
    /// for staleness at save time. A failed read leaves the map empty (versions read as 0): a save then fails safe with a
    /// conflict for any driver whose real version isn't 0, rather than paying on stale balances.</summary>
    private static async Task<IReadOnlyDictionary<string, long>> ReadLedgerVersionsAsync(List<string> warnings)
    {
        try
        {
            var snapshot = await CrossFirebaseFirestore.Current
                .GetCollection("ledger_locks")
                .GetDocumentsAsync<LedgerLockProxy>();

            return snapshot.Documents
                .Where(doc => doc.Data is not null)
                .ToDictionary(doc => doc.Reference.Id, doc => LedgerVersionOf(doc.Data));
        }
        catch (Exception ex)
        {
            warnings.Add($"payment versions could not be read ({ex.Message})");
            return new Dictionary<string, long>();
        }
    }

    /// <summary>
    /// Writes an entry to audit_logs, the same collection the web's inventory and fleet actions use. The actor is the
    /// signed-in manager. The timestamp is set by the server so it's a real Timestamp for the web's date queries.
    /// </summary>
    public async Task WriteAuditAsync(string actionType, string details)
    {
        string userId = CrossFirebaseAuth.Current.CurrentUser?.Uid ?? string.Empty;

        var entry = new Dictionary<object, object>
        {
            ["userId"] = userId,
            ["actionType"] = actionType,
            ["auditLogDetails"] = details,
            ["timestamp"] = FieldValue.ServerTimestamp(),
        };

        await Timed(CrossFirebaseFirestore.Current
            .GetCollection("audit_logs")
            .AddDocumentAsync(entry), "Writing the audit entry");
    }

    /// <summary>
    /// Uploads an E-Wallet receipt photo to Firebase Storage and returns its download URL. The upload is an audit record,
    /// so a failure throws instead of returning null, and the payment is not saved without it.
    /// </summary>
    public async Task<string> UploadReceiptPhotoAsync(string localPath, DateTime nowUtc, string label)
    {
        string storagePath = $"receipts/ewallet/{nowUtc:yyyyMMddHHmmssfff}_{label}.jpg";
        IStorageReference reference = CrossFirebaseStorage.Current.GetRootReference().GetChild(storagePath);

        await Timed(reference.PutFile(localPath, new StorageMetadata(contentType: "image/jpeg")).AwaitAsync(), "Uploading the receipt photo");
        return await Timed(reference.GetDownloadUrlAsync(), "Getting the receipt photo link");
    }

    private static void AddEvidence(Dictionary<object, object> fields, PaymentEvidence? evidence)
    {
        if (evidence is null) return;

        if (!string.IsNullOrEmpty(evidence.Notes)) fields["notes"] = evidence.Notes!;
        if (!string.IsNullOrEmpty(evidence.ReferenceNumber)) fields["gcashReferenceNumber"] = evidence.ReferenceNumber!;
        if (evidence.ReceiptAmount.HasValue) fields["receiptAmount"] = (double)evidence.ReceiptAmount.Value;
        if (evidence.ReceiptDate.HasValue) fields["receiptDate"] = evidence.ReceiptDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(evidence.ReceiptPhotoUrl)) fields["ePayReceiptPhoto"] = evidence.ReceiptPhotoUrl!;
        if (!string.IsNullOrEmpty(evidence.TargetAccount)) fields["receiptTargetAccount"] = evidence.TargetAccount!;
        if (!string.IsNullOrEmpty(evidence.TargetAccountStatus)) fields["targetAccountStatus"] = evidence.TargetAccountStatus!;
    }

    /// <summary>
    /// Reads the document back and checks that the amount is the one just written. The save call can finish without
    /// an error even when the write did not land (for example, if Firestore security rules refuse it), so a success
    /// is only reported after this check passes.
    /// </summary>
    private static async Task VerifySavedAsync(IDocumentReference document, BoundaryPaymentWrite write)
    {
        var readBack = await Timed(document.GetDocumentSnapshotAsync<BoundaryPaymentProxy>(), "Checking the saved payment");

        if (readBack?.Data is null)
        {
            throw new InvalidOperationException(
                $"The payment was not found in boundary_payments/{write.DocumentId} after saving. " +
                "Check the Firestore security rules allow this app to write boundary_payments.");
        }

        decimal stored = ToDecimal(readBack.Data.AmountPaid);
        if (Math.Abs(stored - write.AmountPaid) > 0.005m)
        {
            throw new InvalidOperationException(
                $"boundary_payments/{write.DocumentId} reads back as {stored:N2}, not the {write.AmountPaid:N2} that was saved.");
        }
    }

    /// <summary>Shifts as ledger records - every shift, or only <paramref name="driverId"/>'s (the driver app:
    /// firestore.rules let a driver read only their own).</summary>
    internal static async Task<IReadOnlyList<ShiftRecord>> ReadShiftsAsync(List<string> warnings, string? driverId = null)
    {
        var collection = CrossFirebaseFirestore.Current.GetCollection("shifts");
        var snapshot = driverId is null
            ? await collection.GetDocumentsAsync<ShiftProxy>()
            : await collection.WhereEqualsTo("driverId", driverId).GetDocumentsAsync<ShiftProxy>();

        var records = new List<ShiftRecord>();
        foreach (var doc in snapshot.Documents)
        {
            if (doc.Data is null)
            {
                warnings.Add($"shift {doc.Reference.Id} could not be read");
                continue;
            }

            records.Add(new ShiftRecord(
                DocumentId: doc.Reference.Id,
                ShiftId: StringOf(doc.Data.ShiftId),
                DriverId: StringOf(doc.Data.DriverId),
                TaxiId: StringOf(doc.Data.TaxiId),
                StartUtc: ToUtc(doc.Data.ShiftStart),
                Status: StringOf(doc.Data.Status),
                LateFee: doc.Data.LateFee is null ? null : ToDecimal(doc.Data.LateFee),
                FuelPenalty: doc.Data.FuelPenalty is null ? null : ToDecimal(doc.Data.FuelPenalty),
                EndUtc: ToUtc(doc.Data.ShiftEnd)));
        }

        return records;
    }

    /// <summary>Payment documents as ledger records - all, or only those carrying <paramref name="driverId"/> (the only
    /// ones firestore.rules let a driver read).</summary>
    internal static async Task<IReadOnlyList<BoundaryPaymentRecord>> ReadPaymentsAsync(List<string> warnings, string? driverId = null)
    {
        var collection = CrossFirebaseFirestore.Current.GetCollection("boundary_payments");
        var snapshot = driverId is null
            ? await collection.GetDocumentsAsync<BoundaryPaymentProxy>()
            : await collection.WhereEqualsTo("driverId", driverId).GetDocumentsAsync<BoundaryPaymentProxy>();

        var records = new List<BoundaryPaymentRecord>();
        foreach (var doc in snapshot.Documents)
        {
            if (doc.Data is null)
            {
                warnings.Add($"payment {doc.Reference.Id} could not be read");
                continue;
            }

            records.Add(new BoundaryPaymentRecord(
                DocumentId: doc.Reference.Id,
                ShiftId: StringOf(doc.Data.ShiftId),
                ExpectedBoundary: ToDecimal(doc.Data.ExpectedBoundary),
                LateFees: ToDecimal(doc.Data.LateFees),
                FuelPenalty: ToDecimal(doc.Data.FuelPenalty),
                AmountPaid: ToDecimal(doc.Data.AmountPaid),
                PaymentStatus: StringOf(doc.Data.PaymentStatus),
                PaymentMethod: StringOf(doc.Data.PaymentMethod),
                TimestampUtc: ParseRecordedAt(StringOf(doc.Data.RecordedAtUtc)) ?? ToUtc(doc.Data.Timestamp),
                // Kept empty on older records (no transactionId): BoundaryPaymentRules.TotalPaid tells old
                // running-total documents apart by it, and Done Today groups those by document ID instead.
                TransactionId: StringOf(doc.Data.TransactionId)));
        }

        return records;
    }

    /// <summary>Manual debt adjustments as ledger records - all, or only <paramref name="driverId"/>'s.</summary>
    internal static async Task<IReadOnlyList<DebtAdjustmentRecord>> ReadAdjustmentsAsync(List<string> warnings, string? driverId = null)
    {
        var collection = CrossFirebaseFirestore.Current.GetCollection("debt_adjustments");
        var snapshot = driverId is null
            ? await collection.GetDocumentsAsync<DebtAdjustmentProxy>()
            : await collection.WhereEqualsTo("driverId", driverId).GetDocumentsAsync<DebtAdjustmentProxy>();

        var records = new List<DebtAdjustmentRecord>();
        foreach (var doc in snapshot.Documents)
        {
            if (doc.Data is null)
            {
                warnings.Add($"debt adjustment {doc.Reference.Id} could not be read");
                continue;
            }

            records.Add(new DebtAdjustmentRecord(StringOf(doc.Data.DriverId), ToDecimal(doc.Data.Amount), ToUtc(doc.Data.Timestamp)));
        }

        return records;
    }

    /// <summary>
    /// Every user in the users collection. The Other Payment list is every user with role "Driver" (the same filter the
    /// web's driver roster uses), so a driver with no debt can still be picked.
    /// </summary>
    private static async Task<UserDirectory> ReadUsersAsync()
    {
        var snapshot = await CrossFirebaseFirestore.Current
            .GetCollection("users")
            .GetDocumentsAsync<UserProxy>();

        var names = new Dictionary<string, string>();
        var driverIds = new List<string>();
        foreach (var doc in snapshot.Documents)
        {
            if (doc.Data is null) continue;

            string userId = doc.Reference.Id;
            names[userId] = StringOf(doc.Data.FullName);
            if (string.Equals(StringOf(doc.Data.Role), DriverRole, StringComparison.OrdinalIgnoreCase)) driverIds.Add(userId);
        }

        return new UserDirectory(names, driverIds);
    }

    private const string DriverRole = "Driver";

    private sealed record UserDirectory(IReadOnlyDictionary<string, string> Names, IReadOnlyList<string> DriverIds);

    /// <summary>
    /// Plate numbers keyed by taxi ID. Each taxi is keyed by its document ID and by its taxiId field, since shifts store
    /// the taxi ID either way.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> ReadTaxiPlatesAsync()
    {
        var snapshot = await CrossFirebaseFirestore.Current
            .GetCollection("taxis")
            .GetDocumentsAsync<TaxiProxy>();

        var plates = new Dictionary<string, string>();
        foreach (var doc in snapshot.Documents)
        {
            if (doc.Data is null) continue;

            string plate = StringOf(doc.Data.PlateNumber);
            if (string.IsNullOrWhiteSpace(plate)) continue;

            plates[doc.Reference.Id] = plate;
            string taxiId = StringOf(doc.Data.TaxiId);
            if (!string.IsNullOrEmpty(taxiId)) plates[taxiId] = plate;
        }

        return plates;
    }

    // Falls back to the same default the web ledger uses when the config is missing or unreadable.
    internal static async Task<decimal> ReadDefaultBoundaryRateAsync()
    {
        try
        {
            var snapshot = await CrossFirebaseFirestore.Current
                .GetCollection("system_configs")
                .GetDocument("global")
                .GetDocumentSnapshotAsync<SystemConfigProxy>();

            decimal rate = ToDecimal(snapshot?.Data?.DefaultBoundaryRate);
            return rate > 0 ? rate : QuickLedgerCalculator.FallbackBoundaryRate;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Quick Ledger: could not read system_configs/global, using fallback rate: {ex.Message}");
            return QuickLedgerCalculator.FallbackBoundaryRate;
        }
    }

    /// <summary>
    /// system_configs/global.authorizedPayoutAccounts - the GCash numbers / bank accounts boundary payments may be sent
    /// to. Null when it can't be read (offline), so the scan says "couldn't check" instead of flagging every receipt.
    /// </summary>
    internal static async Task<IReadOnlyList<string>?> ReadAuthorizedPayoutAccountsAsync()
    {
        try
        {
            var snapshot = await CrossFirebaseFirestore.Current
                .GetCollection("system_configs")
                .GetDocument("global")
                .GetDocumentSnapshotAsync<SystemConfigProxy>();

            return (snapshot?.Data?.AuthorizedPayoutAccounts ?? new List<object>())
                .Select(StringOf)
                .Where(a => a.Length > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Quick Ledger: could not read authorized payout accounts: {ex.Message}");
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Value conversion. Firestore can return a number as an integer, a double, or (if it was written by hand) a string.
    // ---------------------------------------------------------------------

    private static decimal ToDecimal(object? value) => value switch
    {
        null => 0m,
        decimal d => d,
        double d => Convert.ToDecimal(d),
        float f => Convert.ToDecimal(f),
        long l => l,
        int i => i,
        string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed) => parsed,
        _ => 0m,
    };

    private static string StringOf(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>
    /// Converts a Firestore timestamp to UTC. Plugin.Firebase can return a DateTimeOffset, a DateTime (which has the
    /// Android 1601 problem, see FirestoreDateTimeFix), or nothing.
    /// </summary>
    private static DateTime? ToUtc(object? value) => value switch
    {
        DateTimeOffset offset => offset.UtcDateTime,
        DateTime dt => ToUtcKind(FirestoreDateTimeFix.Apply(dt)),
        string s => ParseRecordedAt(s),
        _ => null,
    };

    private static DateTime ToUtcKind(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>
    /// When a payment was recorded, as an ISO-8601 UTC string. The app writes this in addition to the Timestamp field,
    /// because a Plugin.Firebase DateTime write was not reliable on Android, and Done Today depends on the value.
    /// The web ledger ignores this field.
    /// </summary>
    private const string RecordedAtField = "recordedAtUtc";

    // boundary_payments.recordedVia: which screen took the payment, shown in the web's Financial History.
    private const string RecordedVia = "Quick Ledger";

    private static string RecordedAtText(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);

    private static DateTime? ParseRecordedAt(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime value)
            ? value
            : null;

    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Fails with a readable message if a Firestore call doesn't finish in time (for example, when offline).</summary>
    private static async Task<T> Timed<T>(Task<T> work, string what)
    {
        Task finished = await Task.WhenAny(work, Task.Delay(OperationTimeout));
        if (finished != work)
        {
            throw new TimeoutException($"{what} timed out after {OperationTimeout.TotalSeconds:0} seconds. Check the connection and try again.");
        }

        return await work;
    }

    private static async Task Timed(Task work, string what)
    {
        Task finished = await Task.WhenAny(work, Task.Delay(OperationTimeout));
        if (finished != work)
        {
            throw new TimeoutException($"{what} timed out after {OperationTimeout.TotalSeconds:0} seconds. Check the connection and try again.");
        }

        await work;
    }

    // ---------------------------------------------------------------------
    // Firestore proxies: field names match the camelCase names the web writes.
    // Plain values are object? so that any stored number type converts (see the class comment).
    // ---------------------------------------------------------------------

    public class LedgerLockProxy
    {
        // object, not long: see the class summary on whole numbers.
        [Plugin.Firebase.Firestore.FirestoreProperty("version")]
        public object? Version { get; set; }
    }

    public class ShiftProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("shiftId")]
        public object? ShiftId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("driverId")]
        public object? DriverId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("taxiId")]
        public object? TaxiId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("shiftStart")]
        public object? ShiftStart { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("shiftEnd")]
        public object? ShiftEnd { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("status")]
        public object? Status { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("lateFee")]
        public object? LateFee { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("fuelPenalty")]
        public object? FuelPenalty { get; set; }
    }

    public class BoundaryPaymentProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("shiftId")]
        public object? ShiftId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("expectedBoundary")]
        public object? ExpectedBoundary { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("lateFees")]
        public object? LateFees { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("fuelPenalty")]
        public object? FuelPenalty { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("amountPaid")]
        public object? AmountPaid { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("paymentStatus")]
        public object? PaymentStatus { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("paymentMethod")]
        public object? PaymentMethod { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("timestamp")]
        public object? Timestamp { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty(RecordedAtField)]
        public object? RecordedAtUtc { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("transactionId")]
        public object? TransactionId { get; set; }
    }

    public class DebtAdjustmentProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("driverId")]
        public object? DriverId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("amount")]
        public object? Amount { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("timestamp")]
        public object? Timestamp { get; set; }
    }

    public class UserProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("fullName")]
        public object? FullName { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("role")]
        public object? Role { get; set; }
    }

    public class TaxiProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("taxiId")]
        public object? TaxiId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("plateNumber")]
        public object? PlateNumber { get; set; }
    }

    public class SystemConfigProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("defaultBoundaryRate")]
        public object? DefaultBoundaryRate { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("authorizedPayoutAccounts")]
        public List<object>? AuthorizedPayoutAccounts { get; set; }
    }
}

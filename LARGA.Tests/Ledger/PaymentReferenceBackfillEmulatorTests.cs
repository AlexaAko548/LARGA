using Google.Cloud.Firestore;
using LARGA.SharedCore;
using LARGA.SharedCore.Services;
using LARGA.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace LARGA.Tests.Ledger;

/// <summary>BackfillPaymentReferenceClaimsAsync against the local Firestore emulator (BRP-008, consistency
/// audit row #62). Never touches the live project.</summary>
[Collection("emulator")]
public class PaymentReferenceBackfillEmulatorTests
{
    private static FinancialLedgerService Ledger(FirestoreDb db)
    {
        var lazy = new Lazy<FirestoreDb>(db);
        return new FinancialLedgerService(lazy,
            new Lazy<PhotoStorageTarget>(() => throw new InvalidOperationException("no storage in tests")),
            NullLogger<FinancialLedgerService>.Instance, new CollectionCache(lazy, NullLogger<CollectionCache>.Instance));
    }

    private static Task Payment(FirestoreDb db, string id, DateTime recordedUtc, string? gcash, string? receipt)
    {
        var d = new Dictionary<string, object>
        {
            ["shiftId"] = "s1", ["driverId"] = "d1", ["transactionId"] = "TXN_" + id, ["expectedBoundary"] = 800.0,
            ["lateFees"] = 0.0, ["fuelPenalty"] = 0.0, ["amountPaid"] = 800.0, ["paymentMethod"] = "Cash",
            ["paymentStatus"] = "Paid", ["timestamp"] = Emu.Utc(recordedUtc),
            ["recordedAtUtc"] = BoundaryPaymentRules.RecordedAtText(recordedUtc),
        };
        if (gcash is not null) d["gcashReferenceNumber"] = gcash;
        if (receipt is not null) d["receiptReferenceNo"] = receipt;
        return db.Collection("boundary_payments").Document(id).SetAsync(d);
    }

    private static async Task<Dictionary<string, DocumentSnapshot>> Claims(FirestoreDb db) =>
        (await db.Collection(PaymentReferenceRules.Collection).GetSnapshotAsync()).Documents.ToDictionary(d => d.Id);

    [EmulatorFact]
    public async Task Backfill_ClaimsReferences_OldestKeeps_EmptyGcashFallsBackToReceipt()
    {
        await Emu.ClearAsync();
        FirestoreDb db = Emu.Db();
        DateTime now = DateTime.UtcNow;
        await Payment(db, "P1", now.AddHours(-3), gcash: "", receipt: "1234 567 890 123");   // empty gcash -> receipt reference
        await Payment(db, "P2", now.AddHours(-2), gcash: "9999999999999", receipt: null);
        await Payment(db, "P3", now.AddHours(-1), gcash: "1234567890123", receipt: null);    // same reference as P1, newer
        await Payment(db, "P4", now.AddMinutes(-30), gcash: null, receipt: null);            // cash, no reference

        int claimed = await Ledger(db).BackfillPaymentReferenceClaimsAsync();

        Dictionary<string, DocumentSnapshot> claims = await Claims(db);
        Assert.Equal(2, claimed);
        Assert.Equal("P1", claims["1234567890123"].GetValue<string>("paymentId"));
        Assert.Equal("1234 567 890 123", claims["1234567890123"].GetValue<string>("referenceNumber"));
        Assert.Equal("P2", claims["9999999999999"].GetValue<string>("paymentId"));
        Assert.True(claims["9999999999999"].GetValue<bool>("backfilled"));
        Assert.Contains(FinancialLedgerService.ReferenceBackfillMarkerId, claims.Keys);
        Assert.Equal(3, claims.Count); // 2 references + the marker
    }

    [EmulatorFact]
    public async Task Backfill_RunsOnlyOnce_AndKeepsAnExistingClaim()
    {
        await Emu.ClearAsync();
        FirestoreDb db = Emu.Db();
        await Payment(db, "P1", DateTime.UtcNow.AddHours(-2), gcash: "1234567890123", receipt: null);
        await db.Collection(PaymentReferenceRules.Collection).Document("1234567890123")
            .SetAsync(new Dictionary<string, object> { ["paymentId"] = "P_LIVE", ["driverId"] = "d1", ["transactionId"] = "T", ["referenceNumber"] = "1234567890123", ["createdAt"] = DateTime.UtcNow });

        Assert.Equal(0, await Ledger(db).BackfillPaymentReferenceClaimsAsync()); // already claimed - kept
        Assert.Equal("P_LIVE", (await Claims(db))["1234567890123"].GetValue<string>("paymentId"));

        await Payment(db, "P2", DateTime.UtcNow.AddHours(-1), gcash: "5555555555555", receipt: null);
        Assert.Equal(0, await Ledger(db).BackfillPaymentReferenceClaimsAsync()); // the marker stops a second run
        Assert.DoesNotContain("5555555555555", (await Claims(db)).Keys);
    }
}

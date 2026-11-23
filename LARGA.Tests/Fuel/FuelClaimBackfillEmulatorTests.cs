using Google.Cloud.Firestore;
using LARGA.SharedCore;
using LARGA.SharedCore.Services;
using LARGA.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace LARGA.Tests.Fuel;

/// <summary>BackfillOrNumberClaimsAsync against the local Firestore emulator (consistency audit row #58):
/// claims under the station-brand + OR number key, clears older-format claims, and only one run at a time.
/// Never touches the live project.</summary>
[Collection("emulator")]
public class FuelClaimBackfillEmulatorTests
{
    private static FuelVerificationService Service(FirestoreDb db) =>
        new(new Lazy<FirestoreDb>(db), NullLogger<FuelVerificationService>.Instance);

    private static async Task<FirestoreDb> FreshAsync()
    {
        await Emu.ClearAsync();
        return Emu.Db();
    }

    private static Task Log(FirestoreDb db, string id, string station, string orNumber, DateTime submittedUtc) =>
        db.Collection("fuel_logs").Document(id).SetAsync(new Dictionary<string, object>
        {
            ["shiftId"] = "s1", ["driverId"] = "d1", ["fuelStation"] = station, ["orNumber"] = orNumber,
            ["litersRefueled"] = 20.0, ["fuelCost"] = 1200.0, ["verificationStatus"] = "Pending",
            ["submittedAt"] = Emu.Utc(submittedUtc),
        });

    private static Task Claim(FirestoreDb db, string key, string fuelLogId) =>
        db.Collection(FuelOrNumberRules.Collection).Document(key).SetAsync(new Dictionary<string, object>
        {
            ["fuelLogId"] = fuelLogId, ["createdAt"] = DateTime.UtcNow,
        });

    private static async Task<Dictionary<string, string?>> Claims(FirestoreDb db) =>
        (await db.Collection(FuelOrNumberRules.Collection).GetSnapshotAsync()).Documents
        .ToDictionary(d => d.Id, d => d.ContainsField("fuelLogId") ? d.GetValue<string>("fuelLogId") : null);

    [EmulatorFact]
    public async Task Backfill_ClaimsByBrand_OldestKeeps_ClearsOlderFormats_KeepsNewPhoneClaims()
    {
        FirestoreDb db = await FreshAsync();
        DateTime now = DateTime.UtcNow;
        await Log(db, "L1", "Petron", "0012345", now.AddHours(-3));
        await Log(db, "L2", "Petron - EDSA", "0012 345", now.AddHours(-2)); // same brand + number as L1
        await Log(db, "L3", "Shell Pilipinas", "0012345", now.AddHours(-1));
        await Log(db, "L4", "Petron", "", now.AddMinutes(-30));                // no OR number - nothing to claim

        await Claim(db, "0012345", "L1");              // v1: the OR number alone
        await Claim(db, "PETRONEDSA~0012345", "L2");   // v2: raw station text
        await Claim(db, "CALTEX~999", "L_FROM_PHONE");  // made by a phone while the backfill ran - its log isn't in the read

        int claimed = await Service(db).BackfillOrNumberClaimsAsync();

        Dictionary<string, string?> claims = await Claims(db);
        Assert.Equal(2, claimed);
        Assert.Equal("L1", claims["PETRON~0012345"]);  // the oldest keeps the number
        Assert.Equal("L3", claims["SHELL~0012345"]);
        Assert.Equal("L_FROM_PHONE", claims["CALTEX~999"]);
        Assert.DoesNotContain("0012345", claims.Keys);
        Assert.DoesNotContain("PETRONEDSA~0012345", claims.Keys);
        Assert.Contains(FuelVerificationService.OrClaimBackfillMarkerId, claims.Keys);
        Assert.DoesNotContain(FuelVerificationService.OrClaimBackfillLockId, claims.Keys); // lock released
        Assert.Equal(4, claims.Count); // 2 backfilled claims + the phone claim + the marker
    }

    [EmulatorFact]
    public async Task Backfill_RunsOnlyOnce()
    {
        FirestoreDb db = await FreshAsync();
        await Log(db, "L1", "Petron", "0012345", DateTime.UtcNow.AddHours(-1));

        Assert.Equal(1, await Service(db).BackfillOrNumberClaimsAsync());
        await db.Collection(FuelOrNumberRules.Collection).Document("PETRON~0012345").DeleteAsync();

        Assert.Equal(0, await Service(db).BackfillOrNumberClaimsAsync());
        Assert.DoesNotContain("PETRON~0012345", (await Claims(db)).Keys); // the marker stops a second run
    }

    [EmulatorFact]
    public async Task Backfill_ExistingCurrentKey_IsKept()
    {
        FirestoreDb db = await FreshAsync();
        await Log(db, "L1", "Petron", "0012345", DateTime.UtcNow.AddHours(-2));
        await Log(db, "L2", "Petron", "0012345", DateTime.UtcNow.AddHours(-1));
        await Claim(db, "PETRON~0012345", "L2"); // a phone on the new app claimed it for the newer log

        await Service(db).BackfillOrNumberClaimsAsync();

        Assert.Equal("L2", (await Claims(db))["PETRON~0012345"]);
    }

    [EmulatorFact]
    public async Task Backfill_AnotherRunInProgress_DoesNothing()
    {
        FirestoreDb db = await FreshAsync();
        await Log(db, "L1", "Petron", "0012345", DateTime.UtcNow.AddHours(-1));
        await Claim(db, "0012345", "L1");
        await db.Collection(FuelOrNumberRules.Collection).Document(FuelVerificationService.OrClaimBackfillLockId)
            .SetAsync(new Dictionary<string, object> { ["startedAt"] = DateTime.UtcNow.AddMinutes(-1) });

        Assert.Equal(0, await Service(db).BackfillOrNumberClaimsAsync());

        Dictionary<string, string?> claims = await Claims(db);
        Assert.Contains("0012345", claims.Keys);                                              // untouched
        Assert.DoesNotContain(FuelVerificationService.OrClaimBackfillMarkerId, claims.Keys);   // not marked done
        Assert.Contains(FuelVerificationService.OrClaimBackfillLockId, claims.Keys);           // the other run's lock stays
    }

    [EmulatorFact]
    public async Task Backfill_StaleLock_IsTakenOver()
    {
        FirestoreDb db = await FreshAsync();
        await Log(db, "L1", "Petron", "0012345", DateTime.UtcNow.AddHours(-1));
        await db.Collection(FuelOrNumberRules.Collection).Document(FuelVerificationService.OrClaimBackfillLockId)
            .SetAsync(new Dictionary<string, object>
            {
                ["startedAt"] = DateTime.UtcNow.AddMinutes(-(FuelVerificationService.BackfillLockMinutes + 5)),
            });

        Assert.Equal(1, await Service(db).BackfillOrNumberClaimsAsync());

        Dictionary<string, string?> claims = await Claims(db);
        Assert.Contains("PETRON~0012345", claims.Keys);
        Assert.DoesNotContain(FuelVerificationService.OrClaimBackfillLockId, claims.Keys);
    }
}

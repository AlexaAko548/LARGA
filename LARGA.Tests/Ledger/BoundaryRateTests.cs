using Google.Cloud.Firestore;
using LARGA.SharedCore;
using LARGA.SharedCore.Ledger;
using LARGA.SharedCore.Services;
using LARGA.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace LARGA.Tests.Ledger;

/// <summary>Changing the boundary rate in Manager Settings: a shift keeps the rate in effect when it started.</summary>
public class BoundaryRateScheduleTests
{
    private static readonly DateTime First = new(2026, 11, 10, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Second = new(2026, 11, 20, 4, 0, 0, DateTimeKind.Utc);

    // 800 until the 10th, 900 until the 20th, 1000 since. Entries deliberately out of order.
    private static BoundaryRateSchedule Schedule() => BoundaryRateSchedule.From(1000m, new[]
    {
        BoundaryRateSchedule.HistoryEntry(Second, 900m),
        BoundaryRateSchedule.HistoryEntry(First, 800m),
    });

    [Fact]
    public void ShiftKeepsTheRateInEffectWhenItStarted()
    {
        BoundaryRateSchedule rates = Schedule();

        Assert.Equal(800m, rates.RateFor(First.AddDays(-30)));
        Assert.Equal(800m, rates.RateFor(First.AddSeconds(-1)));
        Assert.Equal(900m, rates.RateFor(First));
        Assert.Equal(900m, rates.RateFor(Second.AddSeconds(-1)));
        Assert.Equal(1000m, rates.RateFor(Second));
        Assert.Equal(1000m, rates.RateFor(null));
    }

    [Fact]
    public void NoHistory_UnreadableEntries_AndNoConfiguredRate()
    {
        var junk = new[] { null, "", "800", "yesterday|800", "2026-11-10T04:00:00Z|abc", "2026-11-10T04:00:00Z|0" };

        BoundaryRateSchedule rates = BoundaryRateSchedule.From(0m, junk);

        Assert.Equal(ShiftRules.DefaultBoundaryRate, rates.RateFor(First.AddDays(-1)));
        Assert.Equal(950m, BoundaryRateSchedule.From(950m, null).RateFor(First));
    }

    [Fact]
    public void PhoneLedger_ChargesEachShiftItsOwnRate_AndAPaymentRecordStillWins()
    {
        ShiftRecord Shift(string id, DateTime start) =>
            new(id, id, "d1", "TAXI_T", start, "Completed", 0m, 0m, start.AddHours(8));
        var input = new QuickLedgerInput(
            new[] { Shift("old", First.AddDays(-1)), Shift("new", Second.AddDays(1)), Shift("paid", First.AddDays(-2)) },
            new[] { new BoundaryPaymentRecord("p1", "paid", 750m, 0, 0, 100m, "Partial", "Cash", First, "T1") },
            Array.Empty<DebtAdjustmentRecord>(),
            new Dictionary<string, string>(), new[] { "d1" }, new Dictionary<string, string>(), Schedule());

        DriverDebtResult debt = QuickLedgerCalculator.DriverDebt(input, "d1");

        Assert.Equal(800m, debt.Shifts.Single(s => s.Shift.DocumentId == "old").Expected);
        Assert.Equal(1000m, debt.Shifts.Single(s => s.Shift.DocumentId == "new").Expected);
        Assert.Equal(750m, debt.Shifts.Single(s => s.Shift.DocumentId == "paid").Expected);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0.0, false)]
    [InlineData(-5.0, false)]
    [InlineData(900.5, false)]
    [InlineData(10001.0, false)]
    [InlineData(1.0, true)]
    [InlineData(900.0, true)]
    [InlineData(10000.0, true)]
    public void Validate_WholePesosWithinLimits(double? rate, bool valid) =>
        Assert.Equal(valid, BoundaryRateService.Validate((decimal?)rate) is null);
}

[Collection("emulator")]
public class BoundaryRateEmulatorTests
{
    private static async Task<FirestoreDb> FreshAsync()
    {
        await Emu.ClearAsync();
        return Emu.Db();
    }

    private static BoundaryRateService Rates(FirestoreDb db) => new(new Lazy<FirestoreDb>(db));

    private static FinancialLedgerService Ledger(FirestoreDb db)
    {
        var lazy = new Lazy<FirestoreDb>(db);
        return new FinancialLedgerService(lazy,
            new Lazy<PhotoStorageTarget>(() => throw new InvalidOperationException("no storage in tests")),
            NullLogger<FinancialLedgerService>.Instance, new CollectionCache(lazy, NullLogger<CollectionCache>.Instance));
    }

    private static Task Shift(FirestoreDb db, string id, DateTime startUtc) =>
        db.Collection("shifts").Document(id).SetAsync(new Dictionary<string, object>
        {
            ["shiftId"] = id, ["driverId"] = "d1", ["taxiId"] = "TAXI_T", ["shiftStart"] = startUtc,
            ["shiftEnd"] = startUtc.AddMinutes(1), ["status"] = "Completed", ["lateFee"] = 0.0, ["startMileage"] = 1000,
        });

    private static async Task<decimal> DebtAsync(FirestoreDb db) =>
        (await Ledger(db).GetDebtLedgerAsync()).Rows.Single(r => r.DriverId == "d1").TotalDebt;

    [EmulatorFact]
    public async Task ChangingTheRate_LeavesEarlierShiftsAlone_AndChargesLaterOnesTheNewRate()
    {
        FirestoreDb db = await FreshAsync();
        await db.Collection("users").Document("d1").SetAsync(new Dictionary<string, object> { ["fullName"] = "d1", ["role"] = "Driver" });
        DocumentReference config = db.Collection("system_configs").Document("global");
        await config.SetAsync(new Dictionary<string, object> { ["duressPin"] = "1234" });
        await Shift(db, "before", ShiftClock.UtcNow.AddDays(-1));
        Assert.Equal(800m, await DebtAsync(db));

        Assert.Null(await Rates(db).SetAsync(900m, "mgr"));

        Assert.Equal(900m, await Rates(db).GetAsync());
        Assert.Equal(800m, await DebtAsync(db));
        await Shift(db, "after", ShiftClock.UtcNow.AddSeconds(1));
        Assert.Equal(800m + 900m, await DebtAsync(db));

        // A second change keeps both earlier rates.
        await Task.Delay(1500);
        Assert.Null(await Rates(db).SetAsync(1000m, "mgr"));
        await Shift(db, "latest", ShiftClock.UtcNow.AddSeconds(1));
        Assert.Equal(800m + 900m + 1000m, await DebtAsync(db));

        DocumentSnapshot saved = await config.GetSnapshotAsync();
        Assert.Equal("1234", saved.GetValue<string>("duressPin")); // other settings untouched
        Assert.Equal(2, saved.GetValue<List<string>>("boundaryRateHistory").Count);
        Assert.Equal(2, (await db.Collection("audit_logs").WhereEqualTo("actionType", "BoundaryRateChanged").GetSnapshotAsync()).Count);
    }

    [EmulatorFact]
    public async Task SameOrInvalidRate_IsRefused_AndNothingIsWritten()
    {
        FirestoreDb db = await FreshAsync();

        Assert.NotNull(await Rates(db).SetAsync(800m, "mgr")); // already the default
        Assert.NotNull(await Rates(db).SetAsync(0m, "mgr"));
        Assert.NotNull(await Rates(db).SetAsync(850.25m, "mgr"));

        Assert.False((await db.Collection("system_configs").Document("global").GetSnapshotAsync()).Exists);
        Assert.Empty((await db.Collection("audit_logs").GetSnapshotAsync()).Documents);
    }
}

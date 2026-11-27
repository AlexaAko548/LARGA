using FirebaseAdmin.Auth;
using Google.Cloud.Firestore;
using LARGA.SharedCore;
using LARGA.SharedCore.Models.Fleet;
using LARGA.SharedCore.Services;
using LARGA.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace LARGA.Tests.Fleet;

/// <summary>FleetUnitService and DriverRemovalService against the local Firestore emulator.</summary>
[Collection("emulator")]
public class FleetEmulatorTests
{
    private static async Task<FirestoreDb> FreshAsync()
    {
        await Emu.ClearAsync();
        FirestoreDb db = Emu.Db();
        await Put(db, "users", "mgr1", new() { ["fullName"] = "Maria Manager", ["role"] = "Manager" });
        await Put(db, "users", "am1", new() { ["fullName"] = "Ana Assistant", ["role"] = "Assistant Manager" });
        await Put(db, "users", "drv1", new() { ["fullName"] = "Juan Dela Cruz", ["role"] = "Driver", ["assignedTaxiId"] = "" });
        await Put(db, "taxis", "TAXI_001", new()
        {
            ["taxiId"] = "TAXI_001", ["plateNumber"] = "GAB 1001", ["model"] = "Toyota Vios", ["yearManufactured"] = 2020,
            ["currentMileage"] = 1000, ["status"] = "Active Unit",
        });
        return db;
    }

    private static FleetUnitService Fleet(FirestoreDb db) =>
        new(new Lazy<FirestoreDb>(db), NullLogger<FleetUnitService>.Instance);

    // No Auth in these tests: deleting the login fails, which the service reports as a warning.
    private static DriverRemovalService Removal(FirestoreDb db) =>
        new(new Lazy<FirestoreDb>(db),
            new Lazy<FirebaseAuth>(() => throw new InvalidOperationException("no auth in tests")),
            Ledger(db), NullLogger<DriverRemovalService>.Instance);

    private static FinancialLedgerService Ledger(FirestoreDb db)
    {
        var lazy = new Lazy<FirestoreDb>(db);
        return new FinancialLedgerService(lazy,
            new Lazy<PhotoStorageTarget>(() => throw new InvalidOperationException("no storage in tests")),
            NullLogger<FinancialLedgerService>.Instance, new CollectionCache(lazy, NullLogger<CollectionCache>.Instance));
    }

    private static Task Put(FirestoreDb db, string collection, string id, Dictionary<string, object> fields) =>
        db.Collection(collection).Document(id).SetAsync(fields);

    private static Task<DocumentSnapshot> Doc(FirestoreDb db, string collection, string id) =>
        db.Collection(collection).Document(id).GetSnapshotAsync();

    private static async Task<List<string>> Audits(FirestoreDb db) =>
        (await db.Collection("audit_logs").GetSnapshotAsync()).Documents.Select(d => d.GetValue<string>("actionType")).ToList();

    private static Task CompletedShift(FirestoreDb db, string endStatus = "Completed", int endMileage = 5000) =>
        Put(db, "shifts", "s1", new()
        {
            ["driverId"] = "drv1", ["taxiId"] = "TAXI_001", ["status"] = endStatus, ["shiftStart"] = DateTime.UtcNow.AddDays(-2),
            ["startMileage"] = 4000, ["endMileage"] = endMileage,
        });

    private static FleetUnitInput Input(string plate = "GAB 2002", int mileage = 0, string? status = null) =>
        new() { PlateNumber = plate, Model = "Nissan Almera", YearManufactured = 2023, Mileage = mileage, Status = status };

    [EmulatorFact]
    public async Task Add_AssignsTheNextId_AndAudits()
    {
        FirestoreDb db = await FreshAsync();

        FleetActionResult result = await Fleet(db).AddUnitAsync(Input("gab 2002", 15), "mgr1");

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal("TAXI_002", result.TaxiId);
        DocumentSnapshot unit = await Doc(db, "taxis", "TAXI_002");
        Assert.Equal("GAB 2002", unit.GetValue<string>("plateNumber"));
        Assert.Equal("Active Unit", unit.GetValue<string>("status"));
        Assert.Equal(["TaxiUnitAdded"], await Audits(db));
    }

    [EmulatorFact]
    public async Task Add_DuplicatePlate_Refused()
    {
        FirestoreDb db = await FreshAsync();

        FleetActionResult result = await Fleet(db).AddUnitAsync(Input("gab-1001"), "mgr1");

        Assert.False(result.Ok);
        Assert.Contains("TAXI_001", result.ErrorMessage);
        Assert.Single((await db.Collection("taxis").GetSnapshotAsync()).Documents);
    }

    [EmulatorFact]
    public async Task OnlyTheManagerChangesUnits_AndDeletesDrivers()
    {
        FirestoreDb db = await FreshAsync();

        Assert.False((await Fleet(db).AddUnitAsync(Input(), "am1")).Ok);
        Assert.False((await Fleet(db).UpdateUnitAsync("TAXI_001", Input("GAB 1001", 1000), "drv1")).Ok);
        Assert.False((await Fleet(db).DeleteUnitAsync("TAXI_001", "")).Ok);
        Assert.False((await Removal(db).DeleteAsync("drv1", "am1")).Ok);

        Assert.Equal("Toyota Vios", (await Doc(db, "taxis", "TAXI_001")).GetValue<string>("model"));
        Assert.Equal("Driver", (await Doc(db, "users", "drv1")).GetValue<string>("role"));
        Assert.Empty(await Audits(db));
    }

    [EmulatorFact]
    public async Task Update_MileageBelowTheLatestReading_Refused()
    {
        FirestoreDb db = await FreshAsync();
        await CompletedShift(db);

        FleetActionResult low = await Fleet(db).UpdateUnitAsync("TAXI_001", Input("GAB 1001", 4999), "mgr1");
        FleetActionResult ok = await Fleet(db).UpdateUnitAsync("TAXI_001", Input("GAB 1001", 5000, "Under Maintenance"), "mgr1");

        Assert.False(low.Ok);
        Assert.Contains("5,000", low.ErrorMessage);
        Assert.True(ok.Ok, ok.ErrorMessage);
        DocumentSnapshot unit = await Doc(db, "taxis", "TAXI_001");
        Assert.Equal(5000, unit.GetValue<int>("currentMileage"));
        Assert.Equal("Under Maintenance", unit.GetValue<string>("status"));
    }

    [EmulatorFact]
    public async Task List_ShowsTheHigherOfTheRecordedAndShiftMileage()
    {
        FirestoreDb db = await FreshAsync(); // recorded: 1,000 km
        await CompletedShift(db);

        Assert.Equal(5000, Assert.Single(await Fleet(db).GetUnitsAsync()).CurrentMileage);
        Assert.True((await Fleet(db).UpdateUnitAsync("TAXI_001", Input("GAB 1001", 6000), "mgr1")).Ok);
        Assert.Equal(6000, Assert.Single(await Fleet(db).GetUnitsAsync()).CurrentMileage);
    }

    [EmulatorFact]
    public async Task Update_CannotReturnAUnitToServiceWhileItIsInTheShop()
    {
        FirestoreDb db = await FreshAsync();
        await db.Collection("taxis").Document("TAXI_001").UpdateAsync("status", "Under Maintenance");
        await Put(db, "maintenance_logs", "m1", new() { ["taxiId"] = "TAXI_001", ["status"] = "InProgress" });

        FleetActionResult result = await Fleet(db).UpdateUnitAsync("TAXI_001", Input("GAB 1001", 1000, "Active Unit"), "mgr1");

        Assert.False(result.Ok);
        Assert.Equal("Under Maintenance", (await Doc(db, "taxis", "TAXI_001")).GetValue<string>("status"));
    }

    [EmulatorFact]
    public async Task Delete_UnusedUnit_RemovesItWithItsRoutineChecks()
    {
        FirestoreDb db = await FreshAsync();
        await Put(db, "routine_checks", "c1", new() { ["taxiId"] = "TAXI_001", ["checkName"] = "Oil change" });

        FleetActionResult result = await Fleet(db).DeleteUnitAsync("TAXI_001", "mgr1");

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.False(result.Retired);
        Assert.False((await Doc(db, "taxis", "TAXI_001")).Exists);
        Assert.False((await Doc(db, "routine_checks", "c1")).Exists);
        Assert.Equal(["TaxiUnitDeleted"], await Audits(db));
    }

    [EmulatorFact]
    public async Task Delete_UnitWithHistory_RetiresIt_ThenRestores()
    {
        FirestoreDb db = await FreshAsync();
        await CompletedShift(db);

        FleetActionResult retired = await Fleet(db).DeleteUnitAsync("TAXI_001", "mgr1");

        Assert.True(retired.Ok, retired.ErrorMessage);
        Assert.True(retired.Retired);
        Assert.Equal("Decommissioned", (await Doc(db, "taxis", "TAXI_001")).GetValue<string>("status"));
        Assert.False((await Fleet(db).DeleteUnitAsync("TAXI_001", "mgr1")).Ok);

        Assert.True((await Fleet(db).RestoreUnitAsync("TAXI_001", "mgr1")).Ok);
        Assert.Equal("Active Unit", (await Doc(db, "taxis", "TAXI_001")).GetValue<string>("status"));
    }

    [EmulatorFact]
    public async Task Add_AfterADelete_NeverReissuesTheId()
    {
        FirestoreDb db = await FreshAsync();

        Assert.Equal("TAXI_002", (await Fleet(db).AddUnitAsync(Input("GAB 2002"), "mgr1")).TaxiId);
        Assert.True((await Fleet(db).DeleteUnitAsync("TAXI_002", "mgr1")).Ok);
        FleetActionResult next = await Fleet(db).AddUnitAsync(Input("GAB 3003"), "mgr1");

        Assert.True(next.Ok, next.ErrorMessage);
        Assert.Equal("TAXI_003", next.TaxiId);
        Assert.Equal("GAB 2002", (await Doc(db, FleetUnitService.DeletedIdsCollection, "TAXI_002")).GetValue<string>("plateNumber"));
    }

    [EmulatorFact]
    public async Task Delete_UnitInUse_RefusedWithEveryReason()
    {
        FirestoreDb db = await FreshAsync();
        await db.Collection("users").Document("drv1").UpdateAsync("assignedTaxiId", "TAXI_001");
        await CompletedShift(db, endStatus: "Active");
        await Put(db, "maintenance_logs", "m1", new() { ["taxiId"] = "TAXI_001", ["status"] = "Scheduled" });
        await Put(db, "clockin_requests", "r1", new() { ["taxiId"] = "TAXI_001", ["driverId"] = "drv1", ["status"] = "Pending" });
        await Put(db, "shift_schedules", "x1", new()
        {
            ["taxiId"] = "TAXI_001", ["driverId"] = "drv2", ["status"] = "Substitute",
            ["scheduledStartTime"] = Emu.Utc(PhilippineTime.Now.Date.AddDays(3)),
        });

        UnitRemovalPlan plan = await Fleet(db).GetRemovalPlanAsync("TAXI_001");
        FleetActionResult result = await Fleet(db).DeleteUnitAsync("TAXI_001", "mgr1");

        Assert.Equal(5, plan.Blockers.Count);
        Assert.False(result.Ok);
        Assert.Equal("Active Unit", (await Doc(db, "taxis", "TAXI_001")).GetValue<string>("status"));
    }

    [EmulatorFact]
    public async Task DeleteDriver_KeepsTheProfileName_AndTidiesUp()
    {
        FirestoreDb db = await FreshAsync();
        await db.Collection("users").Document("drv1").UpdateAsync("assignedTaxiId", "TAXI_001");
        await Put(db, "shift_schedules", "future", new()
        {
            ["driverId"] = "drv1", ["status"] = "DayOff", ["scheduledStartTime"] = Emu.Utc(PhilippineTime.Now.Date.AddDays(2)),
        });
        await Put(db, "shift_schedules", "past", new()
        {
            ["driverId"] = "drv1", ["status"] = "DayOff", ["scheduledStartTime"] = Emu.Utc(PhilippineTime.Now.Date.AddDays(-2)),
        });
        await db.Collection("users").Document("drv1").Collection("fcmTokens").Document("t1").SetAsync(new Dictionary<string, object> { ["token"] = "abc" });

        DriverRemovalResult result = await Removal(db).DeleteAsync("drv1", "mgr1");

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.NotNull(result.Warning); // the login couldn't be deleted without Auth
        DocumentSnapshot profile = await Doc(db, "users", "drv1");
        Assert.Equal("Former Driver", profile.GetValue<string>("role"));
        Assert.Equal("Juan Dela Cruz", profile.GetValue<string>("fullName"));
        Assert.Equal("", profile.GetValue<string>("assignedTaxiId"));
        Assert.False((await Doc(db, "shift_schedules", "future")).Exists);
        Assert.True((await Doc(db, "shift_schedules", "past")).Exists);
        Assert.Empty((await profile.Reference.Collection("fcmTokens").GetSnapshotAsync()).Documents);
        Assert.Equal(["DriverDeleted"], await Audits(db));
        Assert.False((await Removal(db).DeleteAsync("drv1", "mgr1")).Ok);
    }

    [EmulatorFact]
    public async Task DeleteDriver_WhoStillOwes_Refused()
    {
        FirestoreDb db = await FreshAsync();
        await CompletedShift(db); // a completed shift with nothing paid

        DriverRemovalPlan? plan = await Removal(db).GetPlanAsync("drv1");
        DriverRemovalResult result = await Removal(db).DeleteAsync("drv1", "mgr1");

        Assert.Contains("still owe", Assert.Single(plan!.Blockers));
        Assert.False(result.Ok);
        Assert.Equal("Driver", (await Doc(db, "users", "drv1")).GetValue<string>("role"));
    }

    [EmulatorFact]
    public async Task DeleteDriver_BalanceCountsPaymentsAndAdjustments()
    {
        FirestoreDb db = await FreshAsync();
        await CompletedShift(db);
        await Put(db, "system_configs", "global", new() { ["defaultBoundaryRate"] = 900.0 });
        await Put(db, "boundary_payments", "p1", new()
        {
            ["shiftId"] = "s1", ["driverId"] = "drv1", ["expectedBoundary"] = 900.0, ["amountPaid"] = 900.0,
            ["timestamp"] = DateTime.UtcNow, ["transactionId"] = "t1",
        });
        await Put(db, "debt_adjustments", "a1", new()
        {
            ["driverId"] = "drv1", ["amount"] = 250.0, ["reason"] = "penalty", ["timestamp"] = DateTime.UtcNow,
        });

        DriverRemovalResult refused = await Removal(db).DeleteAsync("drv1", "mgr1");
        await Put(db, "debt_adjustments", "a2", new()
        {
            ["driverId"] = "drv1", ["amount"] = -300.0, ["reason"] = "write-off", ["timestamp"] = DateTime.UtcNow,
        });
        DriverRemovalPlan? plan = await Removal(db).GetPlanAsync("drv1");
        DriverRemovalResult deleted = await Removal(db).DeleteAsync("drv1", "mgr1");

        Assert.False(refused.Ok);
        Assert.Contains("250.00", refused.ErrorMessage);
        Assert.Empty(plan!.Blockers);
        Assert.Equal(50m, plan.AdvanceCredit);
        Assert.True(deleted.Ok, deleted.ErrorMessage);
    }

    [EmulatorFact]
    public async Task DeleteDriver_RacingANewCharge_NeverDeletesWithItUnseen()
    {
        for (int round = 0; round < 15; round++)
        {
            FirestoreDb db = await FreshAsync();
            Task<DriverRemovalResult> delete = Removal(db).DeleteAsync("drv1", "mgr1");
            await Task.Delay(round * 8);
            var charge = await Ledger(db).AddAdjustmentAsync("drv1", 500m, "Damaged side mirror", "mgr1");
            DriverRemovalResult result = await delete;

            Assert.True(charge.Ok, charge.ErrorMessage);
            DocumentSnapshot profile = await Doc(db, "users", "drv1");
            DocumentSnapshot saved = Assert.Single((await db.Collection("debt_adjustments").GetSnapshotAsync()).Documents);
            if (result.Ok)
            {
                // Deleted: only allowed when the charge was saved after the deletion was.
                Assert.True(saved.CreateTime!.Value.ToDateTimeOffset() >= profile.UpdateTime!.Value.ToDateTimeOffset(),
                    $"round {round}: deleted although the charge was already saved");
            }
            else
            {
                Assert.Contains("still owe", result.ErrorMessage);
                Assert.Equal("Driver", profile.GetValue<string>("role"));
            }
        }
    }

    [EmulatorFact]
    public async Task DeleteDriver_OnShiftWithOpenSos_Refused()
    {
        FirestoreDb db = await FreshAsync();
        await CompletedShift(db, endStatus: "Active");
        await Put(db, "emergency_alerts", "a1", new() { ["shiftId"] = "s1", ["isResolved"] = false }); // no driverId: matched by shift

        DriverRemovalResult result = await Removal(db).DeleteAsync("drv1", "mgr1");

        Assert.False(result.Ok);
        Assert.Contains("on shift", result.ErrorMessage);
        Assert.Contains("SOS", result.ErrorMessage);
    }

    [EmulatorFact]
    public async Task RetiredUnit_CannotBeAssigned()
    {
        FirestoreDb db = await FreshAsync();
        await db.Collection("taxis").Document("TAXI_001").UpdateAsync("status", "Decommissioned");
        var lazy = new Lazy<FirestoreDb>(db);
        var drivers = new DriverManagementService(lazy,
            new Lazy<FirebaseAuth>(() => throw new InvalidOperationException("no auth in tests")),
            new Lazy<PhotoStorageTarget>(() => throw new InvalidOperationException("no storage in tests")),
            NullLogger<DriverManagementService>.Instance);

        var created = await drivers.CreateDriverAsync("Pedro Santos", "09171234567", null, "TAXI_001");

        Assert.False(created.Ok);
        Assert.Contains("retired", created.ErrorMessage);
        Assert.Empty((await drivers.GetWeekScheduleAsync(DateTime.UtcNow)).TaxiIds);
    }
}

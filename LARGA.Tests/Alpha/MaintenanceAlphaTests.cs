using System.Reflection;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore;
using LARGA.SharedCore.Models.Garage;
using LARGA.SharedCore.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace LARGA.Tests.Alpha;

/// <summary>M4 Vehicle Maintenance (MNT-xxx) - pure rules, and GarageService against the local emulator.</summary>
public class MaintenanceRuleTests
{
    // ---------- MNT-006: unknown priority read safely ----------

    [Fact]
    public void Mnt006_UnknownPriority_ReadsAsLow()
    {
        var converter = new PriorityLevelConverter();
        Assert.Equal(PriorityLevel.Low, converter.FromFirestore("Urgent"));
        Assert.Equal(PriorityLevel.Low, converter.FromFirestore(null));
        Assert.Equal(PriorityLevel.Low, converter.FromFirestore(3L));
        Assert.Equal(PriorityLevel.High, converter.FromFirestore("High"));
    }

    // ---------- MNT-009: work order date validation (exact messages) ----------

    private static GarageService GarageWithoutDb()
    {
        var lazy = new Lazy<FirestoreDb>(() => throw new InvalidOperationException("no DB in this test"));
        return new GarageService(lazy, NullLogger<GarageService>.Instance,
            new InventoryAuditService(lazy, NullLogger<InventoryAuditService>.Instance, new AlertService(lazy, NullLogger<AlertService>.Instance)));
    }

    [Fact]
    public async Task Mnt009_ShopDateYesterday_IsRefusedWithExactMessage()
    {
        DateTime yesterday = Emu.Utc(PhilippineTime.Now.Date.AddDays(-1));
        GarageActionResult result = await GarageWithoutDb().CreateTicketAsync("m1", PriorityLevel.High, "fix", yesterday, null);
        Assert.False(result.Ok);
        Assert.Equal("The shop date can't be in the past.", result.ErrorMessage);
    }

    [Fact]
    public async Task Mnt009_FinishBeforeShopDate_IsRefusedWithExactMessage()
    {
        DateTime shop = Emu.Utc(PhilippineTime.Now.Date.AddDays(3));
        GarageActionResult result = await GarageWithoutDb().CreateTicketAsync("m1", PriorityLevel.High, "fix", shop, shop.AddDays(-1));
        Assert.False(result.Ok);
        Assert.Equal("The expected finish can't be before the shop date.", result.ErrorMessage);
    }

    [Fact]
    public void Mnt009_GaragePageValidator_ShopDateYesterday_ExactMessage()
    {
        MethodInfo m = typeof(LARGA.ManagerWeb.Components.Pages.Garage).GetMethod("FutureDateError", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal("The shop date can't be in the past.", m.Invoke(null, new object?[] { PhilippineTime.Now.Date.AddDays(-1), "The shop date" }));
    }

    // ---------- MNT-010: future-dated ticket is Scheduled, not in shop ----------

    [Fact]
    public void Mnt010_ScheduledPlus3Days_NotInShopUntilThatDay()
    {
        DateTime todayPh = PhilippineTime.Now.Date;
        DateTime shopDay = Emu.Utc(todayPh.AddDays(3));
        DateTime logged = DateTime.UtcNow;
        Assert.False(WorkOrderRules.IsInShopNow(WorkOrderRules.Scheduled, logged, shopDay, todayPh));
        Assert.False(WorkOrderRules.IsInShopNow(WorkOrderRules.Scheduled, logged, shopDay, todayPh.AddDays(2)));
        Assert.True(WorkOrderRules.IsInShopNow(WorkOrderRules.Scheduled, logged, shopDay, todayPh.AddDays(3)));
    }

    // ---------- MNT-013: Garage page helpers (Garage.razor, changed in 5584e9b) ----------

    private static readonly Type GaragePage = typeof(LARGA.ManagerWeb.Components.Pages.Garage);

    internal static string DueText(RoutineCheckEntry c) =>
        (string)GaragePage.GetMethod("RoutineCheckDueText", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { c })!;

    internal static bool IsOverdue(RoutineCheckEntry c) =>
        (bool)GaragePage.GetMethod("IsRoutineCheckOverdue", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { c })!;

    [Fact]
    public void Mnt013_MileageCheckAtZeroKm_ShowsOverdueEvenWithADueDate()
    {
        // As EnsureRecurringChecksAsync writes it: oil change with both a due date and a due mileage.
        var c = new RoutineCheckEntry { DueInKm = 0, DueDate = Emu.Utc(PhilippineTime.Now.Date.AddDays(40)) };
        Assert.True(IsOverdue(c));
        Assert.Equal("Overdue", DueText(c));
    }

    [Fact]
    public void Mnt013_MileageCheckNotYetDue_ShowsKm()
    {
        var c = new RoutineCheckEntry { DueInKm = 150, DueDate = Emu.Utc(PhilippineTime.Now.Date.AddDays(40)) };
        Assert.False(IsOverdue(c));
        Assert.Equal("Due in 150 km", DueText(c));
    }

    [Fact]
    public void Mnt013_DateCheckInThePast_ShowsOverdue()
    {
        var c = new RoutineCheckEntry { DueDate = Emu.Utc(PhilippineTime.Now.Date.AddDays(-1)) };
        Assert.True(IsOverdue(c));
        Assert.Equal("Overdue", DueText(c));
    }

    [Fact]
    public void Mnt016_DefaultOilChangeInterval_Is75Days()
    {
        // Spec: every 2.5 months (~75 days).
        Assert.Equal(75, MaintenanceIntervals.From(null).OilChangeDays);
    }
}

[Collection("emulator")]
public class GarageEmulatorTests
{
    private static DateTime TodayPh => PhilippineTime.Now.Date;

    /// <summary>A Philippine calendar day as UTC, at local noon.</summary>
    private static DateTime PhNoonUtc(DateTime dayPh) => Emu.Utc(dayPh.Date.AddHours(12) - PhilippineTime.Offset);

    private static async Task<(FirestoreDb Db, GarageService Garage)> FreshAsync()
    {
        await Emu.ClearAsync();
        FirestoreDb db = Emu.Db();
        return (db, Emu.Garage(db));
    }

    private static Task Taxi(FirestoreDb db, string id, string status = "Active Unit", int mileage = 60000) =>
        db.Collection("taxis").Document(id).SetAsync(new Dictionary<string, object> { ["taxiId"] = id, ["status"] = status, ["currentMileage"] = mileage });

    private static async Task<string> Job(FirestoreDb db, string taxiId, string status, Dictionary<string, object>? extra = null)
    {
        var data = new Dictionary<string, object>
        {
            ["taxiId"] = taxiId,
            ["issueTitle"] = "AC Not Cooling",
            ["issueDescription"] = "warm air",
            ["status"] = status,
            ["priorityLevel"] = "High",
            ["maintenanceType"] = "Breakdown Repair",
            ["reportedByDriverId"] = "drv1",
            ["dateLogged"] = DateTime.UtcNow,
        };
        foreach (var kv in extra ?? new()) data[kv.Key] = kv.Value;
        DocumentReference doc = await db.Collection("maintenance_logs").AddAsync(data);
        return doc.Id;
    }

    private static async Task<string> TaxiStatus(FirestoreDb db, string id) =>
        (await db.Collection("taxis").Document(id).GetSnapshotAsync()).GetValue<string>("status");

    private static async Task<List<string>> AuditTypes(FirestoreDb db) =>
        (await db.Collection("audit_logs").GetSnapshotAsync()).Documents.Select(d => d.GetValue<string>("actionType")).ToList();

    // ---------- MNT-002: a driver's report as the app writes it shows on the Garage page ----------

    [EmulatorFact]
    public async Task Mnt002_DriverReportWithPhoto_ListedInPendingWithPhoto()
    {
        var (db, garage) = await FreshAsync();
        await Taxi(db, "TAXI_001");
        // Same fields/values MaintenanceService.SubmitMaintenanceRecordAsync writes.
        await db.Collection("maintenance_logs").AddAsync(new Dictionary<string, object>
        {
            ["taxiId"] = "TAXI_001", ["shiftId"] = "", ["managerId"] = "", ["maintenanceType"] = "BreakdownRepair",
            ["issueTitle"] = "Brake noise", ["issueDescription"] = "grinding when braking", ["dateLogged"] = DateTime.UtcNow,
            ["priorityLevel"] = "High", ["supportingPhotoUrl"] = "https://firebasestorage.googleapis.com/x/defect.jpg",
            ["reportedByDriverId"] = "drv1", ["status"] = "Reported",
        });

        GarageSnapshot snap = await garage.GetGarageSnapshotAsync();
        DriverReportEntry report = Assert.Single(snap.PendingReports);
        Assert.Equal(PriorityLevel.High, report.Priority);
        Assert.Equal("https://firebasestorage.googleapis.com/x/defect.jpg", report.SupportingPhotoUrl);
        Assert.Equal("Brake noise", report.IssueTitle);
    }

    // ---------- MNT-006 ----------

    [EmulatorFact]
    public async Task Mnt006_UrgentPriorityDoc_GarageLoadsAndShowsLow()
    {
        var (db, garage) = await FreshAsync();
        await Taxi(db, "TAXI_001");
        await Job(db, "TAXI_001", "Reported", new() { ["priorityLevel"] = "Urgent" });

        GarageSnapshot snap = await garage.GetGarageSnapshotAsync();
        Assert.Equal(PriorityLevel.Low, Assert.Single(snap.PendingReports).Priority);
    }

    // ---------- MNT-007: dismiss ----------

    [EmulatorFact]
    public async Task Mnt007_Dismiss_RemovesFromQueue_UnitUnchanged()
    {
        var (db, garage) = await FreshAsync();
        await Taxi(db, "TAXI_002", "Active Unit");
        string ac = await Job(db, "TAXI_002", "Reported");
        await Job(db, "TAXI_002", "Reported", new() { ["issueTitle"] = "Wiper broken" });
        Assert.Equal(2, (await garage.GetGarageSnapshotAsync()).PendingReports.Count);

        GarageActionResult result = await garage.DismissReportAsync(ac, "mgr1");

        Assert.True(result.Ok);
        GarageSnapshot after = await garage.GetGarageSnapshotAsync();
        Assert.Single(after.PendingReports);
        Assert.DoesNotContain(after.PendingReports, r => r.MaintenanceId == ac);
        Assert.Equal("Active Unit", await TaxiStatus(db, "TAXI_002"));
        Assert.Contains("MaintenanceDismissed", await AuditTypes(db));
    }

    // ---------- MNT-008: schedule an upcoming routine check ----------

    [EmulatorFact]
    public async Task Mnt008_ScheduleRoutineCheck_BecomesOneDayWorkOrder()
    {
        var (db, garage) = await FreshAsync();
        await Taxi(db, "TAXI_001", mileage: 60000);
        await db.Collection("routine_checks").Document("chk1").SetAsync(new Dictionary<string, object>
        {
            ["taxiId"] = "TAXI_001", ["checkName"] = "Oil change", ["dueMileage"] = 60150, ["routineType"] = "OilChange",
        });
        RoutineCheckEntry entry = Assert.Single((await garage.GetGarageSnapshotAsync()).UpcomingChecks);
        Assert.Equal(150, entry.DueInKm);

        DateTime day = Emu.Utc(TodayPh.AddDays(2));
        GarageActionResult result = await garage.ScheduleRoutineCheckAsync("chk1", day, "mgr1");

        Assert.True(result.Ok);
        GarageSnapshot snap = await garage.GetGarageSnapshotAsync();
        Assert.Empty(snap.UpcomingChecks);
        WorkOrderEntry job = Assert.Single(snap.ScheduledWorkOrders);
        Assert.Equal(day.Date, job.ScheduledDate!.Value.Date);
        Assert.Equal(day.Date, job.EstimatedCompletionDate!.Value.Date); // one-day job
        Assert.Contains("RoutineCheckScheduled", await AuditTypes(db));
    }

    // ---------- MNT-010: future ticket is Scheduled; unit stays drivable ----------

    [EmulatorFact]
    public async Task Mnt010_TicketPlus3Days_IsScheduled_UnitStaysActive()
    {
        var (db, garage) = await FreshAsync();
        await Taxi(db, "TAXI_003", "Active Unit");
        string id = await Job(db, "TAXI_003", "Reported");

        GarageActionResult result = await garage.CreateTicketAsync(id, PriorityLevel.Medium, "check compressor", Emu.Utc(TodayPh.AddDays(3)), null, "mgr1");

        Assert.True(result.Ok);
        DocumentSnapshot doc = await db.Collection("maintenance_logs").Document(id).GetSnapshotAsync();
        Assert.Equal(WorkOrderRules.Scheduled, doc.GetValue<string>("status"));
        GarageSnapshot snap = await garage.GetGarageSnapshotAsync();
        Assert.Single(snap.ScheduledWorkOrders);
        Assert.Empty(snap.ActiveWorkOrders);
        Assert.Equal("Active Unit", await TaxiStatus(db, "TAXI_003"));
    }

    [EmulatorFact]
    public async Task Mnt010_ScheduledTicketWhoseDayHasCome_MovesToInProgress()
    {
        var (db, garage) = await FreshAsync();
        await Taxi(db, "TAXI_003", "Active Unit");
        // Seeded same-day Scheduled ticket (as if booked 3 days ago for today).
        string id = await Job(db, "TAXI_003", WorkOrderRules.Scheduled, new() { ["scheduledDate"] = Emu.Utc(TodayPh) });

        GarageSnapshot snap = await garage.GetGarageSnapshotAsync();

        Assert.Single(snap.ActiveWorkOrders);
        Assert.Equal(WorkOrderRules.InProgress, (await db.Collection("maintenance_logs").Document(id).GetSnapshotAsync()).GetValue<string>("status"));
    }

    // ---------- MNT-011: unit stays in maintenance until its last open job closes ----------

    [EmulatorFact]
    public async Task Mnt011_UnitReturnsToServiceOnlyAfterLastJob()
    {
        var (db, garage) = await FreshAsync();
        await Taxi(db, "TAXI_002", "Under Maintenance");
        string first = await Job(db, "TAXI_002", WorkOrderRules.InProgress);
        string second = await Job(db, "TAXI_002", WorkOrderRules.InProgress, new() { ["issueTitle"] = "Brake pads" });

        Assert.True((await garage.MarkResolvedAsync(first, "mgr1")).Ok);
        Assert.Equal("Under Maintenance", await TaxiStatus(db, "TAXI_002"));

        Assert.True((await garage.MarkResolvedAsync(second, "mgr1")).Ok);
        Assert.Equal("Active Unit", await TaxiStatus(db, "TAXI_002"));
    }

    // ---------- MNT-012: unschedule returns each item to its origin ----------

    [EmulatorFact]
    public async Task Mnt012_Unschedule_RoutineToUpcoming_DriverReportToPending()
    {
        var (db, garage) = await FreshAsync();
        await Taxi(db, "TAXI_001");
        DateTime day = Emu.Utc(TodayPh.AddDays(2));
        string routine = await Job(db, "TAXI_001", WorkOrderRules.Scheduled, new()
        {
            ["reportedByDriverId"] = "", ["maintenanceType"] = "Routine Checkup", ["issueTitle"] = "Oil change",
            ["routineType"] = "OilChange", ["scheduledDate"] = day, ["estimatedCompletionDate"] = day, ["priorityLevel"] = "Low",
        });
        string driverJob = await Job(db, "TAXI_001", WorkOrderRules.Scheduled, new() { ["scheduledDate"] = day });

        Assert.True((await garage.UnscheduleAsync(routine, "mgr1")).Ok);
        Assert.True((await garage.UnscheduleAsync(driverJob, "mgr1")).Ok);

        GarageSnapshot snap = await garage.GetGarageSnapshotAsync();
        Assert.Empty(snap.ScheduledWorkOrders);
        RoutineCheckEntry check = Assert.Single(snap.UpcomingChecks);
        Assert.Equal("Oil change", check.CheckName);
        Assert.Equal(day.Date, check.DueDate!.Value.Date);
        DriverReportEntry report = Assert.Single(snap.PendingReports);
        Assert.Equal(driverJob, report.MaintenanceId);
    }

    // ---------- MNT-013: oil change dueMileage 60,000, unit odometer 60,050 ----------

    [EmulatorFact]
    public async Task Mnt013_MileageOverdue_ShowsOverdueOnGaragePage()
    {
        var (db, garage) = await FreshAsync();
        await Taxi(db, "TAXI_001", mileage: 59000);
        await db.Collection("shifts").Document("s1").SetAsync(new Dictionary<string, object>
        {
            ["shiftId"] = "s1", ["taxiId"] = "TAXI_001", ["driverId"] = "drv1", ["status"] = "Completed",
            ["shiftStart"] = DateTime.UtcNow.AddDays(-1), ["startMileage"] = 59900, ["endMileage"] = 60050,
        });
        // As EnsureRecurringChecksAsync writes an oil change for a unit with a mileage interval.
        await db.Collection("routine_checks").Document("TAXI_001_OilChange_x").SetAsync(new Dictionary<string, object>
        {
            ["taxiId"] = "TAXI_001", ["checkName"] = "Oil change", ["dueMileage"] = 60000,
            ["dueDate"] = Emu.Utc(TodayPh.AddDays(30)), ["routineType"] = "OilChange",
        });

        RoutineCheckEntry entry = Assert.Single((await garage.GetGarageSnapshotAsync()).UpcomingChecks);

        Assert.Equal(0, entry.DueInKm);
        Assert.True(MaintenanceRuleTests.IsOverdue(entry));
        Assert.Equal("Overdue", MaintenanceRuleTests.DueText(entry));
    }

    // ---------- MNT-014/015/016: recurring checks and their alerts ----------

    private static Task ResolvedRoutineJob(FirestoreDb db, string taxiId, string type, int daysAgo) =>
        Job(db, taxiId, "Resolved", new()
        {
            ["reportedByDriverId"] = "", ["maintenanceType"] = "Routine Checkup", ["issueTitle"] = MaintenanceSchedule.DisplayName(type),
            ["routineType"] = type, ["dateResolved"] = PhNoonUtc(TodayPh.AddDays(-daysAgo)), ["priorityLevel"] = "Low",
        });

    private static async Task<SystemAlert?> AlertFor(FirestoreDb db, string taxiId, string type, DateTime dueDayPh, string alertType)
    {
        string id = MaintenanceSchedule.AlertId(MaintenanceSchedule.CheckId(taxiId, type, dueDayPh), alertType);
        DocumentSnapshot snap = await db.Collection("system_alerts").Document(id).GetSnapshotAsync();
        return snap.Exists ? snap.ConvertTo<SystemAlert>() : null;
    }

    private static async Task<int> RoutineAlertCount(FirestoreDb db, string taxiId, string type) =>
        (await db.Collection("system_alerts").GetSnapshotAsync()).Documents
            .Count(d => d.Id.StartsWith($"routine_{taxiId}_{type}_", StringComparison.Ordinal));

    [EmulatorFact]
    public async Task Mnt014_WeeklyDoneSevenDaysAgo_CheckAndBellAlertOnDay7()
    {
        var (db, garage) = await FreshAsync();
        await Taxi(db, "TAXI_001");
        await ResolvedRoutineJob(db, "TAXI_001", MaintenanceSchedule.WeeklyInspection, 7);

        await garage.EnsureRecurringChecksAsync();
        await garage.RaiseRoutineCheckAlertsAsync();

        GarageSnapshot snap = await garage.GetGarageSnapshotAsync();
        RoutineCheckEntry weekly = Assert.Single(snap.UpcomingChecks, c => c.CheckName == "Weekly inspection");
        Assert.Equal(TodayPh, weekly.DueDate!.Value.Date);
        SystemAlert? alert = await AlertFor(db, "TAXI_001", MaintenanceSchedule.WeeklyInspection, TodayPh, MaintenanceSchedule.DueSoonAlertType);
        Assert.NotNull(alert);
        Assert.Equal("Weekly inspection for Unit 01 is due today.", alert!.Message);
    }

    [EmulatorFact]
    public async Task Mnt015_Biweekly_AlertOnDay14()
    {
        var (db, garage) = await FreshAsync();
        await Taxi(db, "TAXI_001");
        await ResolvedRoutineJob(db, "TAXI_001", MaintenanceSchedule.BiweeklyCheckup, 14);

        await garage.EnsureRecurringChecksAsync();
        await garage.RaiseRoutineCheckAlertsAsync();

        Assert.NotNull(await AlertFor(db, "TAXI_001", MaintenanceSchedule.BiweeklyCheckup, TodayPh, MaintenanceSchedule.DueSoonAlertType));
    }

    [EmulatorFact]
    public async Task Mnt015_Biweekly_NoAlertOnDay13()
    {
        var (db, garage) = await FreshAsync();
        await Taxi(db, "TAXI_001");
        await ResolvedRoutineJob(db, "TAXI_001", MaintenanceSchedule.BiweeklyCheckup, 13);

        await garage.EnsureRecurringChecksAsync();
        await garage.RaiseRoutineCheckAlertsAsync();

        Assert.Equal(0, await RoutineAlertCount(db, "TAXI_001", MaintenanceSchedule.BiweeklyCheckup));
    }

    [EmulatorFact]
    public async Task Mnt016_OilChange_AlertOnDay75()
    {
        var (db, garage) = await FreshAsync();
        await Taxi(db, "TAXI_001");
        await ResolvedRoutineJob(db, "TAXI_001", MaintenanceSchedule.OilChange, 75);

        await garage.EnsureRecurringChecksAsync();
        await garage.RaiseRoutineCheckAlertsAsync();

        Assert.True(await RoutineAlertCount(db, "TAXI_001", MaintenanceSchedule.OilChange) > 0);
    }

    [EmulatorFact]
    public async Task Mnt016_OilChange_NoAlertOnDay74()
    {
        var (db, garage) = await FreshAsync();
        await Taxi(db, "TAXI_001");
        await ResolvedRoutineJob(db, "TAXI_001", MaintenanceSchedule.OilChange, 74);

        await garage.EnsureRecurringChecksAsync();
        await garage.RaiseRoutineCheckAlertsAsync();

        Assert.Equal(0, await RoutineAlertCount(db, "TAXI_001", MaintenanceSchedule.OilChange));
    }

    [EmulatorFact]
    public async Task Mnt016_AfterResolve_NextOilChangeDueIn75Days()
    {
        var (db, garage) = await FreshAsync();
        await Taxi(db, "TAXI_001");
        await ResolvedRoutineJob(db, "TAXI_001", MaintenanceSchedule.OilChange, 0);

        await garage.EnsureRecurringChecksAsync();

        RoutineCheckEntry oil = Assert.Single((await garage.GetGarageSnapshotAsync()).UpcomingChecks, c => c.CheckName == "Oil change");
        Assert.Equal(TodayPh.AddDays(75), oil.DueDate!.Value.Date);
    }

    // What the same day-13 / day-74 checks give with system_configs.maintenanceDueSoonDays = 0,
    // to separate "the DueSoon window" from "the interval" as root causes.
    [EmulatorFact]
    public async Task Diag_DueSoonZero_Day13HasNoBiweeklyAlert()
    {
        var (db, garage) = await FreshAsync();
        await db.Collection("system_configs").Document("global").SetAsync(new Dictionary<string, object> { ["maintenanceDueSoonDays"] = 0 });
        await Taxi(db, "TAXI_001");
        await ResolvedRoutineJob(db, "TAXI_001", MaintenanceSchedule.BiweeklyCheckup, 13);

        await garage.EnsureRecurringChecksAsync();
        await garage.RaiseRoutineCheckAlertsAsync();

        Assert.Equal(0, await RoutineAlertCount(db, "TAXI_001", MaintenanceSchedule.BiweeklyCheckup));
    }

    // GPS-014's root cause (a bad fuelExpectedKmPerLiter value) also hits GarageService, which still
    // reads system_configs/global as a whole SystemConfig: a configured interval is then ignored.
    [EmulatorFact]
    public async Task Diag_BadUnrelatedConfigField_StillHonoursConfiguredWeeklyInterval()
    {
        var (db, garage) = await FreshAsync();
        await db.Collection("system_configs").Document("global").SetAsync(new Dictionary<string, object>
        {
            ["maintenanceWeeklyIntervalDays"] = 10, ["fuelExpectedKmPerLiter"] = "",
        });
        await Taxi(db, "TAXI_001");
        await ResolvedRoutineJob(db, "TAXI_001", MaintenanceSchedule.WeeklyInspection, 0);

        await garage.EnsureRecurringChecksAsync();

        RoutineCheckEntry weekly = Assert.Single((await garage.GetGarageSnapshotAsync()).UpcomingChecks, c => c.CheckName == "Weekly inspection");
        Assert.Equal(TodayPh.AddDays(10), weekly.DueDate!.Value.Date);
    }
}

using Google.Cloud.Firestore;
using LARGA.SharedCore;
using LARGA.SharedCore.Models.DriverShifts;
using LARGA.SharedCore.Services;
using LARGA.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace LARGA.Tests.Shifts;

/// <summary>ManagerWeb's clock-in Approve / Deny (ClockInApprovalService): every write of a decision
/// lands in one transaction, and only while the request is still Pending (gap #13).</summary>
[Collection("emulator")]
public class ClockInDecisionEmulatorTests
{
    private static async Task<(FirestoreDb Db, ClockInApprovalService Service)> FreshAsync()
    {
        await Emu.ClearAsync();
        FirestoreDb db = Emu.Db();
        await db.Collection("users").Document("drv1").SetAsync(new Dictionary<string, object>
        {
            ["fullName"] = "Juan Dela Cruz", ["role"] = "Driver",
            ["licenseExpiryDate"] = DateTime.UtcNow.AddYears(2),
        });
        await db.Collection("taxis").Document("TAXI_001").SetAsync(new Dictionary<string, object>
        {
            ["taxiId"] = "TAXI_001", ["status"] = "Active Unit",
        });
        return (db, new ClockInApprovalService(new Lazy<FirestoreDb>(db), NullLogger<ClockInApprovalService>.Instance));
    }

    private static Task Request(FirestoreDb db, string status = "Pending", string defectIds = "", bool tiresOk = true) =>
        db.Collection("clockin_requests").Document("req1").SetAsync(new Dictionary<string, object>
        {
            ["driverId"] = "drv1", ["taxiId"] = "TAXI_001", ["status"] = status,
            ["flagReasons"] = tiresOk ? "Fuel below half-tank" : "Tire Condition failed",
            ["tireCondition"] = tiresOk, ["underTheHood"] = true, ["lightsCondition"] = true,
            ["interiorCleanliness"] = true, ["exteriorCondition"] = true,
            ["defectReportIds"] = defectIds, ["createdAt"] = DateTime.UtcNow,
        });

    private static Task Bell(FirestoreDb db) =>
        db.Collection("system_alerts").Document("req1_CLOCKIN").SetAsync(new Dictionary<string, object>
        {
            ["type"] = ClockInApprovalService.PendingAlertType, ["isRead"] = false, ["timestamp"] = DateTime.UtcNow,
        });

    private static async Task<T> Field<T>(FirestoreDb db, string collection, string id, string field) =>
        (await db.Collection(collection).Document(id).GetSnapshotAsync()).GetValue<T>(field);

    private static async Task<int> Count(FirestoreDb db, string collection) =>
        (await db.Collection(collection).GetSnapshotAsync()).Count;

    [EmulatorFact]
    public async Task Deny_WritesEverythingTogether_SkipsMissingDefect()
    {
        var (db, service) = await FreshAsync();
        await db.Collection("maintenance_logs").Document("def1").SetAsync(new Dictionary<string, object>
        {
            ["taxiId"] = "TAXI_001", ["status"] = "Reported", ["priorityLevel"] = "Low", ["reportedByDriverId"] = "drv1",
        });
        await Request(db, defectIds: "def1, gone");
        await Bell(db);

        ClockInDecisionResult result = await service.DenyAsync("req1", "Bring it to the garage", markUnitUnderMaintenance: true, "mgr1");

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal("Denied", await Field<string>(db, "clockin_requests", "req1", "status"));
        Assert.Equal("Bring it to the garage", await Field<string>(db, "clockin_requests", "req1", "managerNote"));
        Assert.Equal("High", await Field<string>(db, "maintenance_logs", "def1", "priorityLevel"));
        Assert.False((await db.Collection("maintenance_logs").Document("gone").GetSnapshotAsync()).Exists);
        Assert.Equal(1, await Count(db, "maintenance_logs")); // an existing report: no extra one created
        Assert.Equal(TaxiStatusRules.UnderMaintenance, await Field<string>(db, "taxis", "TAXI_001", "status"));
        Assert.True(await Field<bool>(db, "system_alerts", "req1_CLOCKIN", "isRead"));
        Assert.Equal(1, await Count(db, "audit_logs"));
    }

    [EmulatorFact]
    public async Task Deny_NoReport_FailedItems_CreatesGarageReport()
    {
        var (db, service) = await FreshAsync();
        await Request(db, tiresOk: false);

        ClockInDecisionResult result = await service.DenyAsync("req1", "Tires are bald", markUnitUnderMaintenance: false, "mgr1");

        Assert.True(result.Ok, result.ErrorMessage);
        DocumentSnapshot report = Assert.Single((await db.Collection("maintenance_logs").GetSnapshotAsync()).Documents);
        Assert.Equal("Failed pre-shift inspection", report.GetValue<string>("issueTitle"));
        Assert.Equal("High", report.GetValue<string>("priorityLevel"));
        Assert.Equal("Active Unit", await Field<string>(db, "taxis", "TAXI_001", "status"));
    }

    [EmulatorFact]
    public async Task Deny_AlreadyDecided_RefusedAndNothingWritten()
    {
        var (db, service) = await FreshAsync();
        await Request(db, status: "Approved", tiresOk: false);

        ClockInDecisionResult result = await service.DenyAsync("req1", "Too late", markUnitUnderMaintenance: true, "mgr1");

        Assert.False(result.Ok);
        Assert.Equal("Approved", await Field<string>(db, "clockin_requests", "req1", "status"));
        Assert.Equal("Active Unit", await Field<string>(db, "taxis", "TAXI_001", "status"));
        Assert.Equal(0, await Count(db, "maintenance_logs"));
        Assert.Equal(0, await Count(db, "audit_logs"));
    }

    [EmulatorFact]
    public async Task Approve_DecisionAuditAndBellTogether()
    {
        var (db, service) = await FreshAsync();
        await Request(db);
        await Bell(db);

        ClockInDecisionResult result = await service.ApproveAsync("req1", null, "mgr1");

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal("Approved", await Field<string>(db, "clockin_requests", "req1", "status"));
        Assert.True(await Field<bool>(db, "system_alerts", "req1_CLOCKIN", "isRead"));
        Assert.Equal(1, await Count(db, "audit_logs"));
    }
}

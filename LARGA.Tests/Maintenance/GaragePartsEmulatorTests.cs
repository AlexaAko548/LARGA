using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore;
using LARGA.SharedCore.Models.Garage;
using LARGA.SharedCore.Services;
using LARGA.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace LARGA.Tests.Maintenance;

/// <summary>
/// #59: GarageService.LogPartsUsedAsync deducts a ticket's whole parts list in one transaction
/// (InventoryAuditService.DeductPartsAsync) - all or nothing - and says clearly when a part was
/// archived while the form was open. Also checks the LowStock alert follows the Inventory page's
/// rule (SparePartStockRules) as stock goes down and back up. Local Firestore emulator only.
/// </summary>
[Collection("emulator")]
public class GaragePartsEmulatorTests
{
    private static async Task<(FirestoreDb Db, GarageService Garage)> FreshAsync()
    {
        await Emu.ClearAsync();
        FirestoreDb db = Emu.Db();
        await db.Collection("maintenance_logs").Document("job1").SetAsync(new Dictionary<string, object>
        {
            ["maintenanceId"] = "job1",
            ["taxiId"] = "TX-01",
            ["issueTitle"] = "Brakes",
            ["status"] = "In Progress",
            ["dateLogged"] = DateTime.UtcNow,
        });
        return (db, Emu.Garage(db));
    }

    private static Task Part(FirestoreDb db, string id, int stock, int minimum = 2, bool archived = false) =>
        db.Collection("spare_parts").Document(id).SetAsync(new Dictionary<string, object>
        {
            ["partName"] = id,
            ["stockQuantity"] = stock,
            ["reorderLevel"] = minimum,
            ["unit"] = "pcs",
            ["unitPrice"] = 100.0,
            ["isArchived"] = archived,
        });

    private static async Task<int> Stock(FirestoreDb db, string id) =>
        (await db.Collection("spare_parts").Document(id).GetSnapshotAsync()).GetValue<int>("stockQuantity");

    private static async Task<int> Count(FirestoreDb db, string collection) =>
        (await db.Collection(collection).GetSnapshotAsync()).Count;

    [EmulatorFact]
    public async Task AllPartsAvailable_EveryLineIsDeductedAndLogged()
    {
        var (db, garage) = await FreshAsync();
        await Part(db, "Pads", 10);
        await Part(db, "Fluid", 5);

        GarageActionResult result = await garage.LogPartsUsedAsync("job1", new[] { ("Pads", 2), ("Fluid", 1), ("Pads", 3) });

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal(5, await Stock(db, "Pads"));
        Assert.Equal(4, await Stock(db, "Fluid"));
        Assert.Equal(3, await Count(db, "maintenance_parts_used"));
        Assert.Equal(3, await Count(db, "audit_logs"));
    }

    [EmulatorFact]
    public async Task ArchivedPartLaterInTheList_NothingIsDeducted()
    {
        var (db, garage) = await FreshAsync();
        await Part(db, "Pads", 10);
        await Part(db, "OldFilter", 10, archived: true);

        GarageActionResult result = await garage.LogPartsUsedAsync("job1", new[] { ("Pads", 2), ("OldFilter", 1) });

        Assert.False(result.Ok);
        Assert.Contains("'OldFilter' was archived, so nothing was deducted", result.ErrorMessage);
        Assert.Equal(10, await Stock(db, "Pads"));
        Assert.Equal(0, await Count(db, "maintenance_parts_used"));
        Assert.Equal(0, await Count(db, "audit_logs"));
    }

    [EmulatorFact]
    public async Task NotEnoughForTheLinesTogether_NothingIsDeducted()
    {
        var (db, garage) = await FreshAsync();
        await Part(db, "Pads", 4);
        await Part(db, "Fluid", 5);

        // 3 + 2 Pads is more than the 4 in stock, though each line alone fits.
        GarageActionResult result = await garage.LogPartsUsedAsync("job1", new[] { ("Fluid", 1), ("Pads", 3), ("Pads", 2) });

        Assert.False(result.Ok);
        Assert.Contains("Only 4 pcs of Pads in stock", result.ErrorMessage);
        Assert.Contains("Nothing was deducted", result.ErrorMessage);
        Assert.Equal(5, await Stock(db, "Fluid"));
        Assert.Equal(4, await Stock(db, "Pads"));
        Assert.Equal(0, await Count(db, "maintenance_parts_used"));
    }

    [EmulatorFact]
    public async Task MissingPart_NothingIsDeducted()
    {
        var (db, garage) = await FreshAsync();
        await Part(db, "Pads", 10);

        GarageActionResult result = await garage.LogPartsUsedAsync("job1", new[] { ("Pads", 1), ("Gone", 1) });

        Assert.False(result.Ok);
        Assert.Equal(10, await Stock(db, "Pads"));
        Assert.Equal(0, await Count(db, "audit_logs"));
    }

    [EmulatorFact]
    public async Task LowStockAlert_IsCriticalAtHalfTheMinimum_LikeTheInventoryPage()
    {
        var (db, garage) = await FreshAsync();
        await Part(db, "Pads", 12, minimum: 10);

        await garage.LogPartsUsedAsync("job1", new[] { ("Pads", 4) }); // 8 left: Low Stock
        DocumentSnapshot alert = await db.Collection("system_alerts").Document(AlertService.LowStockAlertId("Pads")).GetSnapshotAsync();
        Assert.Equal(SparePartStockRules.LowStock, alert.GetValue<string>("status"));

        await garage.LogPartsUsedAsync("job1", new[] { ("Pads", 3) }); // 5 left: half the minimum, Critical
        alert = await db.Collection("system_alerts").Document(AlertService.LowStockAlertId("Pads")).GetSnapshotAsync();
        Assert.Equal(SparePartStockRules.Critical, alert.GetValue<string>("status"));
        Assert.Contains("critically low: 5 pcs left", alert.GetValue<string>("message"));
    }

    private static InventoryAuditService Inventory(FirestoreDb db)
    {
        var lazy = new Lazy<FirestoreDb>(db);
        return new InventoryAuditService(lazy, NullLogger<InventoryAuditService>.Instance, new AlertService(lazy, NullLogger<AlertService>.Instance));
    }

    private static async Task<DocumentSnapshot> Alert(FirestoreDb db, string partId) =>
        await db.Collection("system_alerts").Document(AlertService.LowStockAlertId(partId)).GetSnapshotAsync();

    [EmulatorFact]
    public async Task PartialRestock_UpdatesTheOpenAlertQuietly()
    {
        var (db, _) = await FreshAsync();
        InventoryAuditService inventory = Inventory(db);
        await Part(db, "Pads", 2, minimum: 10);

        await inventory.DeductPartAsync("Pads", 1); // 1 left: Critical
        await db.Collection("system_alerts").Document(AlertService.LowStockAlertId("Pads")).UpdateAsync("isRead", true);
        Timestamp raisedAt = (await Alert(db, "Pads")).GetValue<Timestamp>("timestamp");

        await inventory.AddPartStockAsync("Pads", 6); // 7 left: still below the minimum - Low Stock

        DocumentSnapshot alert = await Alert(db, "Pads");
        Assert.Equal(SparePartStockRules.StatusOf(7, 10), alert.GetValue<string>("status"));
        Assert.Equal(SparePartStockRules.LowStock, alert.GetValue<string>("status"));
        Assert.Equal(7, alert.GetValue<int>("stockQuantity"));
        Assert.Contains("7 pcs left", alert.GetValue<string>("message"));
        Assert.True(alert.GetValue<bool>("isRead"));                       // not a new notification
        Assert.Equal(raisedAt, alert.GetValue<Timestamp>("timestamp"));    // keeps its time
    }

    [EmulatorFact]
    public async Task FullRestock_ResolvesTheAlert()
    {
        var (db, _) = await FreshAsync();
        InventoryAuditService inventory = Inventory(db);
        await Part(db, "Pads", 2, minimum: 10);

        await inventory.DeductPartAsync("Pads", 1);
        await inventory.AddPartStockAsync("Pads", 20);

        Assert.Equal(AlertService.ResolvedStatus, (await Alert(db, "Pads")).GetValue<string>("status"));
    }

    [EmulatorFact]
    public async Task LoweringTheMinimum_StillLow_UpdatesTheOpenAlert()
    {
        var (db, _) = await FreshAsync();
        InventoryAuditService inventory = Inventory(db);
        await Part(db, "Pads", 5, minimum: 10);

        SparePart? part = await inventory.DeductPartAsync("Pads", 1); // 4 left of 10: Critical
        Assert.Equal(SparePartStockRules.Critical, (await Alert(db, "Pads")).GetValue<string>("status"));

        part!.ReorderLevel = 6; // 4 of 6: Low Stock
        await inventory.UpdateSparePartAsync(part);

        Assert.Equal(SparePartStockRules.LowStock, (await Alert(db, "Pads")).GetValue<string>("status"));
    }

    [EmulatorFact]
    public async Task RestockWithNoAlert_DoesNotCreateOne()
    {
        var (db, _) = await FreshAsync();
        await Part(db, "Pads", 3, minimum: 10);

        await Inventory(db).AddPartStockAsync("Pads", 1);

        Assert.False((await Alert(db, "Pads")).Exists);
    }
}

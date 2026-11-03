using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore;
using LARGA.Tests.Support;

namespace LARGA.Tests.Ledger;

/// <summary>SystemConfigReader: system_configs/global is hand-edited in the console, so one bad
/// field must only cost that field - not every web setting (BRP-014).</summary>
[Collection("emulator")]
public class SystemConfigReaderEmulatorTests
{
    private static async Task<DocumentSnapshot> ConfigAsync(Dictionary<string, object?>? fields)
    {
        await Emu.ClearAsync();
        FirestoreDb db = Emu.Db();
        DocumentReference doc = db.Collection("system_configs").Document("global");
        if (fields is not null)
        {
            await doc.SetAsync(fields);
        }
        return await doc.GetSnapshotAsync();
    }

    [EmulatorFact]
    public async Task BadFields_KeepTheirDefaults_OtherFieldsStillRead()
    {
        DocumentSnapshot snapshot = await ConfigAsync(new()
        {
            ["defaultBoundaryRate"] = 900,
            ["fuelExpectedKmPerLiter"] = "",
            ["maintenanceDueSoonDays"] = "two",
            ["fuelCostPerKmMax"] = null,
            ["authorizedPayoutAccounts"] = new[] { "0917 123 4567" },
            ["maintenanceWeeklyIntervalDays"] = 10,
            ["duressPin"] = "1234", // not on SystemConfig - ignored
        });
        var bad = new List<string>();

        SystemConfig config = SystemConfigReader.Read(snapshot, (field, _) => bad.Add(field));

        Assert.Equal("global", config.ConfigId);
        Assert.Equal(900, config.DefaultBoundaryRate);
        Assert.Equal(new[] { "0917 123 4567" }, config.AuthorizedPayoutAccounts);
        Assert.Equal(10, config.MaintenanceWeeklyIntervalDays);
        Assert.Empty(config.FuelExpectedKmPerLiter);
        Assert.Equal(new SystemConfig().MaintenanceDueSoonDays, config.MaintenanceDueSoonDays);
        Assert.Equal(new SystemConfig().FuelCostPerKmMax, config.FuelCostPerKmMax);
        // A null in a number field is unreadable too (kept at its default, and reported).
        Assert.Equal(new[] { "fuelCostPerKmMax", "fuelExpectedKmPerLiter", "maintenanceDueSoonDays" }, bad.Order());
    }

    [EmulatorFact]
    public async Task WellFormedDocument_ReadsLikeConvertTo()
    {
        DocumentSnapshot snapshot = await ConfigAsync(new()
        {
            ["defaultBoundaryRate"] = 950.5,
            ["fuelExpectedKmPerLiter"] = new Dictionary<string, object> { ["TAXI_001"] = 11.5 },
            ["oilChangeMileageIntervalKm"] = new Dictionary<string, object> { ["TAXI_002"] = 5000 },
            ["maintenanceDueSoonDays"] = 0,
        });

        SystemConfig read = SystemConfigReader.Read(snapshot);
        SystemConfig converted = snapshot.ConvertTo<SystemConfig>();

        Assert.Equal(converted.DefaultBoundaryRate, read.DefaultBoundaryRate);
        Assert.Equal(converted.FuelExpectedKmPerLiter, read.FuelExpectedKmPerLiter);
        Assert.Equal(converted.OilChangeMileageIntervalKm, read.OilChangeMileageIntervalKm);
        Assert.Equal(0, read.MaintenanceDueSoonDays);
        Assert.Equal(converted.IdleThresholdMinutes, read.IdleThresholdMinutes);
    }

    [EmulatorFact]
    public async Task MissingDocument_AllDefaults()
    {
        SystemConfig config = SystemConfigReader.Read(await ConfigAsync(null));

        Assert.Equal(0, config.DefaultBoundaryRate);
        Assert.Equal(new SystemConfig().OilChangeIntervalDays, config.OilChangeIntervalDays);
    }
}

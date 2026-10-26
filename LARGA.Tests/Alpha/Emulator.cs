using Google.Cloud.Firestore;
using LARGA.SharedCore.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace LARGA.Tests.Alpha;

/// <summary>Local Firestore emulator only (demo- project, can never reach larga-blmtaxi).
/// Start it with: cd tests/rules && ./node_modules/.bin/firebase emulators:start --only firestore --project demo-larga-tests
/// then run with LARGA_EMULATOR=1.</summary>
public static class Emu
{
    public const string Host = "127.0.0.1:8080";
    public const string ProjectId = "demo-larga-tests";

    public static bool Enabled => Environment.GetEnvironmentVariable("LARGA_EMULATOR") == "1";

    public static FirestoreDb Db()
    {
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", Host);
        return new FirestoreDbBuilder { ProjectId = ProjectId, EmulatorDetection = Google.Api.Gax.EmulatorDetection.EmulatorOnly }.Build();
    }

    public static async Task ClearAsync()
    {
        using var http = new HttpClient();
        var response = await http.DeleteAsync($"http://{Host}/emulator/v1/projects/{ProjectId}/databases/(default)/documents");
        response.EnsureSuccessStatusCode();
    }

    public static GarageService Garage(FirestoreDb db)
    {
        var lazy = new Lazy<FirestoreDb>(db);
        var alerts = new AlertService(lazy, NullLogger<AlertService>.Instance);
        return new GarageService(lazy, NullLogger<GarageService>.Instance,
            new InventoryAuditService(lazy, NullLogger<InventoryAuditService>.Instance, alerts));
    }

    public static FleetReportingService Fleet(FirestoreDb db)
    {
        var lazy = new Lazy<FirestoreDb>(db);
        return new FleetReportingService(lazy, NullLogger<FleetReportingService>.Instance,
            new CollectionCache(lazy, NullLogger<CollectionCache>.Instance));
    }

    public static AlertService Alerts(FirestoreDb db) =>
        new(new Lazy<FirestoreDb>(db), NullLogger<AlertService>.Instance);

    public static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

public sealed class EmulatorFactAttribute : FactAttribute
{
    public EmulatorFactAttribute()
    {
        if (!Emu.Enabled)
        {
            Skip = "Needs the local Firestore emulator (LARGA_EMULATOR=1).";
        }
    }
}

[CollectionDefinition("emulator", DisableParallelization = true)]
public class EmulatorCollection { }

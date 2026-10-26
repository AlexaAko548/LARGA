using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Models.FinancialLedger;
using LARGA.SharedCore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace LARGA.Tests.Live;

/// <summary>READ-ONLY look at the live larga-blmtaxi ledger for the M2 re-run (no writes anywhere): the boundary rate
/// as each reader sees it, and the web's Master Debt Ledger rows next to each driver's History total.
/// Run with LARGA_LIVE_READONLY=1 and LARGA_CREDENTIALS=path to the admin key.</summary>
public class LiveLedgerReadOnlyProbe
{
    private readonly ITestOutputHelper _out;
    public LiveLedgerReadOnlyProbe(ITestOutputHelper output) => _out = output;

    private static string? Credentials => Environment.GetEnvironmentVariable("LARGA_CREDENTIALS");
    private static bool Enabled => Environment.GetEnvironmentVariable("LARGA_LIVE_READONLY") == "1" && File.Exists(Credentials);

    private static FirestoreDb Db()
    {
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", null);
        return new FirestoreDbBuilder { ProjectId = "larga-blmtaxi", CredentialsPath = Credentials, EmulatorDetection = Google.Api.Gax.EmulatorDetection.ProductionOnly }.Build();
    }

    [Fact]
    public async Task ProbeLedger()
    {
        if (!Enabled) { _out.WriteLine("skipped"); return; }
        FirestoreDb db = Db();

        DocumentSnapshot global = await db.Collection("system_configs").Document("global").GetSnapshotAsync();
        foreach ((string key, object value) in global.ToDictionary().OrderBy(kv => kv.Key))
        {
            string shown = value is string or double or long or bool ? value.ToString()! : value?.GetType().Name ?? "null";
            _out.WriteLine($"config {key} = {value?.GetType().Name} {shown}");
        }
        try { _out.WriteLine($"ConvertTo<SystemConfig> ok: defaultBoundaryRate={global.ConvertTo<SystemConfig>().DefaultBoundaryRate}"); }
        catch (Exception ex) { _out.WriteLine($"ConvertTo<SystemConfig> THROWS: {ex.GetType().Name}: {ex.Message}"); }

        var lazy = new Lazy<FirestoreDb>(db);
        var ledger = new FinancialLedgerService(lazy, new Lazy<PhotoStorageTarget>(() => throw new InvalidOperationException("read-only")),
            NullLogger<FinancialLedgerService>.Instance, new CollectionCache(lazy, NullLogger<CollectionCache>.Instance));

        DebtLedgerSnapshot snap = await ledger.GetDebtLedgerAsync();
        _out.WriteLine($"MASTER withDebt={snap.DriversWithDebtCount} total={snap.TotalOutstandingDebt:N2} debtFree={snap.DebtFreeDriverCount}");
        foreach (DebtLedgerRow r in snap.Rows)
        {
            DriverLedgerHistory h = await ledger.GetDriverHistoryAsync(r.DriverId);
            decimal replay = h.Transactions.FirstOrDefault()?.RunningDebt ?? 0;
            _out.WriteLine($"ROW {r.DriverId} '{r.DriverName}' taxi={r.TaxiId} debt={r.TotalDebt:N2} credit={r.AdvanceCredit:N2} unpaidShifts={r.UnpaidCount} days={r.DaysUnpaid} flagged={r.IsDebtFlagged} " +
                           $"| history current={h.CurrentOutstandingDebt:N2} newestRunning={replay:N2}{(replay != r.TotalDebt || h.CurrentOutstandingDebt != r.TotalDebt ? "  <-- MISMATCH" : "")}");
        }

        foreach (DocumentSnapshot u in (await db.Collection("users").GetSnapshotAsync()).Documents)
        {
            var d = u.ToDictionary();
            _out.WriteLine($"USER {u.Id} role={d.GetValueOrDefault("role")} name={d.GetValueOrDefault("fullName")} email={d.GetValueOrDefault("email")} debtFlaggedSince={d.GetValueOrDefault("debtFlaggedSince")}");
        }

        DailySettlementSnapshot today = await ledger.GetDailySettlementAsync(DateTime.UtcNow);
        _out.WriteLine($"TODAY {today.Date:yyyy-MM-dd} expected={today.ExpectedCollection:N2} collected={today.CollectedSoFar:N2} cleared={today.ClearedCount} partial={today.PartialCount} waiting={today.WaitingCount}");
        foreach (SettlementRow r in today.Rows)
        {
            _out.WriteLine($"  {r.Status} {r.DriverName} {r.TaxiId} end={r.ShiftEnd:u} expected={r.ExpectedTotal:N2} paid={r.AmountPaid:N2} credit={r.CreditApplied:N2}");
        }
    }
}

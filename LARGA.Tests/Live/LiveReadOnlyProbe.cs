using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using Xunit.Abstractions;

namespace LARGA.Tests.Live;

/// <summary>READ-ONLY look at the live larga-blmtaxi project for the M4/M6 re-run (no writes anywhere).
/// Run with LARGA_LIVE_READONLY=1 and LARGA_CREDENTIALS=path to the admin key.</summary>
public class LiveReadOnlyProbe
{
    private readonly ITestOutputHelper _out;
    public LiveReadOnlyProbe(ITestOutputHelper output) => _out = output;

    private const string ProjectId = "larga-blmtaxi";
    private static string? Credentials => Environment.GetEnvironmentVariable("LARGA_CREDENTIALS");
    private static bool Enabled => Environment.GetEnvironmentVariable("LARGA_LIVE_READONLY") == "1" && File.Exists(Credentials);

    private static FirestoreDb Db()
    {
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", null);
        return new FirestoreDbBuilder { ProjectId = ProjectId, CredentialsPath = Credentials, EmulatorDetection = Google.Api.Gax.EmulatorDetection.ProductionOnly }.Build();
    }

    private static string Describe(object? v) => v switch
    {
        null => "null",
        string s => $"string \"{s}\"",
        IDictionary<string, object> d => $"map {{{string.Join(", ", d.Select(kv => $"{kv.Key}: {kv.Value}"))}}}",
        Timestamp t => $"timestamp {t.ToDateTime():u}",
        _ => $"{v.GetType().Name} {v}",
    };

    [Fact]
    public async Task ProbeShifts()
    {
        if (!Enabled) { _out.WriteLine("skipped"); return; }
        FirestoreDb db = Db();
        var g = (await db.Collection("system_configs").Document("global").GetSnapshotAsync()).ToDictionary();
        _out.WriteLine($"garage {g.GetValueOrDefault("garageLatitude")},{g.GetValueOrDefault("garageLongitude")} radius={g.GetValueOrDefault("garageRadiusMeters")} testClockPh={g.GetValueOrDefault("testClockPh")}");
        foreach (DocumentSnapshot t in (await db.Collection("taxis").GetSnapshotAsync()).Documents)
        {
            var d = t.ToDictionary();
            _out.WriteLine($"taxi {t.Id}: plate={d.GetValueOrDefault("plateNumber")} status={d.GetValueOrDefault("status")} currentMileage={d.GetValueOrDefault("currentMileage")}");
        }
        QuerySnapshot recent = await db.Collection("shifts").OrderByDescending("shiftStart").Limit(8).GetSnapshotAsync();
        foreach (DocumentSnapshot s in recent.Documents)
        {
            var d = s.ToDictionary();
            _out.WriteLine($"shift {s.Id}: taxi={d.GetValueOrDefault("taxiId")} driver={d.GetValueOrDefault("driverId")} status={d.GetValueOrDefault("status")} " +
                           $"start={(d.GetValueOrDefault("shiftStart") as Timestamp?)?.ToDateTime():u} end={(d.GetValueOrDefault("shiftEnd") as Timestamp?)?.ToDateTime():u} " +
                           $"startMileage={d.GetValueOrDefault("startMileage")} endMileage={d.GetValueOrDefault("endMileage")} isOnBreak={d.GetValueOrDefault("isOnBreak")} " +
                           $"distanceKm={d.GetValueOrDefault("distanceKm")} speed={d.GetValueOrDefault("currentSpeed")} lastFix={(d.GetValueOrDefault("currentPositionUpdatedAt") as Timestamp?)?.ToDateTime():u}");
            if (Environment.GetEnvironmentVariable("LARGA_POINTS") == "1" && s == recent.Documents[0])
            {
                var pts = (await db.Collection("gps_telemetry").WhereEqualTo("shiftId", s.Id).GetSnapshotAsync()).Documents
                    .Select(p => p.ToDictionary()).OrderBy(p => ((Timestamp)p["timestamp"]).ToDateTime()).ToList();
                foreach (var p in pts)
                {
                    _out.WriteLine($"  pt {((Timestamp)p["timestamp"]).ToDateTime():HH:mm:ss} lat={p["latitude"]} lon={p["longitude"]} speed={p["speed"]}");
                }
            }
        }
        foreach (DocumentSnapshot a in (await db.Collection("system_alerts").WhereEqualTo("type", "DriverIdle").GetSnapshotAsync()).Documents
                     .OrderByDescending(a => a.GetValue<Timestamp>("timestamp").ToDateTime()).Take(3))
        {
            _out.WriteLine($"idle alert {a.Id}: {a.GetValue<string>("message")} at {a.GetValue<Timestamp>("timestamp").ToDateTime():u}");
        }
    }

    [Fact]
    public async Task Probe()
    {
        if (!Enabled) { _out.WriteLine("skipped"); return; }
        FirestoreDb db = Db();
        DateTime now = DateTime.UtcNow;

        // ---- system_configs/global (GPS-014 root cause, MNT-014/015/016 intervals) ----
        DocumentSnapshot cfg = await db.Collection("system_configs").Document("global").GetSnapshotAsync();
        _out.WriteLine("== system_configs/global ==");
        foreach (string key in new[] { "idleThresholdMinutes", "fuelExpectedKmPerLiter", "maintenanceDueSoonDays", "maintenanceWeeklyIntervalDays",
                     "maintenanceBiweeklyIntervalDays", "maintenanceMonthlyIntervalDays", "oilChangeIntervalDays", "oilChangeMileageIntervalKm" })
        {
            _out.WriteLine($"{key}: {(cfg.TryGetValue(key, out object? v) ? Describe(v) : "(absent)")}");
        }
        try { cfg.ConvertTo<SystemConfig>(); _out.WriteLine("ConvertTo<SystemConfig>: OK"); }
        catch (Exception ex) { _out.WriteLine($"ConvertTo<SystemConfig>: THROWS {ex.GetType().Name}: {ex.Message}"); }

        // ---- GPS-016: TTL policies and stale points ----
        _out.WriteLine("== gps_telemetry TTL ==");
        var credential = GoogleCredential.FromFile(Credentials).CreateScoped("https://www.googleapis.com/auth/datastore");
        string token = await ((ITokenAccess)credential).GetAccessTokenForRequestAsync();
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        foreach (string field in new[] { "timestamp", "expireAt" })
        {
            string url = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/collectionGroups/gps_telemetry/fields/{field}";
            string body = await http.GetStringAsync(url);
            using JsonDocument json = JsonDocument.Parse(body);
            _out.WriteLine($"{field}: ttlConfig = {(json.RootElement.TryGetProperty("ttlConfig", out JsonElement ttl) ? ttl.GetRawText() : "(none)")}");
        }

        CollectionReference gps = db.Collection("gps_telemetry");
        long total = (await gps.Count().GetSnapshotAsync()).Count ?? 0;
        long older7 = (await gps.WhereLessThan("timestamp", now.AddDays(-7)).Count().GetSnapshotAsync()).Count ?? 0;
        long older8 = (await gps.WhereLessThan("timestamp", now.AddDays(-8)).Count().GetSnapshotAsync()).Count ?? 0;
        _out.WriteLine($"gps_telemetry total={total}, timestamp older than 7 d={older7}, older than 8 d={older8}");
        QuerySnapshot oldest = await gps.OrderBy("timestamp").Limit(1).GetSnapshotAsync();
        foreach (DocumentSnapshot d in oldest.Documents)
        {
            _out.WriteLine($"oldest point: {d.GetValue<Timestamp>("timestamp").ToDateTime():u} expireAt={(d.TryGetValue("expireAt", out object? e) ? Describe(e) : "(absent)")}");
        }

        // ---- GPS-001 / GPS-002 / GPS-009: real shifts and their points ----
        QuerySnapshot shifts = await db.Collection("shifts").WhereGreaterThanOrEqualTo("shiftStart", now.AddDays(-14)).GetSnapshotAsync();
        _out.WriteLine($"== shifts started in the last 14 days: {shifts.Count} ==");
        int afterEndTotal = 0, completedChecked = 0;
        foreach (DocumentSnapshot s in shifts.Documents.OrderByDescending(d => d.GetValue<Timestamp>("shiftStart").ToDateTime()))
        {
            DateTime start = s.GetValue<Timestamp>("shiftStart").ToDateTime();
            string status = s.TryGetValue("status", out string? st) ? st : "?";
            DateTime? end = s.TryGetValue("shiftEnd", out Timestamp te) ? te.ToDateTime() : null;
            List<DocumentSnapshot> points = (await gps.WhereEqualTo("shiftId", s.Id).GetSnapshotAsync()).Documents
                .OrderBy(p => p.GetValue<Timestamp>("timestamp").ToDateTime()).ToList();
            if (points.Count == 0) { _out.WriteLine($"{s.Id} {status} start={start:u} end={end:u} points=0"); continue; }

            List<DateTime> times = points.Select(p => p.GetValue<Timestamp>("timestamp").ToDateTime()).ToList();
            List<double> gaps = times.Zip(times.Skip(1), (a, b) => (b - a).TotalSeconds).ToList();
            double median = gaps.Count == 0 ? 0 : gaps.OrderBy(g => g).ElementAt(gaps.Count / 2);
            var fieldSets = points.Select(p => string.Join(",", p.ToDictionary().Keys.OrderBy(k => k))).Distinct().ToList();
            int afterEnd = end is DateTime e2 ? times.Count(t => t > e2) : 0;
            if (status == "Completed" && end is not null) { completedChecked++; afterEndTotal += afterEnd; }
            int bigGaps = gaps.Count(g => g > 90);
            _out.WriteLine($"{s.Id} {status} start={start:u} end={end:u} points={points.Count} firstPointAfterStart={(times[0] - start).TotalSeconds:0}s " +
                           $"medianGap={median:0}s gapsOver90s={bigGaps} pointsAfterShiftEnd={afterEnd} fields=[{string.Join(" | ", fieldSets)}] " +
                           $"sameDriver={points.All(p => p.GetValue<string>("driverId") == s.GetValue<string>("driverId"))}");
        }
        _out.WriteLine($"completed shifts checked={completedChecked}, total points after shiftEnd={afterEndTotal}");

        // ---- MNT-002: newest driver defect reports as stored ----
        QuerySnapshot reports = await db.Collection("maintenance_logs").WhereGreaterThanOrEqualTo("dateLogged", now.AddDays(-30)).GetSnapshotAsync();
        _out.WriteLine($"== maintenance_logs last 30 d: {reports.Count} ==");
        foreach (DocumentSnapshot r in reports.Documents.Where(r => r.TryGetValue("reportedByDriverId", out string? drv) && !string.IsNullOrEmpty(drv))
                     .OrderByDescending(r => r.GetValue<Timestamp>("dateLogged").ToDateTime()).Take(6))
        {
            var d = r.ToDictionary();
            _out.WriteLine($"{r.Id}: status={d.GetValueOrDefault("status")} priority={d.GetValueOrDefault("priorityLevel")} " +
                           $"photo={(string.IsNullOrEmpty(d.GetValueOrDefault("supportingPhotoUrl") as string) ? "(empty)" : "set")} taxiId={d.GetValueOrDefault("taxiId")} title={d.GetValueOrDefault("issueTitle")}");
        }

        // ---- routine checks currently waiting ----
        QuerySnapshot checks = await db.Collection("routine_checks").GetSnapshotAsync();
        _out.WriteLine($"== routine_checks: {checks.Count} ==");
        foreach (DocumentSnapshot c in checks.Documents.Take(30))
        {
            var d = c.ToDictionary();
            _out.WriteLine($"{c.Id}: due={Describe(d.GetValueOrDefault("dueDate"))} dueMileage={d.GetValueOrDefault("dueMileage")}");
        }
    }
}

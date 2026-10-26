using System.Reflection;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Models.Alerts;
using LARGA.SharedCore.Models.Dashboard;
using LARGA.SharedCore.Services;
using Microsoft.Maui.Devices.Sensors;

namespace LARGA.Tests.Alpha;

/// <summary>M6 GPS Tracking (GPS-xxx) - idle rule, distance filter, idle alerts.</summary>
public class GpsRuleTests
{
    private static bool IsMoving(ShiftLog shift, double thresholdMinutes, DateTime now) =>
        (bool)typeof(FleetReportingService).GetMethod("IsMoving", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { shift, thresholdMinutes, now })!;

    private static ShiftLog Shift(DateTime now, double startedMinutesAgo, double? speed, double? lastFixMinutesAgo) => new()
    {
        ShiftId = "s1", Status = "Active",
        ShiftStart = now.AddMinutes(-startedMinutesAgo),
        CurrentSpeed = speed,
        CurrentPositionUpdatedAt = lastFixMinutesAgo is double m ? now.AddMinutes(-m) : null,
    };

    // ---------- GPS-014 ----------

    [Fact]
    public void Gps014_JustClockedIn_NotMovingYet_IsNotIdle()
    {
        DateTime now = DateTime.UtcNow;
        Assert.True(IsMoving(Shift(now, 1, 0, 0.5), 15, now));
        Assert.True(IsMoving(Shift(now, 14.9, null, null), 15, now));
    }

    [Fact]
    public void Gps014_Parked16Minutes_IsIdle()
    {
        DateTime now = DateTime.UtcNow;
        // Phone keeps writing every 30 s with speed 0 while parked.
        Assert.False(IsMoving(Shift(now, 16, 0, 0.5), 15, now));
        Assert.False(IsMoving(Shift(now, 120, 0, 0.5), 15, now));
    }

    [Fact]
    public void Gps014_DrivingNormally_IsNotIdle()
    {
        DateTime now = DateTime.UtcNow;
        Assert.True(IsMoving(Shift(now, 60, 32, 0.5), 15, now));
    }

    [Fact]
    public void Gps014_StoppedForOneMinuteMidShift_IsNotIdleYet()
    {
        // 60 min into the shift, was driving until a minute ago, now at a red light: the latest
        // point has speed 0. It has not been still for the 15-minute threshold.
        DateTime now = DateTime.UtcNow;
        Assert.True(IsMoving(Shift(now, 60, 0, 0.5), 15, now));
    }

    // ---------- GPS-010: jitter does not add distance ----------

    private static readonly Type TrackerType = typeof(GpsTelemetryService).GetNestedType("DistanceTracker", BindingFlags.NonPublic)!;

    private sealed class Tracker
    {
        private readonly object _inner = Activator.CreateInstance(TrackerType, nonPublic: true)!;
        public bool Add(Location fix) => (bool)TrackerType.GetMethod("Add")!.Invoke(_inner, new object[] { fix })!;
        public double Meters => (double)TrackerType.GetProperty("Meters")!.GetValue(_inner)!;
    }

    private static Location Fix(double lat, double lon, double accuracy, int second) =>
        new(lat, lon, new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero).AddSeconds(second)) { Accuracy = accuracy };

    // ~1.11 m per 0.00001 deg of latitude.
    private const double Base = 10.25;

    [Fact]
    public void Gps010_ParkedPhoneJitterUnder20m_AddsNothing()
    {
        var t = new Tracker();
        var rnd = new Random(7);
        for (int i = 0; i < 20; i++) // 10 minutes of 30 s fixes, wandering up to ~8 m
        {
            t.Add(Fix(Base + rnd.NextDouble() * 0.00007, 123.9 + rnd.NextDouble() * 0.00007, 12, i * 30));
        }
        Assert.Equal(0, t.Meters);
    }

    [Fact]
    public void Gps010_PoorAccuracyFixOver50m_Ignored()
    {
        var t = new Tracker();
        t.Add(Fix(Base, 123.9, 10, 0));
        Assert.False(t.Add(Fix(Base + 0.001, 123.9, 65, 30))); // ~111 m hop, but accuracy 65 m
        Assert.Equal(0, t.Meters);
    }

    [Fact]
    public void Gps010_RealMovementOver20m_Counts()
    {
        var t = new Tracker();
        t.Add(Fix(Base, 123.9, 10, 0));
        Assert.True(t.Add(Fix(Base + 0.0003, 123.9, 10, 30))); // ~33 m
        Assert.InRange(t.Meters, 30, 36);
    }

    [Fact]
    public void Gps010_SlowCreep_AddsUpOncePast20m()
    {
        var t = new Tracker();
        t.Add(Fix(Base, 123.9, 10, 0));
        Assert.False(t.Add(Fix(Base + 0.0001, 123.9, 10, 30))); // ~11 m from anchor
        Assert.True(t.Add(Fix(Base + 0.0002, 123.9, 10, 60)));  // ~22 m from the same anchor
        Assert.InRange(t.Meters, 20, 24);
    }
}

[Collection("emulator")]
public class GpsEmulatorTests
{
    private static async Task<FirestoreDb> SeedAsync(double startedMinutesAgo, double speed, double? idleThreshold, bool badFuelField)
    {
        await Emu.ClearAsync();
        FirestoreDb db = Emu.Db();
        DateTime now = DateTime.UtcNow;
        var config = new Dictionary<string, object>();
        if (idleThreshold is double t) config["idleThresholdMinutes"] = t;
        if (badFuelField) config["fuelExpectedKmPerLiter"] = ""; // the live data problem from GPS-014
        if (config.Count > 0) await db.Collection("system_configs").Document("global").SetAsync(config);

        await db.Collection("taxis").Document("TAXI_004").SetAsync(new Dictionary<string, object> { ["taxiId"] = "TAXI_004", ["status"] = "Active Unit", ["plateNumber"] = "GAB 4004" });
        await db.Collection("users").Document("drv4").SetAsync(new Dictionary<string, object> { ["fullName"] = "Dan Four", ["role"] = "Driver" });
        await db.Collection("shifts").Document("shift4").SetAsync(new Dictionary<string, object>
        {
            ["shiftId"] = "shift4", ["driverId"] = "drv4", ["taxiId"] = "TAXI_004", ["status"] = "Active", ["isOnBreak"] = false,
            ["shiftStart"] = now.AddMinutes(-startedMinutesAgo), ["startMileage"] = 1000,
            ["currentSpeed"] = speed, ["currentPositionUpdatedAt"] = now.AddSeconds(-30),
            ["currentLatitude"] = 10.25, ["currentLongitude"] = 123.9,
        });
        return db;
    }

    [EmulatorFact]
    public async Task Gps014_Parked16Min_WebShowsIdle_AndOneAlertPerShift()
    {
        FirestoreDb db = await SeedAsync(startedMinutesAgo: 16, speed: 0, idleThreshold: null, badFuelField: false);
        FleetReportingService fleet = Emu.Fleet(db);
        AlertService alerts = Emu.Alerts(db);

        IdleDriverInfo idle = Assert.Single(await fleet.GetIdleDriversAsync());
        Assert.Equal(15, idle.IdleThresholdMinutes);

        DashboardSnapshot dash = await fleet.GetDashboardSnapshotAsync();
        Assert.Equal(FleetUnitStatus.IdleStatus, Assert.Single(dash.FleetStatus.Units, u => u.TaxiId == "TAXI_004").Status);

        Assert.True(await alerts.CreateIdleAlertIfNeededAsync(idle));
        Assert.False(await alerts.CreateIdleAlertIfNeededAsync(idle)); // next 3-minute tick
        var all = (await db.Collection("system_alerts").GetSnapshotAsync()).Documents;
        DocumentSnapshot alert = Assert.Single(all);
        Assert.Equal("shift4_IDLE", alert.Id);
        Assert.Equal("DriverIdle", alert.GetValue<string>("type"));
    }

    [EmulatorFact]
    public async Task Gps014_OneMinuteAfterClockIn_NoIdleAlert()
    {
        FirestoreDb db = await SeedAsync(startedMinutesAgo: 1, speed: 0, idleThreshold: null, badFuelField: false);
        Assert.Empty(await Emu.Fleet(db).GetIdleDriversAsync());
    }

    [EmulatorFact]
    public async Task Gps014_ConfiguredThreshold10_UsedDespiteBadFuelField()
    {
        FirestoreDb db = await SeedAsync(startedMinutesAgo: 11, speed: 0, idleThreshold: 10, badFuelField: true);
        IdleDriverInfo idle = Assert.Single(await Emu.Fleet(db).GetIdleDriversAsync());
        Assert.Equal(10, idle.IdleThresholdMinutes);

        Assert.True(await Emu.Alerts(db).CreateIdleAlertIfNeededAsync(idle));
        DocumentSnapshot alert = await db.Collection("system_alerts").Document("shift4_IDLE").GetSnapshotAsync();
        Assert.Equal("Dan Four (Unit 04) has been idle for 10 minutes.", alert.GetValue<string>("message"));
    }

    // Isolates the cause: a clean config doc with only idleThresholdMinutes = 10.
    [EmulatorFact]
    public async Task Diag_ConfiguredThreshold10_CleanConfig()
    {
        FirestoreDb db = await SeedAsync(startedMinutesAgo: 11, speed: 0, idleThreshold: 10, badFuelField: false);
        IdleDriverInfo idle = Assert.Single(await Emu.Fleet(db).GetIdleDriversAsync());
        Assert.Equal(10, idle.IdleThresholdMinutes);
    }

    // Same shape as FleetReportingService's IdleThresholdConfigProxy (no [FirestoreData]).
    private sealed class ProxyWithoutFirestoreData
    {
        [FirestoreProperty("idleThresholdMinutes")]
        public double IdleThresholdMinutes { get; set; }
    }

    [EmulatorFact]
    public async Task Diag_ProxyWithoutFirestoreDataAttribute_ConvertTo()
    {
        FirestoreDb db = await SeedAsync(startedMinutesAgo: 11, speed: 0, idleThreshold: 10, badFuelField: false);
        DocumentSnapshot cfg = await db.Collection("system_configs").Document("global").GetSnapshotAsync();
        Exception? ex = Record.Exception(() => cfg.ConvertTo<ProxyWithoutFirestoreData>());
        Assert.Null(ex);
    }
}

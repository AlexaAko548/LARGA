using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.SharedCore;
using LARGA.SharedCore.Models.Alerts;
using LARGA.SharedCore.Models.Dashboard;
using LARGA.Shared.Models.Entities;
using Microsoft.Extensions.Logging;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Builds the data the ManagerWeb Executive Dashboard renders, by reading Firestore
/// server-side with an admin (service account) FirestoreDb - see docs/ERD.md
/// "Live fleet status (computed, not stored)" for the per-taxi status precedence this
/// implements.
///
/// Read-cost design: `shifts` and `boundary_payments` are written roughly once per taxi
/// per day, so their total history grows without bound the longer the fleet actually
/// runs - fetching either collection in full on every dashboard load would eventually
/// cost thousands of reads per page view (see docs/ERD.md "Dashboard read-cost notes").
/// Nothing this dashboard reports needs more than "currently active" shifts or the last
/// two weeks of history, so those two collections are queried narrowly (WhereEqualTo /
/// WhereGreaterThanOrEqualTo) instead of fetched whole. `taxis`/`users` stay full fetches
/// since they're bounded by fleet/team size, not time. `maintenance_logs`/
/// `emergency_alerts` also stay full fetches: they're incident-driven so their volume
/// grows far slower, and a query filter like WhereEqualTo("dateResolved", null) would
/// only match documents where that field is explicitly null - not documents where it's
/// simply missing, a real possibility given some live docs were hand-entered.
/// </summary>
public class FleetReportingService
{
    private const double DefaultIdleThresholdMinutes = 15;

    // Lazy: credential/connection failures should surface when a method below actually
    // runs (where callers like Dashboard.razor already catch them), not at DI-construction
    // time - see the comment on this service's registration in ManagerWeb's Program.cs.
    private readonly Lazy<FirestoreDb> _dbLazy;
    private readonly ILogger<FleetReportingService> _logger;
    private readonly CollectionCache _cache;

    private FirestoreDb Db => _dbLazy.Value;

    public FleetReportingService(Lazy<FirestoreDb> dbLazy, ILogger<FleetReportingService> logger, CollectionCache cache)
    {
        _dbLazy = dbLazy;
        _logger = logger;
        _cache = cache;
    }

    /// <summary>
    /// The Executive Dashboard. Its whole-collection reads (taxis, users, maintenance, SOS alerts, fuel logs)
    /// come from CollectionCache's listeners (PERFORMANCE_REPORT.md P4): the same documents, so the same
    /// figures, without re-reading them on every visit. The dashboard only reads, so it can't miss a write of
    /// its own. The date-window queries (active / recent shifts, recent payments) stay direct.
    /// </summary>
    public async Task<DashboardSnapshot> GetDashboardSnapshotAsync()
    {
        DateTime now = DateTime.UtcNow;
        DateTime twoWeeksAgo = now.AddDays(-14);

        // The reads don't depend on each other, so they run together: the dashboard waits for the
        // slowest one instead of the sum of all nine (PERFORMANCE_REPORT.md P3). Same queries as before.

        // Bounded by fleet/team size, not history - safe to fetch in full forever.
        Task<List<TaxiUnit>> taxisTask = GetAllAsync<TaxiUnit>("taxis", fromCache: true);
        Task<List<UserProfile>> usersTask = GetAllAsync<UserProfile>("users", fromCache: true);

        // Incident-driven, low volume even over years of real operation - see the class
        // comment above for why these stay full fetches instead of a null-filter query.
        Task<List<MaintenanceRecord>> maintenanceTask = GetAllAsync<MaintenanceRecord>("maintenance_logs", fromCache: true);
        Task<List<EmergencyAlert>> alertsTask = GetAllAsync<EmergencyAlert>("emergency_alerts", fromCache: true);

        // High-frequency, unbounded-by-history - query narrowly instead of fetching all.
        Task<List<ShiftLog>> activeShiftsTask = GetWhereEqualAsync<ShiftLog>("shifts", "status", "Active");
        Task<List<ShiftLog>> recentShiftsTask = GetSinceAsync<ShiftLog>("shifts", "shiftStart", twoWeeksAgo);
        Task<List<BoundaryPayment>> recentPaymentsTask = GetSinceAsync<BoundaryPayment>("boundary_payments", "timestamp", twoWeeksAgo);
        // Every receipt, like the Fuel Verification page - so the Dashboard's fuel chart always
        // matches its Pending / Verified / Flagged totals. Not windowed by receiptTimestamp: that's
        // the date printed on the receipt (read by OCR), which can be weeks old by the time it's
        // verified - or misread entirely - so a 14-day window left most verified receipts out.
        // One document per refuel, so this stays small.
        Task<List<FuelLog>> fuelLogsTask = GetAllAsync<FuelLog>("fuel_logs", fromCache: true);

        Task<double> idleThresholdTask = GetIdleThresholdMinutesAsync();

        await Task.WhenAll(taxisTask, usersTask, maintenanceTask, alertsTask, activeShiftsTask, recentShiftsTask,
            recentPaymentsTask, fuelLogsTask, idleThresholdTask);

        List<TaxiUnit> taxis = taxisTask.Result;
        List<UserProfile> drivers = usersTask.Result
            .Where(u => string.Equals(u.Role, "Driver", StringComparison.OrdinalIgnoreCase))
            .ToList();
        List<MaintenanceRecord> maintenance = maintenanceTask.Result;
        List<EmergencyAlert> alerts = alertsTask.Result;
        List<ShiftLog> activeShifts = activeShiftsTask.Result;
        List<ShiftLog> recentShifts = recentShiftsTask.Result;
        List<BoundaryPayment> recentPayments = recentPaymentsTask.Result;
        List<FuelLog> fuelLogs = fuelLogsTask.Result;
        double idleThresholdMinutes = idleThresholdTask.Result;

        var snapshot = new DashboardSnapshot
        {
            WeeklyRevenue = BuildWeeklyRevenue(recentPayments, now),
            ActiveDrivers = BuildActiveDrivers(activeShifts, drivers),
            FleetMileage = BuildFleetMileage(recentShifts, now),
            OpenIncidents = BuildOpenIncidents(maintenance, alerts, now),
            FleetStatus = await BuildFleetStatusAsync(taxis, drivers, activeShifts, recentShifts, alerts, maintenance, idleThresholdMinutes, now),
            // Top Driver Standings is now a "last 14 days" leaderboard rather than an
            // all-time one - a deliberate side effect of no longer fetching full shift/
            // payment history. Arguably more useful anyway (recent performance vs.
            // lifetime), but flagging the semantic change explicitly.
            TopDrivers = await BuildTopDriversAsync(drivers, maintenance, alerts, PhilippineTime.Now.Date.AddDays(1 - StandingsWindowDays), now),
            BoundaryCollections = BuildBoundaryCollections(recentPayments),
            FuelVerification = BuildFuelVerification(fuelLogs),
            FleetMileageByTaxi = BuildFleetMileageByTaxi(recentShifts),
            MaintenanceExpenses = BuildMaintenanceExpenses(maintenance),
            Utilization = BuildUtilization(taxis, activeShifts, recentShifts, OpenWorkOrders(maintenance), now),
        };

        return snapshot;
    }

    // ---------------------------------------------------------------------
    // Fleet utilization KPIs (LAR-84)
    // ---------------------------------------------------------------------

    private static FleetUtilization BuildUtilization(List<TaxiUnit> taxis, List<ShiftLog> activeShifts, List<ShiftLog> recentShifts, List<MaintenanceRecord> openJobs, DateTime now)
    {
        List<TaxiUnit> operable = taxis
            .Where(t => !string.Equals(t.Status, "Decommissioned", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.TaxiId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        HashSet<string> operableIds = operable.Select(t => t.TaxiId).ToHashSet();

        // The last 7 Philippine calendar days, today included.
        DateTime windowStart = now.ToPhilippineTime().Date.AddDays(1 - FleetUtilization.WindowDays) - PhilippineTime.Offset;
        List<ShiftLog> windowShifts = recentShifts
            .Where(s => s.ShiftStart is DateTime start && start >= windowStart && start <= now && operableIds.Contains(s.TaxiId))
            .ToList();

        Dictionary<string, int> daysByUnit = windowShifts
            .GroupBy(s => s.TaxiId)
            .ToDictionary(g => g.Key, g => g.Select(s => s.ShiftStart!.Value.ToPhilippineTime().Date).Distinct().Count());

        List<ShiftLog> completed = windowShifts
            .Where(s => s.ShiftEnd is DateTime end && end > s.ShiftStart!.Value)
            .ToList();
        List<ShiftLog> withMileage = completed.Where(s => ShiftKm(s) > 0).ToList();

        return new FleetUtilization
        {
            OperableUnits = operable.Count,
            UnitsOnRoadNow = activeShifts.Where(s => operableIds.Contains(s.TaxiId)).Select(s => s.TaxiId).Distinct().Count(),
            UnitsUnderMaintenance = operable.Count(t => TaxiStatusRules.IsUnderMaintenance(t.Status)
                || (!activeShifts.Any(s => s.TaxiId == t.TaxiId) && openJobs.Any(j => j.TaxiId == t.TaxiId))),
            UnitDaysWorked = daysByUnit.Values.Sum(),
            ShiftsCompleted = completed.Count,
            AverageShiftHours = completed.Count == 0 ? 0 : completed.Average(s => (s.ShiftEnd!.Value - s.ShiftStart!.Value).TotalHours),
            AverageKmPerShift = withMileage.Count == 0 ? 0 : withMileage.Average(s => (double)ShiftKm(s)),
            DaysWorkedByUnit = operable
                .Select(t => new ChartPoint { Label = FormatUnitLabel(t.TaxiId), Value = daysByUnit.GetValueOrDefault(t.TaxiId) })
                .ToList(),
        };
    }

    // ---------------------------------------------------------------------
    // Stat cards
    // ---------------------------------------------------------------------

    private static StatCard BuildWeeklyRevenue(List<BoundaryPayment> payments, DateTime now)
    {
        DateTime weekAgo = now.AddDays(-7);
        DateTime twoWeeksAgo = now.AddDays(-14);

        decimal thisWeek = payments.Where(p => p.Timestamp >= weekAgo && p.Timestamp <= now).Sum(p => p.AmountPaid);
        decimal lastWeek = payments.Where(p => p.Timestamp >= twoWeeksAgo && p.Timestamp < weekAgo).Sum(p => p.AmountPaid);

        return new StatCard
        {
            Value = thisWeek,
            TrendPercent = PercentChange(thisWeek, lastWeek),
        };
    }

    private static ActiveDriversStat BuildActiveDrivers(List<ShiftLog> shifts, List<UserProfile> drivers)
    {
        int activeCount = shifts
            .Where(s => s.Status == "Active")
            .Select(s => s.DriverId)
            .Distinct()
            .Count();

        return new ActiveDriversStat
        {
            ActiveCount = activeCount,
            TotalDrivers = drivers.Count,
        };
    }

    private static StatCard BuildFleetMileage(List<ShiftLog> shifts, DateTime now)
    {
        DateTime weekAgo = now.AddDays(-7);
        DateTime twoWeeksAgo = now.AddDays(-14);

        decimal thisWeek = SumMileage(shifts.Where(s => (s.ShiftStart ?? DateTime.MinValue) >= weekAgo && (s.ShiftStart ?? DateTime.MinValue) <= now));
        decimal lastWeek = SumMileage(shifts.Where(s => (s.ShiftStart ?? DateTime.MinValue) >= twoWeeksAgo && (s.ShiftStart ?? DateTime.MinValue) < weekAgo));

        return new StatCard
        {
            Value = thisWeek,
            TrendPercent = PercentChange(thisWeek, lastWeek),
        };
    }

    // A taxi can't cover more than this in one shift - a larger gap means a mistyped or
    // missing odometer reading (e.g. a start of 0), so that shift's distance is left out.
    private const int MaxPlausibleShiftKm = 1000;

    private static int ShiftKm(ShiftLog s) =>
        s.EndMileage > s.StartMileage && s.EndMileage - s.StartMileage <= MaxPlausibleShiftKm ? s.EndMileage - s.StartMileage : 0;

    private static decimal SumMileage(IEnumerable<ShiftLog> shifts) => shifts.Sum(ShiftKm);

    private static StatCard BuildOpenIncidents(List<MaintenanceRecord> maintenance, List<EmergencyAlert> alerts, DateTime now)
    {
        int currentlyOpen = maintenance.Count(m => m.DateResolved == null) + alerts.Count(a => !a.IsResolved);

        // Approximation: we don't keep a historical snapshot of "open incidents as of N days
        // ago", so the trend compares incidents *opened* in the last 7 days vs. the 7 days
        // before that (using DateLogged/Timestamp as a proxy) rather than a true point-in-time
        // open count.
        DateTime weekAgo = now.AddDays(-7);
        DateTime twoWeeksAgo = now.AddDays(-14);

        int openedLast7 = maintenance.Count(m => m.DateLogged >= weekAgo) + alerts.Count(a => a.Timestamp >= weekAgo);
        int openedPrior7 = maintenance.Count(m => m.DateLogged >= twoWeeksAgo && m.DateLogged < weekAgo)
            + alerts.Count(a => a.Timestamp >= twoWeeksAgo && a.Timestamp < weekAgo);

        return new StatCard
        {
            Value = currentlyOpen,
            TrendAbsolute = openedLast7 - openedPrior7,
        };
    }

    private static double? PercentChange(decimal current, decimal previous)
    {
        if (previous == 0)
        {
            return current == 0 ? 0 : null; // undefined % change off a zero baseline
        }

        return (double)((current - previous) / previous * 100);
    }

    // ---------------------------------------------------------------------
    // Live fleet status - see docs/ERD.md "Live fleet status (computed, not stored)"
    // ---------------------------------------------------------------------

    private async Task<FleetStatusCounts> BuildFleetStatusAsync(
        List<TaxiUnit> taxis,
        List<UserProfile> drivers,
        List<ShiftLog> activeShifts,
        List<ShiftLog> recentShifts,
        List<EmergencyAlert> alerts,
        List<MaintenanceRecord> maintenance,
        double idleThresholdMinutes,
        DateTime now)
    {
        var counts = new FleetStatusCounts();
        List<MaintenanceRecord> openJobs = OpenWorkOrders(maintenance);
        Dictionary<string, List<(EmergencyAlert Alert, ShiftLog Shift)>> sosByTaxi =
            await UnresolvedSosByTaxiAsync(alerts, activeShifts.Concat(recentShifts));

        foreach (TaxiUnit taxi in taxis.OrderBy(t => t.TaxiId, StringComparer.OrdinalIgnoreCase))
        {
            ShiftLog? activeShift = activeShifts.FirstOrDefault(s => s.TaxiId == taxi.TaxiId);

            // Every unresolved SOS from a shift on this unit, however old - it stays an
            // emergency until someone ticks Resolved in SOS Dispatch.
            List<(EmergencyAlert Alert, ShiftLog Shift)> sosAlerts = sosByTaxi.GetValueOrDefault(taxi.TaxiId) ?? new();
            (EmergencyAlert Alert, ShiftLog Shift)? latestSos = sosAlerts.Count == 0 ? null : sosAlerts.MaxBy(x => x.Alert.Timestamp);
            EmergencyAlert? sos = latestSos?.Alert;

            ShiftLog? driverShift = activeShift ?? latestSos?.Shift;
            UserProfile? driver = driverShift is null ? null : drivers.FirstOrDefault(d => d.UserId == driverShift.DriverId);
            var unit = new FleetUnitStatus
            {
                TaxiId = taxi.TaxiId,
                PlateNumber = string.IsNullOrWhiteSpace(taxi.PlateNumber) ? null : taxi.PlateNumber,
                DriverId = driver?.UserId ?? driverShift?.DriverId,
                DriverName = driver?.FullName,
                DriverPhone = string.IsNullOrWhiteSpace(driver?.PhoneNumber) ? null : driver.PhoneNumber,
            };

            if (sos is not null)
            {
                unit.Status = FleetUnitStatus.SosStatus;
                unit.Detail = sosAlerts.Count == 1
                    ? $"SOS sent {Ago(sos.Timestamp, now)} - open SOS Dispatch"
                    : $"{sosAlerts.Count} unresolved SOS alerts, latest {Ago(sos.Timestamp, now)} - open SOS Dispatch";
                counts.Sos++;
            }
            // In the shop: the taxi is marked under maintenance, or - when nobody is driving it -
            // it has a Garage work order in progress (an active work order, same as the Garage
            // page lists; one past its estimated finish is still in the shop until resolved).
            else if (TaxiStatusRules.IsUnderMaintenance(taxi.Status)
                || (activeShift is null && openJobs.Any(j => j.TaxiId == taxi.TaxiId)))
            {
                unit.Status = FleetUnitStatus.MaintenanceStatus;
                List<MaintenanceRecord> jobs = openJobs.Where(j => j.TaxiId == taxi.TaxiId).OrderByDescending(j => j.DateLogged).ToList();
                unit.Detail = jobs.Count == 0
                    ? "Marked under maintenance"
                    : string.Join(" · ", jobs.Take(2).Select(JobLabel)) + (jobs.Count > 2 ? $" · +{jobs.Count - 2} more" : "");
                counts.Maintenance++;
            }
            else if (activeShift is not null && activeShift.IsOnBreak)
            {
                unit.Status = FleetUnitStatus.OnBreakStatus;
                unit.Detail = "On break";
                counts.OnBreak++;
            }
            else if (activeShift is null)
            {
                // Nobody is driving it - parked, not "idle" (idle means a driver is on shift
                // but the unit isn't moving).
                unit.Status = FleetUnitStatus.ParkedStatus;
                unit.Detail = "No driver on shift - available";
                counts.Parked++;
            }
            else
            {
                // Latest GPS point from the shift itself (PERFORMANCE_REPORT.md P5) - the same time and speed the
                // phone writes to gps_telemetry with every point, without one query per unit.
                DateTime? lastFixAt = activeShift.CurrentPositionUpdatedAt;
                bool moving = IsMoving(activeShift, idleThresholdMinutes, now);
                if (moving)
                {
                    unit.Status = FleetUnitStatus.ActiveStatus;
                    unit.Detail = $"On shift since {activeShift.ShiftStart?.ToPhilippineTime():h:mm tt}";
                    counts.Active++;
                }
                else
                {
                    unit.Status = FleetUnitStatus.IdleStatus;
                    unit.Detail = lastFixAt is not DateTime fixAt ? "On shift - no GPS from the phone yet" : $"Not moving - last GPS {Ago(fixAt, now)}";
                    counts.Idle++;
                }
            }

            counts.Units.Add(unit);
        }

        return counts;
    }

    /// <summary>Unresolved SOS alerts by the taxi of the shift each was sent from. Shifts not
    /// already loaded (older than the dashboard's window) are looked up by either of their IDs.</summary>
    private async Task<Dictionary<string, List<(EmergencyAlert Alert, ShiftLog Shift)>>> UnresolvedSosByTaxiAsync(
        IEnumerable<EmergencyAlert> alerts, IEnumerable<ShiftLog> knownShifts)
    {
        var shiftsById = new Dictionary<string, ShiftLog>();
        foreach (ShiftLog shift in knownShifts)
        {
            if (!string.IsNullOrEmpty(shift.DocumentId)) shiftsById.TryAdd(shift.DocumentId, shift);
            if (!string.IsNullOrEmpty(shift.ShiftId)) shiftsById.TryAdd(shift.ShiftId, shift);
        }

        var byTaxi = new Dictionary<string, List<(EmergencyAlert, ShiftLog)>>(StringComparer.OrdinalIgnoreCase);
        foreach (EmergencyAlert alert in alerts.Where(a => !a.IsResolved && !string.IsNullOrWhiteSpace(a.ShiftId)))
        {
            if (!shiftsById.TryGetValue(alert.ShiftId, out ShiftLog? shift))
            {
                DocumentSnapshot doc = await Db.Collection("shifts").Document(alert.ShiftId).GetSnapshotAsync();
                if (doc.Exists)
                {
                    shift = doc.ConvertTo<ShiftLog>();
                }
                else
                {
                    QuerySnapshot byField = await Db.Collection("shifts").WhereEqualTo("shiftId", alert.ShiftId).Limit(1).GetSnapshotAsync();
                    shift = byField.Documents.Count > 0 ? byField.Documents[0].ConvertTo<ShiftLog>() : null;
                }

                if (shift is not null)
                {
                    shiftsById[alert.ShiftId] = shift;
                }
            }

            if (shift is null || string.IsNullOrWhiteSpace(shift.TaxiId))
            {
                continue;
            }

            if (!byTaxi.TryGetValue(shift.TaxiId, out List<(EmergencyAlert, ShiftLog)>? list))
            {
                byTaxi[shift.TaxiId] = list = new();
            }
            list.Add((alert, shift));
        }

        return byTaxi;
    }

    /// <summary>Garage work orders in the shop now (the Garage page's Active Work Orders) - a
    /// Scheduled one counts from its day, even before the Garage page moves it to In Progress.</summary>
    private static List<MaintenanceRecord> OpenWorkOrders(IEnumerable<MaintenanceRecord> maintenance) =>
        maintenance
            .Where(m => m.DateResolved is null && WorkOrderRules.IsInShopNow(m.Status, m.DateLogged, m.ScheduledDate, PhilippineTime.Now.Date))
            .ToList();

    private static string JobLabel(MaintenanceRecord job)
    {
        string title = string.IsNullOrWhiteSpace(job.IssueTitle) ? "Garage job" : job.IssueTitle.Trim();
        if (job.EstimatedCompletionDate is not DateTime est)
        {
            return $"{title} (no finish date)";
        }

        DateTime due = est.ToPhilippineTime().Date;
        return due < PhilippineTime.Now.Date ? $"{title} (overdue since {due:MMM d})" : $"{title} (until {due:MMM d})";
    }

    private static string Ago(DateTime utc, DateTime now)
    {
        TimeSpan age = now - utc;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        return $"{(int)age.TotalDays} d ago";
    }

    // Moving = a GPS point within the idle threshold with speed above 0. Read from the shift's copy of its
    // latest point (currentPositionUpdatedAt / currentSpeed, written with every gps_telemetry point).
    private static bool IsMoving(ShiftLog shift, double idleThresholdMinutes, DateTime now)
    {
        // GPS-014: a unit that hasn't driven yet this shift has CurrentSpeed stuck at 0 from its
        // very first telemetry tick, so it used to be flagged idle within moments of clocking in -
        // not after genuinely sitting still for idleThresholdMinutes, which is what the ticket (and
        // the manager configuring that number) actually expects. Until the shift itself is at least
        // that old, a driver who simply hasn't started driving yet (a pre-trip check, waiting for a
        // passenger) isn't idle.
        if (shift.ShiftStart is DateTime shiftStart && (now - shiftStart).TotalMinutes < idleThresholdMinutes)
        {
            return true;
        }

        if (shift.CurrentPositionUpdatedAt is not DateTime lastFixAt)
        {
            return false; // no telemetry yet - can't confirm movement, treat as Idle
        }

        bool isRecent = lastFixAt >= now.AddMinutes(-idleThresholdMinutes);
        return isRecent && (shift.CurrentSpeed ?? 0) > 0;
    }

    /// <summary>Which taxis are *currently* computed as Idle, with enough identity to raise an
    /// alert about each one - used by IdleAlertMonitorService. Mirrors BuildFleetStatusAsync's
    /// precedence (a taxi under maintenance, on break, or with an unresolved SOS is never
    /// "Idle" even if it's not moving) but only bothers evaluating taxis with an active shift,
    /// since a taxi with no driver on it right now has nobody to alert.</summary>
    public async Task<List<IdleDriverInfo>> GetIdleDriversAsync()
    {
        DateTime now = DateTime.UtcNow;

        // Background check every 3 minutes (PERFORMANCE_REPORT.md P2): taxis and users from the listener
        // cache; only unresolved SOS alerts (the only ones the check looks at) instead of all of them.
        List<TaxiUnit> taxis = await GetAllAsync<TaxiUnit>("taxis", fromCache: true);
        List<UserProfile> drivers = await GetAllAsync<UserProfile>("users", fromCache: true);
        List<EmergencyAlert> alerts = await GetWhereEqualAsync<EmergencyAlert>("emergency_alerts", "isResolved", false);
        List<ShiftLog> activeShifts = await GetWhereEqualAsync<ShiftLog>("shifts", "status", "Active");
        double idleThresholdMinutes = await GetIdleThresholdMinutesAsync();

        Dictionary<string, TaxiUnit> taxiById = taxis.ToDictionary(t => t.TaxiId);
        Dictionary<string, UserProfile> driverById = drivers.ToDictionary(d => d.UserId);

        var result = new List<IdleDriverInfo>();
        foreach (ShiftLog shift in activeShifts)
        {
            if (!taxiById.TryGetValue(shift.TaxiId, out TaxiUnit? taxi)) continue;
            if (TaxiStatusRules.IsUnderMaintenance(taxi.Status)) continue;
            if (shift.IsOnBreak) continue;
            if (alerts.Any(a => a.ShiftId == shift.ShiftId && !a.IsResolved)) continue; // unresolved SOS takes precedence
            if (IsMoving(shift, idleThresholdMinutes, now)) continue;

            driverById.TryGetValue(shift.DriverId, out UserProfile? driver);
            result.Add(new IdleDriverInfo
            {
                DriverId = shift.DriverId,
                DriverName = driver?.FullName ?? "Unknown driver",
                TaxiId = shift.TaxiId,
                UnitLabel = FormatUnitLabel(shift.TaxiId),
                ShiftId = shift.ShiftId,
                IdleThresholdMinutes = idleThresholdMinutes,
            });
        }

        return result;
    }

    // Same "TAXI_004" -> "Unit 04" convention as GarageService.FormatUnitLabel.
    private static string FormatUnitLabel(string taxiId)
    {
        int lastUnderscore = taxiId.LastIndexOf('_');
        string suffix = lastUnderscore >= 0 ? taxiId[(lastUnderscore + 1)..] : taxiId;
        return int.TryParse(suffix, out int n) ? $"Unit {n:00}" : $"Unit {taxiId}";
    }

    // ---------------------------------------------------------------------
    // Top Driver Standings
    // ---------------------------------------------------------------------

    /// <summary>How far back Top Driver Standings look.</summary>
    public const int StandingsWindowDays = 30;

    /// <summary>
    /// Top Driver Standings: each driver's DriverPerformance from <paramref name="fromPh"/> to
    /// <paramref name="nowUtc"/> - attendance against the days they were expected to drive,
    /// on-time returns, boundaries paid on time, damage incidents - ranked by the average of
    /// the three percentages, then by boundaries remitted.
    /// </summary>
    private async Task<List<DriverStanding>> BuildTopDriversAsync(List<UserProfile> drivers, List<MaintenanceRecord> maintenance, List<EmergencyAlert> alerts, DateTime fromPh, DateTime nowUtc)
    {
        DateTime fromUtc = fromPh.Date - PhilippineTime.Offset;
        List<ShiftLog> shifts = await GetSinceAsync<ShiftLog>("shifts", "shiftStart", fromUtc);
        // Day-off entries are stored at the day's UTC midnight - a day earlier still covers fromPh.
        List<ShiftSchedule> exceptions = await GetSinceAsync<ShiftSchedule>("shift_schedules", "scheduledStartTime", fromUtc.AddDays(-1));
        // Payments for these shifts are recorded after they start.
        List<BoundaryPayment> payments = await GetSinceAsync<BoundaryPayment>("boundary_payments", "timestamp", fromUtc);
        decimal defaultRate = await GetDefaultBoundaryRateAsync();

        List<DriverStanding> standings = drivers.Select(driver =>
        {
            List<ShiftLog> driverShifts = shifts.Where(s => s.DriverId == driver.UserId).ToList();
            HashSet<string> ids = driverShifts.SelectMany(s => new[] { s.DocumentId, s.ShiftId }).Where(id => !string.IsNullOrEmpty(id)).ToHashSet();
            DriverPerformance performance = DriverPerformanceCalculator.Calculate(
                driver, driverShifts, exceptions.Where(e => e.DriverId == driver.UserId).ToList(), maintenance,
                payments.Where(p => ids.Contains(p.ShiftId)).ToList(), alerts, defaultRate, fromPh, nowUtc);

            return new DriverStanding
            {
                DriverId = driver.UserId,
                FullName = driver.FullName,
                TaxiId = driver.AssignedTaxiId,
                PunctualPercent = performance.PunctualityPercent ?? 0,
                IncidentCount = performance.Incidents,
                BoundariesRemitted = performance.BoundariesRemitted,
                Performance = performance,
            };
        })
        .OrderByDescending(d => d.Performance.Score)
        .ThenByDescending(d => d.BoundariesRemitted)
        .ThenBy(d => d.FullName)
        .ToList();

        for (int i = 0; i < standings.Count; i++)
        {
            standings[i].Rank = i + 1;
        }

        return standings;
    }

    private async Task<decimal> GetDefaultBoundaryRateAsync()
    {
        try
        {
            DocumentSnapshot snapshot = await Db.Collection("system_configs").Document("global").GetSnapshotAsync();
            if (snapshot.Exists && snapshot.ConvertTo<BoundaryRateConfigProxy>().DefaultBoundaryRate is double rate && rate > 0)
            {
                return (decimal)rate;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the default boundary rate");
        }
        return ShiftRules.DefaultBoundaryRate;
    }

    // ---------------------------------------------------------------------
    // Chart series
    // ---------------------------------------------------------------------

    private static List<ChartPoint> BuildBoundaryCollections(List<BoundaryPayment> payments)
    {
        decimal paid = payments.Sum(p => p.AmountPaid);
        decimal outstanding = payments.Sum(p => Math.Max(0, p.ExpectedBoundary + p.LateFees - p.AmountPaid));

        return new List<ChartPoint>
        {
            new() { Label = "Paid", Value = paid },
            new() { Label = "Outstanding", Value = outstanding },
        };
    }

    private static List<ChartPoint> BuildFuelVerification(List<FuelLog> fuelLogs) =>
        fuelLogs
            .GroupBy(f => f.VerificationStatus.ToString())
            .Select(g => new ChartPoint { Label = g.Key, Value = g.Sum(f => f.FuelCost) })
            .ToList();

    private static List<ChartPoint> BuildFleetMileageByTaxi(List<ShiftLog> shifts) =>
        shifts
            .GroupBy(s => s.TaxiId)
            .Select(g => new ChartPoint { Label = g.Key, Value = SumMileage(g) })
            .ToList();

    private static List<ChartPoint> BuildMaintenanceExpenses(List<MaintenanceRecord> maintenance) =>
        maintenance
            .GroupBy(m => m.MaintenanceType.ToString())
            .Select(g => new ChartPoint { Label = g.Key, Value = g.Sum(m => m.TotalCost) })
            .ToList();

    // ---------------------------------------------------------------------
    // Report export (Generate Reports modal) - CSV only for now; PDF/Excel need a
    // rendering library and an actual layout spec, neither of which exist yet.
    // ---------------------------------------------------------------------

    public static readonly IReadOnlyList<string> SupportedReportTypes = new[]
    {
        "Financial Summary",
        "Maintenance History",
        "Fuel & Mileage Analysis",
        "Driver Performance",
        "Full Audit Trail",
    };

    public async Task<string> BuildReportCsvAsync(string reportType, DateTime fromUtc, DateTime toUtc) =>
        ToCsv(await BuildReportAsync(reportType, fromUtc, toUtc));

    /// <summary>The report's data as tables - what both the CSV (BuildReportCsvAsync) and the PDF
    /// (ManagerWeb's PdfReportRenderer, ANA-012) are made from.</summary>
    public async Task<ReportDocument> BuildReportAsync(string reportType, DateTime fromUtc, DateTime toUtc)
    {
        List<ReportTable> tables = reportType switch
        {
            "Financial Summary" => await BuildFinancialSummaryAsync(fromUtc, toUtc),
            "Maintenance History" => await BuildMaintenanceHistoryAsync(fromUtc, toUtc),
            "Fuel & Mileage Analysis" => await BuildFuelMileageAsync(fromUtc, toUtc),
            "Driver Performance" => await BuildDriverPerformanceAsync(fromUtc, toUtc),
            "Full Audit Trail" => await BuildAuditTrailAsync(fromUtc, toUtc),
            _ => throw new ArgumentException($"Unknown report type: {reportType}", nameof(reportType)),
        };
        return new ReportDocument { Title = reportType, Tables = tables };
    }

    private async Task<List<ReportTable>> BuildFinancialSummaryAsync(DateTime fromUtc, DateTime toUtc)
    {
        List<BoundaryPayment> payments = await GetBetweenAsync<BoundaryPayment>("boundary_payments", "timestamp", fromUtc, toUtc);

        return new List<ReportTable>
        {
            new()
            {
                Headers = new[] { "Date", "ShiftId", "ExpectedBoundary", "LateFees", "AmountPaid", "PaymentMethod", "PaymentStatus" },
                Rows = payments.OrderBy(p => p.Timestamp).Select(p => new object?[]
                {
                    p.Timestamp.ToPhilippineTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    p.ShiftId, p.ExpectedBoundary, p.LateFees, p.AmountPaid, p.PaymentMethod, p.PaymentStatus,
                }).ToList(),
                MoneyColumns = new HashSet<int> { 2, 3, 4 },
            },
        };
    }

    private async Task<List<ReportTable>> BuildMaintenanceHistoryAsync(DateTime fromUtc, DateTime toUtc)
    {
        List<MaintenanceRecord> records = await GetBetweenAsync<MaintenanceRecord>("maintenance_logs", "dateLogged", fromUtc, toUtc);

        return new List<ReportTable>
        {
            new()
            {
                Headers = new[] { "DateLogged", "TaxiId", "MaintenanceType", "IssueTitle", "PriorityLevel", "LaborCost", "TotalCost", "DateResolved" },
                Rows = records.OrderBy(m => m.DateLogged).Select(m => new object?[]
                {
                    m.DateLogged.ToPhilippineTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    m.TaxiId, m.MaintenanceType, m.IssueTitle, m.PriorityLevel, m.LaborCost, m.TotalCost,
                    m.DateResolved.ToPhilippineTime()?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "Open",
                }).ToList(),
                MoneyColumns = new HashSet<int> { 5, 6 },
                RelativeWidths = new[] { 2f, 1.3f, 1.8f, 3f, 1.2f, 1.3f, 1.3f, 2f },
            },
        };
    }

    private async Task<List<ReportTable>> BuildFuelMileageAsync(DateTime fromUtc, DateTime toUtc)
    {
        List<FuelLog> fuelLogs = await GetBetweenAsync<FuelLog>("fuel_logs", "receiptTimestamp", fromUtc, toUtc);
        List<ShiftLog> shifts = await GetBetweenAsync<ShiftLog>("shifts", "shiftStart", fromUtc, toUtc);

        return new List<ReportTable>
        {
            new()
            {
                Heading = "Fuel Logs",
                Headers = new[] { "ReceiptTimestamp", "ShiftId", "FuelStation", "ORNumber", "LitersRefueled", "FuelCost", "VerificationStatus", "OdometerReading" },
                Rows = fuelLogs.OrderBy(f => f.ReceiptTimestamp).Select(f => new object?[]
                {
                    f.ReceiptTimestamp.ToPhilippineTime()?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty,
                    f.ShiftId, f.FuelStation, f.ORNumber, f.LitersRefueled, f.FuelCost, f.VerificationStatus, f.OdometerReading,
                }).ToList(),
                MoneyColumns = new HashSet<int> { 5 },
            },
            new()
            {
                Heading = "Mileage By Taxi",
                Headers = new[] { "TaxiId", "TotalMileage" },
                Rows = shifts.GroupBy(s => s.TaxiId).Select(g => new object?[] { g.Key, SumMileage(g) }).ToList(),
            },
        };
    }

    private async Task<List<ReportTable>> BuildDriverPerformanceAsync(DateTime fromUtc, DateTime toUtc)
    {
        List<UserProfile> drivers = (await GetAllAsync<UserProfile>("users"))
            .Where(u => string.Equals(u.Role, "Driver", StringComparison.OrdinalIgnoreCase))
            .ToList();
        List<MaintenanceRecord> maintenance = await GetAllAsync<MaintenanceRecord>("maintenance_logs");
        List<EmergencyAlert> alerts = await GetAllAsync<EmergencyAlert>("emergency_alerts");
        DateTime until = toUtc < DateTime.UtcNow ? toUtc : DateTime.UtcNow;
        List<DriverStanding> standings = await BuildTopDriversAsync(drivers, maintenance, alerts, fromUtc.ToPhilippineTime().Date, until);

        static string Pct(double? p) => p is double v ? v.ToString("0.0", CultureInfo.InvariantCulture) : "";
        return new List<ReportTable>
        {
            new()
            {
                Headers = new[] { "Rank", "DriverName", "TaxiId", "ExpectedDays", "DaysWorked", "MissedDays", "AttendancePercent",
                        "ShiftsCompleted", "LateReturns", "PunctualityPercent", "BoundariesDue", "PaidOnTime",
                        "PaymentReliabilityPercent", "SosAlerts", "DamageIncidents", "BoundariesRemitted" },
                Rows = standings.Select(d => new object?[]
                {
                    d.Rank, d.FullName, d.TaxiId, d.Performance.ExpectedDays, d.Performance.DaysWorked, d.Performance.MissedDays,
                    Pct(d.Performance.AttendancePercent), d.Performance.ShiftsCompleted, d.Performance.LateReturns,
                    Pct(d.Performance.PunctualityPercent), d.Performance.BoundariesDue, d.Performance.BoundariesPaidOnTime,
                    Pct(d.Performance.PaymentReliabilityPercent), d.Performance.SosAlerts, d.Performance.DamageIncidents, d.BoundariesRemitted,
                }).ToList(),
                MoneyColumns = new HashSet<int> { 15 },
                RelativeWidths = new[] { 0.7f, 2.4f, 1.3f, 1f, 1f, 1f, 1.1f, 1f, 1f, 1.1f, 1f, 1f, 1.2f, 0.9f, 1f, 1.5f },
            },
        };
    }

    private async Task<List<ReportTable>> BuildAuditTrailAsync(DateTime fromUtc, DateTime toUtc)
    {
        List<AuditLog> logs = await GetBetweenAsync<AuditLog>("audit_logs", "timestamp", fromUtc, toUtc);

        return new List<ReportTable>
        {
            new()
            {
                Headers = new[] { "Timestamp", "UserId", "ActionType", "Details", "IpAddress" },
                Rows = logs.OrderBy(a => a.Timestamp).Select(a => new object?[]
                {
                    a.Timestamp.ToPhilippineTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    a.UserId, a.ActionType, a.AuditLogDetails, a.IpAddress,
                }).ToList(),
                RelativeWidths = new[] { 1.6f, 2f, 2f, 6f, 1.4f },
            },
        };
    }

    /// <summary>The CSV a report has always downloaded as: one table as-is; several each under
    /// their heading line, separated by a blank line.</summary>
    public static string ToCsv(ReportDocument report)
    {
        if (report.Tables.Count == 1 && report.Tables[0].Heading is null)
        {
            return BuildCsv(report.Tables[0].Headers, report.Tables[0].Rows);
        }

        var sb = new StringBuilder();
        for (int i = 0; i < report.Tables.Count; i++)
        {
            ReportTable table = report.Tables[i];
            if (i > 0)
            {
                sb.AppendLine();
            }
            if (table.Heading is not null)
            {
                sb.AppendLine(table.Heading);
            }
            sb.Append(BuildCsv(table.Headers, table.Rows));
        }
        return sb.ToString();
    }

    private static string BuildCsv(IEnumerable<string> headers, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(CsvEscape)));
        foreach (IEnumerable<object?> row in rows)
        {
            sb.AppendLine(string.Join(",", row.Select(v => CsvEscape(v?.ToString() ?? string.Empty))));
        }
        return sb.ToString();
    }

    // Quoted, and safe from formula injection when opened in Excel (audit G-02).
    private static string CsvEscape(string value) => CsvRules.Escape(value);

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    // GPS-014: system_configs/global had fuelExpectedKmPerLiter stored as an empty string against
    // a Dictionary<string, double> field - ConvertTo<SystemConfig> throws for the whole document
    // over that one unrelated field, so GetIdleThresholdMinutesAsync and GetDefaultBoundaryRateAsync
    // (the two full-document readers here) were silently falling back to their defaults even with a
    // manager-configured value in place. A narrow proxy - just the one field each actually needs -
    // can't be broken by a bad value anywhere else in that shared, hand-editable document. Same
    // pattern as ShiftManagementService's GarageConfigProxy.
    [FirestoreData]
    private sealed class IdleThresholdConfigProxy
    {
        [FirestoreProperty("idleThresholdMinutes")]
        public double IdleThresholdMinutes { get; set; }
    }

    [FirestoreData]
    private sealed class BoundaryRateConfigProxy
    {
        [FirestoreProperty("defaultBoundaryRate")]
        public double DefaultBoundaryRate { get; set; }
    }

    private async Task<double> GetIdleThresholdMinutesAsync()
    {
        DocumentSnapshot snapshot = await Db.Collection("system_configs").Document("global").GetSnapshotAsync();
        if (!snapshot.Exists)
        {
            return DefaultIdleThresholdMinutes;
        }

        try
        {
            IdleThresholdConfigProxy config = snapshot.ConvertTo<IdleThresholdConfigProxy>();
            return config.IdleThresholdMinutes > 0 ? config.IdleThresholdMinutes : DefaultIdleThresholdMinutes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read system_configs/global, falling back to default idle threshold");
            return DefaultIdleThresholdMinutes;
        }
    }

    /// <param name="fromCache">Background checks only: the same documents from CollectionCache's listeners
    /// (falls back to a direct read if the cache isn't ready).</param>
    private async Task<List<T>> GetAllAsync<T>(string collection, bool fromCache = false) where T : class
    {
        if (fromCache && await _cache.GetDocumentsAsync(collection) is IReadOnlyList<DocumentSnapshot> cached)
        {
            return ConvertDocuments<T>(cached, collection);
        }

        QuerySnapshot snapshot = await Db.Collection(collection).GetSnapshotAsync();
        PerfLog.Reads(collection, snapshot.Count);
        return ConvertDocuments<T>(snapshot, collection);
    }

    // Range query (e.g. "shiftStart >= 14 days ago") - a single-field filter like this is
    // auto-indexed by Firestore, no composite index needed. Only matches documents where
    // dateField is present and set (a missing/null field never satisfies a range filter).
    private async Task<List<T>> GetSinceAsync<T>(string collection, string dateField, DateTime sinceUtc) where T : class
    {
        QuerySnapshot snapshot = await Db.Collection(collection)
            .WhereGreaterThanOrEqualTo(dateField, sinceUtc)
            .GetSnapshotAsync();
        PerfLog.Reads(collection, snapshot.Count);
        return ConvertDocuments<T>(snapshot, collection);
    }

    // Two inequalities on the same field (>= from AND <= to) still count as a single-field
    // filter as far as Firestore indexing is concerned - no composite index needed here
    // either. Used for report export, where the date range comes from the user rather
    // than a fixed 14-day window.
    private async Task<List<T>> GetBetweenAsync<T>(string collection, string dateField, DateTime fromUtc, DateTime toUtc) where T : class
    {
        QuerySnapshot snapshot = await Db.Collection(collection)
            .WhereGreaterThanOrEqualTo(dateField, fromUtc)
            .WhereLessThanOrEqualTo(dateField, toUtc)
            .GetSnapshotAsync();
        PerfLog.Reads(collection, snapshot.Count);
        return ConvertDocuments<T>(snapshot, collection);
    }

    // Equality query (e.g. status == "Active") - also auto-indexed, and its read cost is
    // bounded by how many documents currently match right now, not by collection history.
    private async Task<List<T>> GetWhereEqualAsync<T>(string collection, string field, object value) where T : class
    {
        QuerySnapshot snapshot = await Db.Collection(collection)
            .WhereEqualTo(field, value)
            .GetSnapshotAsync();
        PerfLog.Reads(collection, snapshot.Count);
        return ConvertDocuments<T>(snapshot, collection);
    }

    private List<T> ConvertDocuments<T>(QuerySnapshot snapshot, string collection) where T : class =>
        ConvertDocuments<T>(snapshot.Documents, collection);

    private List<T> ConvertDocuments<T>(IReadOnlyList<DocumentSnapshot> documents, string collection) where T : class
    {
        var results = new List<T>(documents.Count);

        foreach (DocumentSnapshot doc in documents)
        {
            try
            {
                results.Add(doc.ConvertTo<T>());
            }
            catch (Exception ex)
            {
                // One malformed field on one document (e.g. a hand-entered value with the
                // wrong type) shouldn't take down the whole dashboard - skip it and keep going.
                _logger.LogWarning(ex, "Skipping {Collection}/{DocumentId}: failed to convert to {Type}",
                    collection, doc.Id, typeof(T).Name);
            }
        }

        return results;
    }
}

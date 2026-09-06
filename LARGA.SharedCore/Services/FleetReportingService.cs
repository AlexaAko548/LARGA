using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
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
    private const double DefaultIdleThresholdMinutes = 10;

    // Lazy: credential/connection failures should surface when a method below actually
    // runs (where callers like Dashboard.razor already catch them), not at DI-construction
    // time - see the comment on this service's registration in ManagerWeb's Program.cs.
    private readonly Lazy<FirestoreDb> _dbLazy;
    private readonly ILogger<FleetReportingService> _logger;

    private FirestoreDb Db => _dbLazy.Value;

    public FleetReportingService(Lazy<FirestoreDb> dbLazy, ILogger<FleetReportingService> logger)
    {
        _dbLazy = dbLazy;
        _logger = logger;
    }

    public async Task<DashboardSnapshot> GetDashboardSnapshotAsync()
    {
        DateTime now = DateTime.UtcNow;
        DateTime twoWeeksAgo = now.AddDays(-14);

        // Bounded by fleet/team size, not history - safe to fetch in full forever.
        List<TaxiUnit> taxis = await GetAllAsync<TaxiUnit>("taxis");
        List<UserProfile> drivers = (await GetAllAsync<UserProfile>("users"))
            .Where(u => string.Equals(u.Role, "Driver", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Incident-driven, low volume even over years of real operation - see the class
        // comment above for why these stay full fetches instead of a null-filter query.
        List<MaintenanceRecord> maintenance = await GetAllAsync<MaintenanceRecord>("maintenance_logs");
        List<EmergencyAlert> alerts = await GetAllAsync<EmergencyAlert>("emergency_alerts");

        // High-frequency, unbounded-by-history - query narrowly instead of fetching all.
        List<ShiftLog> activeShifts = await GetWhereEqualAsync<ShiftLog>("shifts", "status", "Active");
        List<ShiftLog> recentShifts = await GetSinceAsync<ShiftLog>("shifts", "shiftStart", twoWeeksAgo);
        List<BoundaryPayment> recentPayments = await GetSinceAsync<BoundaryPayment>("boundary_payments", "timestamp", twoWeeksAgo);
        List<FuelLog> recentFuelLogs = await GetSinceAsync<FuelLog>("fuel_logs", "receiptTimestamp", twoWeeksAgo);

        double idleThresholdMinutes = await GetIdleThresholdMinutesAsync();

        var snapshot = new DashboardSnapshot
        {
            WeeklyRevenue = BuildWeeklyRevenue(recentPayments, now),
            ActiveDrivers = BuildActiveDrivers(activeShifts, drivers),
            FleetMileage = BuildFleetMileage(recentShifts, now),
            OpenIncidents = BuildOpenIncidents(maintenance, alerts, now),
            FleetStatus = await BuildFleetStatusAsync(taxis, activeShifts, recentShifts, alerts, idleThresholdMinutes, now),
            // Top Driver Standings is now a "last 14 days" leaderboard rather than an
            // all-time one - a deliberate side effect of no longer fetching full shift/
            // payment history. Arguably more useful anyway (recent performance vs.
            // lifetime), but flagging the semantic change explicitly.
            TopDrivers = BuildTopDrivers(drivers, recentShifts, maintenance, alerts, recentPayments),
            BoundaryCollections = BuildBoundaryCollections(recentPayments),
            FuelVerification = BuildFuelVerification(recentFuelLogs),
            FleetMileageByTaxi = BuildFleetMileageByTaxi(recentShifts),
            MaintenanceExpenses = BuildMaintenanceExpenses(maintenance),
        };

        return snapshot;
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

    private static decimal SumMileage(IEnumerable<ShiftLog> shifts) =>
        shifts.Sum(s => s.EndMileage > s.StartMileage ? s.EndMileage - s.StartMileage : 0);

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
        List<ShiftLog> activeShifts,
        List<ShiftLog> recentShifts,
        List<EmergencyAlert> alerts,
        double idleThresholdMinutes,
        DateTime now)
    {
        var counts = new FleetStatusCounts();

        foreach (TaxiUnit taxi in taxis)
        {
            ShiftLog? activeShift = activeShifts.FirstOrDefault(s => s.TaxiId == taxi.TaxiId);

            // "Most recent shift" only looks back 14 days now (recentShifts' window) rather
            // than the taxi's entire history - if a taxi's last shift is older than that AND
            // still has an unresolved SOS alert against it, that's a data-hygiene problem
            // worth fixing at the source, not something worth an unbounded query to keep
            // detecting indefinitely. Falls back to the active shift if the window missed it.
            ShiftLog? mostRecentShift = recentShifts
                .Where(s => s.TaxiId == taxi.TaxiId)
                .OrderByDescending(s => s.ShiftStart ?? DateTime.MinValue)
                .FirstOrDefault() ?? activeShift;

            bool hasUnresolvedSos = mostRecentShift is not null
                && alerts.Any(a => a.ShiftId == mostRecentShift.ShiftId && !a.IsResolved);

            if (hasUnresolvedSos)
            {
                counts.Sos++;
            }
            else if (string.Equals(taxi.Status, "Under Maintenance", StringComparison.OrdinalIgnoreCase))
            {
                counts.Maintenance++;
            }
            else if (activeShift is not null && activeShift.IsOnBreak)
            {
                counts.OnBreak++;
            }
            else if (activeShift is not null && await IsMovingAsync(activeShift.ShiftId, idleThresholdMinutes, now))
            {
                counts.Active++;
            }
            else
            {
                counts.Idle++;
            }
        }

        return counts;
    }

    private async Task<bool> IsMovingAsync(string shiftId, double idleThresholdMinutes, DateTime now)
    {
        // NOTE: this equality-filter + order-by-a-different-field query needs a Firestore
        // composite index on gps_telemetry (shiftId Asc, timestamp Desc). The first time this
        // runs without one, Firestore throws a FailedPrecondition exception whose message
        // contains a direct "create this index" console link - open it, click Create, and
        // retry once it finishes building (usually under a minute for a collection this size).
        QuerySnapshot snapshot = await Db.Collection("gps_telemetry")
            .WhereEqualTo("shiftId", shiftId)
            .OrderByDescending("timestamp")
            .Limit(1)
            .GetSnapshotAsync();

        if (snapshot.Documents.Count == 0)
        {
            return false; // no telemetry yet - can't confirm movement, treat as Idle
        }

        GpsTelemetry latest = snapshot.Documents[0].ConvertTo<GpsTelemetry>();
        bool isRecent = latest.Timestamp >= now.AddMinutes(-idleThresholdMinutes);
        return isRecent && latest.Speed > 0;
    }

    // ---------------------------------------------------------------------
    // Top Driver Standings
    // ---------------------------------------------------------------------

    private static List<DriverStanding> BuildTopDrivers(
        List<UserProfile> drivers,
        List<ShiftLog> shifts,
        List<MaintenanceRecord> maintenance,
        List<EmergencyAlert> alerts,
        List<BoundaryPayment> payments)
    {
        var standings = new List<DriverStanding>();

        foreach (UserProfile driver in drivers)
        {
            List<ShiftLog> driverShifts = shifts.Where(s => s.DriverId == driver.UserId).ToList();
            HashSet<string> driverShiftIds = driverShifts.Select(s => s.ShiftId).ToHashSet();

            double punctualPercent = driverShifts.Count == 0
                ? 0
                : 100.0 * driverShifts.Count(s => s.Status != "Overdue") / driverShifts.Count;

            int incidentCount = maintenance.Count(m => m.ShiftId is not null && driverShiftIds.Contains(m.ShiftId))
                + alerts.Count(a => driverShiftIds.Contains(a.ShiftId));

            decimal boundariesRemitted = payments
                .Where(p => driverShiftIds.Contains(p.ShiftId))
                .Sum(p => p.AmountPaid);

            standings.Add(new DriverStanding
            {
                DriverId = driver.UserId,
                FullName = driver.FullName,
                TaxiId = driver.AssignedTaxiId,
                PunctualPercent = punctualPercent,
                IncidentCount = incidentCount,
                BoundariesRemitted = boundariesRemitted,
            });
        }

        List<DriverStanding> ranked = standings
            .OrderByDescending(d => d.PunctualPercent)
            .ThenByDescending(d => d.BoundariesRemitted)
            .ToList();

        for (int i = 0; i < ranked.Count; i++)
        {
            ranked[i].Rank = i + 1;
        }

        return ranked;
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

    public async Task<string> BuildReportCsvAsync(string reportType, DateTime fromUtc, DateTime toUtc)
    {
        return reportType switch
        {
            "Financial Summary" => await BuildFinancialSummaryCsvAsync(fromUtc, toUtc),
            "Maintenance History" => await BuildMaintenanceHistoryCsvAsync(fromUtc, toUtc),
            "Fuel & Mileage Analysis" => await BuildFuelMileageCsvAsync(fromUtc, toUtc),
            "Driver Performance" => await BuildDriverPerformanceCsvAsync(fromUtc, toUtc),
            "Full Audit Trail" => await BuildAuditTrailCsvAsync(fromUtc, toUtc),
            _ => throw new ArgumentException($"Unknown report type: {reportType}", nameof(reportType)),
        };
    }

    private async Task<string> BuildFinancialSummaryCsvAsync(DateTime fromUtc, DateTime toUtc)
    {
        List<BoundaryPayment> payments = await GetBetweenAsync<BoundaryPayment>("boundary_payments", "timestamp", fromUtc, toUtc);

        return BuildCsv(
            new[] { "Date", "ShiftId", "ExpectedBoundary", "LateFees", "AmountPaid", "PaymentMethod", "PaymentStatus" },
            payments.OrderBy(p => p.Timestamp).Select(p => new object?[]
            {
                p.Timestamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                p.ShiftId, p.ExpectedBoundary, p.LateFees, p.AmountPaid, p.PaymentMethod, p.PaymentStatus,
            }));
    }

    private async Task<string> BuildMaintenanceHistoryCsvAsync(DateTime fromUtc, DateTime toUtc)
    {
        List<MaintenanceRecord> records = await GetBetweenAsync<MaintenanceRecord>("maintenance_logs", "dateLogged", fromUtc, toUtc);

        return BuildCsv(
            new[] { "DateLogged", "TaxiId", "MaintenanceType", "IssueTitle", "PriorityLevel", "LaborCost", "TotalCost", "DateResolved" },
            records.OrderBy(m => m.DateLogged).Select(m => new object?[]
            {
                m.DateLogged.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                m.TaxiId, m.MaintenanceType, m.IssueTitle, m.PriorityLevel, m.LaborCost, m.TotalCost,
                m.DateResolved?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "Open",
            }));
    }

    private async Task<string> BuildFuelMileageCsvAsync(DateTime fromUtc, DateTime toUtc)
    {
        List<FuelLog> fuelLogs = await GetBetweenAsync<FuelLog>("fuel_logs", "receiptTimestamp", fromUtc, toUtc);
        List<ShiftLog> shifts = await GetBetweenAsync<ShiftLog>("shifts", "shiftStart", fromUtc, toUtc);

        var sb = new StringBuilder();
        sb.AppendLine("Fuel Logs");
        sb.Append(BuildCsv(
            new[] { "ReceiptTimestamp", "ShiftId", "FuelStation", "LitersRefueled", "FuelCost", "VerificationStatus", "OdometerReading" },
            fuelLogs.OrderBy(f => f.ReceiptTimestamp).Select(f => new object?[]
            {
                f.ReceiptTimestamp?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty,
                f.ShiftId, f.FuelStation, f.LitersRefueled, f.FuelCost, f.VerificationStatus, f.OdometerReading,
            })));

        sb.AppendLine();
        sb.AppendLine("Mileage By Taxi");
        sb.Append(BuildCsv(
            new[] { "TaxiId", "TotalMileage" },
            shifts.GroupBy(s => s.TaxiId).Select(g => new object?[] { g.Key, SumMileage(g) })));

        return sb.ToString();
    }

    private async Task<string> BuildDriverPerformanceCsvAsync(DateTime fromUtc, DateTime toUtc)
    {
        List<UserProfile> drivers = (await GetAllAsync<UserProfile>("users"))
            .Where(u => string.Equals(u.Role, "Driver", StringComparison.OrdinalIgnoreCase))
            .ToList();
        List<ShiftLog> shifts = await GetBetweenAsync<ShiftLog>("shifts", "shiftStart", fromUtc, toUtc);
        List<MaintenanceRecord> maintenance = await GetAllAsync<MaintenanceRecord>("maintenance_logs");
        List<EmergencyAlert> alerts = await GetAllAsync<EmergencyAlert>("emergency_alerts");
        List<BoundaryPayment> payments = await GetBetweenAsync<BoundaryPayment>("boundary_payments", "timestamp", fromUtc, toUtc);

        List<DriverStanding> standings = BuildTopDrivers(drivers, shifts, maintenance, alerts, payments);

        return BuildCsv(
            new[] { "Rank", "DriverName", "TaxiId", "PunctualPercent", "IncidentCount", "BoundariesRemitted" },
            standings.Select(d => new object?[]
            {
                d.Rank, d.FullName, d.TaxiId, d.PunctualPercent.ToString("0.0", CultureInfo.InvariantCulture),
                d.IncidentCount, d.BoundariesRemitted,
            }));
    }

    private async Task<string> BuildAuditTrailCsvAsync(DateTime fromUtc, DateTime toUtc)
    {
        List<AuditLog> logs = await GetBetweenAsync<AuditLog>("audit_logs", "timestamp", fromUtc, toUtc);

        return BuildCsv(
            new[] { "Timestamp", "UserId", "ActionType", "Details", "IpAddress" },
            logs.OrderBy(a => a.Timestamp).Select(a => new object?[]
            {
                a.Timestamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                a.UserId, a.ActionType, a.AuditLogDetails, a.IpAddress,
            }));
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

    private static string CsvEscape(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private async Task<double> GetIdleThresholdMinutesAsync()
    {
        DocumentSnapshot snapshot = await Db.Collection("system_configs").Document("global").GetSnapshotAsync();
        if (!snapshot.Exists)
        {
            return DefaultIdleThresholdMinutes;
        }

        try
        {
            SystemConfig config = snapshot.ConvertTo<SystemConfig>();
            return config.IdleThresholdMinutes > 0 ? config.IdleThresholdMinutes : DefaultIdleThresholdMinutes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read system_configs/global, falling back to default idle threshold");
            return DefaultIdleThresholdMinutes;
        }
    }

    private async Task<List<T>> GetAllAsync<T>(string collection) where T : class
    {
        QuerySnapshot snapshot = await Db.Collection(collection).GetSnapshotAsync();
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
        return ConvertDocuments<T>(snapshot, collection);
    }

    // Equality query (e.g. status == "Active") - also auto-indexed, and its read cost is
    // bounded by how many documents currently match right now, not by collection history.
    private async Task<List<T>> GetWhereEqualAsync<T>(string collection, string field, object value) where T : class
    {
        QuerySnapshot snapshot = await Db.Collection(collection)
            .WhereEqualTo(field, value)
            .GetSnapshotAsync();
        return ConvertDocuments<T>(snapshot, collection);
    }

    private List<T> ConvertDocuments<T>(QuerySnapshot snapshot, string collection) where T : class
    {
        var results = new List<T>(snapshot.Documents.Count);

        foreach (DocumentSnapshot doc in snapshot.Documents)
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

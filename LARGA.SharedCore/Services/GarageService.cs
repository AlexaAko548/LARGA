using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.SharedCore.Models.Garage;
using LARGA.Shared.Models.Entities;
using Microsoft.Extensions.Logging;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Backs the ManagerWeb Garage / Maintenance Scheduler page: driver-filed defect reports
/// awaiting triage, active work orders currently in the shop, and upcoming preventive
/// maintenance (routine checks). Same Lazy&lt;FirestoreDb&gt; deferred-credentials pattern as
/// the other ManagerWeb services.
///
/// `maintenance_logs` stays a full fetch here, same as FleetReportingService's dashboard
/// reads - it's incident-driven, not written once-per-taxi-per-day like shifts/
/// boundary_payments, so its volume grows far slower (see docs/ERD.md "Dashboard read-cost
/// notes"). `routine_checks` is bounded by how many preventive-maintenance items are
/// currently outstanding across the fleet, always small.
/// </summary>
public class GarageService
{
    /// <summary>Max maintenance jobs (by EstimatedCompletionDate) the shop takes on any one day - used by GetNextAvailableScheduleDateAsync's auto-scheduling.</summary>
    private const int DailyScheduleCapacity = 2;

    private readonly Lazy<FirestoreDb> _dbLazy;
    private readonly ILogger<GarageService> _logger;

    private FirestoreDb Db => _dbLazy.Value;

    public GarageService(Lazy<FirestoreDb> dbLazy, ILogger<GarageService> logger)
    {
        _dbLazy = dbLazy;
        _logger = logger;
    }

    public async Task<GarageSnapshot> GetGarageSnapshotAsync()
    {
        List<MaintenanceRecord> records = await GetAllAsync<MaintenanceRecord>("maintenance_logs");
        List<TaxiUnit> taxis = await GetAllAsync<TaxiUnit>("taxis");
        List<UserProfile> drivers = await GetAllAsync<UserProfile>("users");
        List<RoutineCheckItem> checks = await GetAllAsync<RoutineCheckItem>("routine_checks");

        // "Remaining km before due" = target threshold - current odometer, where current
        // odometer is the taxi's live mileage from actual shift activity (pre-/post-shift
        // odometer readings), not the static TaxiUnit.CurrentMileage snapshot field, which
        // nothing keeps in sync automatically. Windowed to the last 30 days on `shiftStart`
        // (single-field query, no composite index needed) rather than a full `shifts` fetch
        // or a WhereEqualTo(taxiId)+OrderBy(shiftStart) query (which WOULD need one) - see
        // docs/ERD.md "Dashboard read-cost notes". A taxi with no shifts in that window falls
        // back to TaxiUnit.CurrentMileage, since there's no fresher data to read cheaply.
        List<ShiftLog> recentShifts = await GetSinceAsync<ShiftLog>("shifts", "shiftStart", DateTime.UtcNow.AddDays(-30));
        Dictionary<string, int> liveOdometerByTaxi = BuildLiveOdometerMap(recentShifts);

        Dictionary<string, string> driverNames = drivers.ToDictionary(d => d.UserId, d => d.FullName);
        Dictionary<string, TaxiUnit> taxiById = taxis.ToDictionary(t => t.TaxiId);

        string ReportedByName(string? driverId) =>
            !string.IsNullOrEmpty(driverId) && driverNames.TryGetValue(driverId, out string? name) ? name : "Unknown";

        List<DriverReportEntry> pending = records
            .Where(r => r.Status == "Reported")
            .OrderByDescending(r => r.PriorityLevel)
            .ThenByDescending(r => r.DateLogged)
            .Select(r => new DriverReportEntry
            {
                MaintenanceId = r.MaintenanceId,
                TaxiId = r.TaxiId,
                UnitLabel = FormatUnitLabel(r.TaxiId),
                IssueTitle = r.IssueTitle,
                IssueDescription = r.IssueDescription,
                ReportedByName = ReportedByName(r.ReportedByDriverId),
                DateLogged = r.DateLogged,
                Priority = r.PriorityLevel,
                SupportingPhotoUrl = r.SupportingPhotoUrl,
            })
            .ToList();

        List<WorkOrderEntry> active = records
            .Where(r => r.Status == "InProgress")
            .OrderBy(r => r.DateLogged)
            .Select(r => new WorkOrderEntry
            {
                MaintenanceId = r.MaintenanceId,
                TaxiId = r.TaxiId,
                UnitLabel = FormatUnitLabel(r.TaxiId),
                IssueTitle = r.IssueTitle,
                IssueDescription = r.IssueDescription,
                ReportedByName = ReportedByName(r.ReportedByDriverId),
                DateLogged = r.DateLogged,
                Priority = r.PriorityLevel,
                SupportingPhotoUrl = r.SupportingPhotoUrl,
                MechanicInstructions = r.MechanicInstructions,
                EstimatedCompletionDate = r.EstimatedCompletionDate,
            })
            .ToList();

        List<RoutineCheckEntry> upcoming = checks
            .Select(c =>
            {
                int? dueInKm = null;
                if (c.DueMileage.HasValue)
                {
                    int currentOdometer = liveOdometerByTaxi.TryGetValue(c.TaxiId, out int liveKm)
                        ? liveKm
                        : taxiById.TryGetValue(c.TaxiId, out TaxiUnit? taxi) ? taxi.CurrentMileage : 0;
                    dueInKm = Math.Max(0, c.DueMileage.Value - currentOdometer);
                }

                return new RoutineCheckEntry
                {
                    CheckId = c.CheckId,
                    TaxiId = c.TaxiId,
                    UnitLabel = FormatUnitLabel(c.TaxiId),
                    CheckName = c.CheckName,
                    DueInKm = dueInKm,
                    DueDate = c.DueDate,
                };
            })
            .OrderBy(c => c.DueDate.HasValue ? 1 : 0)
            .ThenBy(c => c.DueInKm ?? int.MaxValue)
            .ToList();

        return new GarageSnapshot { PendingReports = pending, ActiveWorkOrders = active, UpcomingChecks = upcoming };
    }

    public async Task<List<HistoryEntry>> GetHistoryAsync()
    {
        List<MaintenanceRecord> records = await GetAllAsync<MaintenanceRecord>("maintenance_logs");
        List<UserProfile> drivers = await GetAllAsync<UserProfile>("users");
        Dictionary<string, string> driverNames = drivers.ToDictionary(d => d.UserId, d => d.FullName);

        return records
            .Where(r => r.Status is "Resolved" or "Dismissed")
            .OrderByDescending(r => r.DateResolved ?? r.DateLogged)
            .Select(r => new HistoryEntry
            {
                MaintenanceId = r.MaintenanceId,
                UnitLabel = FormatUnitLabel(r.TaxiId),
                IssueTitle = r.IssueTitle,
                ReportedByName = !string.IsNullOrEmpty(r.ReportedByDriverId) && driverNames.TryGetValue(r.ReportedByDriverId, out string? name) ? name : "—",
                DateLogged = r.DateLogged,
                DateResolved = r.DateResolved,
                Status = r.Status,
            })
            .ToList();
    }

    public async Task<GarageActionResult> DismissReportAsync(string maintenanceId)
    {
        try
        {
            await Db.Collection("maintenance_logs").Document(maintenanceId).UpdateAsync("status", "Dismissed");
            return new GarageActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dismiss report {MaintenanceId}", maintenanceId);
            return new GarageActionResult { Ok = false, ErrorMessage = "Could not dismiss this report. Please try again." };
        }
    }

    public async Task<GarageActionResult> CreateTicketAsync(string maintenanceId, PriorityLevel priority, string mechanicInstructions, DateTime? estimatedCompletionDate)
    {
        try
        {
            var updates = new Dictionary<string, object>
            {
                ["status"] = "InProgress",
                ["priorityLevel"] = new PriorityLevelConverter().ToFirestore(priority),
                ["mechanicInstructions"] = mechanicInstructions,
            };
            if (estimatedCompletionDate.HasValue)
            {
                updates["estimatedCompletionDate"] = estimatedCompletionDate.Value;
            }
            await Db.Collection("maintenance_logs").Document(maintenanceId).UpdateAsync(updates);
            return new GarageActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create ticket for {MaintenanceId}", maintenanceId);
            return new GarageActionResult { Ok = false, ErrorMessage = "Could not create this ticket. Please try again." };
        }
    }

    public async Task<GarageActionResult> MarkResolvedAsync(string maintenanceId)
    {
        try
        {
            var updates = new Dictionary<string, object>
            {
                ["status"] = "Resolved",
                ["dateResolved"] = DateTime.UtcNow,
            };
            await Db.Collection("maintenance_logs").Document(maintenanceId).UpdateAsync(updates);
            return new GarageActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark {MaintenanceId} resolved", maintenanceId);
            return new GarageActionResult { Ok = false, ErrorMessage = "Could not mark this resolved. Please try again." };
        }
    }

    /// <summary>
    /// Auto-scheduling for "Schedule": starts at tomorrow (never today) and, for each
    /// candidate day, counts how many "InProgress" MAINTENANCE_RECORD rows already have
    /// that EstimatedCompletionDate - if it's at or above DailyScheduleCapacity, moves to
    /// the next day and checks again, until it finds a day under capacity.
    /// </summary>
    public async Task<DateTime> GetNextAvailableScheduleDateAsync()
    {
        List<MaintenanceRecord> records = await GetAllAsync<MaintenanceRecord>("maintenance_logs");
        Dictionary<DateTime, int> countsByDay = records
            .Where(r => r.Status == "InProgress" && r.EstimatedCompletionDate.HasValue)
            .GroupBy(r => r.EstimatedCompletionDate!.Value.Date)
            .ToDictionary(g => g.Key, g => g.Count());

        DateTime candidate = DateTime.UtcNow.Date.AddDays(1);
        while (countsByDay.TryGetValue(candidate, out int countThatDay) && countThatDay >= DailyScheduleCapacity)
        {
            candidate = candidate.AddDays(1);
        }

        return candidate;
    }

    /// <summary>Turns an upcoming routine check straight into an active work order (skipping
    /// the driver-report stage, since it's manager/system-initiated) and removes it from the
    /// upcoming list.</summary>
    public async Task<GarageActionResult> ScheduleRoutineCheckAsync(string checkId, DateTime scheduledDate)
    {
        try
        {
            DocumentSnapshot snapshot = await Db.Collection("routine_checks").Document(checkId).GetSnapshotAsync();
            if (!snapshot.Exists)
            {
                return new GarageActionResult { Ok = false, ErrorMessage = "This check no longer exists." };
            }

            RoutineCheckItem check = snapshot.ConvertTo<RoutineCheckItem>();
            var record = new MaintenanceRecord
            {
                TaxiId = check.TaxiId,
                MaintenanceType = MaintenanceType.RoutineCheckup,
                IssueTitle = check.CheckName,
                IssueDescription = $"Scheduled preventive maintenance: {check.CheckName}.",
                DateLogged = DateTime.UtcNow,
                Status = "InProgress",
                PriorityLevel = PriorityLevel.Low,
                EstimatedCompletionDate = scheduledDate,
            };
            await Db.Collection("maintenance_logs").AddAsync(record);
            await Db.Collection("routine_checks").Document(checkId).DeleteAsync();

            return new GarageActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to schedule routine check {CheckId}", checkId);
            return new GarageActionResult { Ok = false, ErrorMessage = "Could not schedule this check. Please try again." };
        }
    }

    /// <summary>Most recent odometer reading per taxi from a window of shifts - EndMileage
    /// (post-shift) if the latest shift has ended, else its StartMileage (pre-shift, still
    /// active). Taxis with no reading in the window are simply absent from the result.</summary>
    private static Dictionary<string, int> BuildLiveOdometerMap(List<ShiftLog> shifts)
    {
        var map = new Dictionary<string, int>();
        foreach (IGrouping<string, ShiftLog> group in shifts.GroupBy(s => s.TaxiId))
        {
            ShiftLog latest = group.OrderByDescending(s => s.ShiftStart ?? DateTime.MinValue).First();
            int reading = latest.EndMileage > 0 ? latest.EndMileage : latest.StartMileage;
            if (reading > 0)
            {
                map[group.Key] = reading;
            }
        }
        return map;
    }

    private static string FormatUnitLabel(string taxiId)
    {
        int lastUnderscore = taxiId.LastIndexOf('_');
        string suffix = lastUnderscore >= 0 ? taxiId[(lastUnderscore + 1)..] : taxiId;
        return int.TryParse(suffix, out int n) ? $"Unit {n:00}" : $"Unit {taxiId}";
    }

    private async Task<List<T>> GetAllAsync<T>(string collection) where T : class
    {
        QuerySnapshot snapshot = await Db.Collection(collection).GetSnapshotAsync();
        return ConvertDocuments<T>(snapshot, collection);
    }

    /// <summary>WhereGreaterThanOrEqualTo on a single field - auto-indexed, no composite
    /// index needed (same safe pattern used by FleetReportingService/FinancialLedgerService),
    /// unlike a WhereEqualTo(taxiId) + OrderBy(shiftStart) query would require.</summary>
    private async Task<List<T>> GetSinceAsync<T>(string collection, string dateField, DateTime sinceUtc) where T : class
    {
        QuerySnapshot snapshot = await Db.Collection(collection).WhereGreaterThanOrEqualTo(dateField, sinceUtc).GetSnapshotAsync();
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
                _logger.LogWarning(ex, "Skipping {Collection}/{DocumentId}: failed to convert to {Type}",
                    collection, doc.Id, typeof(T).Name);
            }
        }
        return results;
    }
}

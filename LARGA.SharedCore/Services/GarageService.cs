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
    /// <summary>Max jobs the shop starts on any one day (by shop day: scheduled or in progress) - used by GetNextAvailableScheduleDateAsync's auto-scheduling.</summary>
    private const int DailyScheduleCapacity = 2;

    private readonly Lazy<FirestoreDb> _dbLazy;
    private readonly ILogger<GarageService> _logger;
    private readonly InventoryAuditService _inventoryAudit;

    private FirestoreDb Db => _dbLazy.Value;

    public GarageService(Lazy<FirestoreDb> dbLazy, ILogger<GarageService> logger, InventoryAuditService inventoryAudit)
    {
        _dbLazy = dbLazy;
        _logger = logger;
        _inventoryAudit = inventoryAudit;
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

        await StartDueScheduledJobsAsync(records);

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

        WorkOrderEntry ToWorkOrder(MaintenanceRecord r) => new()
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
            ScheduledDate = r.ScheduledDate,
            IsRoutineCheck = string.IsNullOrWhiteSpace(r.ReportedByDriverId) && r.MaintenanceType == MaintenanceType.RoutineCheckup,
        };

        List<WorkOrderEntry> active = records
            .Where(r => r.Status == WorkOrderRules.InProgress)
            .Select(ToWorkOrder)
            .OrderBy(w => w.ShopDayPh)
            .ThenBy(w => w.DateLogged)
            .ToList();

        List<WorkOrderEntry> scheduled = records
            .Where(r => r.Status == WorkOrderRules.Scheduled)
            .Select(ToWorkOrder)
            .OrderBy(w => w.ShopDayPh)
            .ThenByDescending(w => w.Priority)
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

        return new GarageSnapshot { PendingReports = pending, ActiveWorkOrders = active, ScheduledWorkOrders = scheduled, UpcomingChecks = upcoming };
    }

    /// <summary>A Scheduled ticket whose shop day has come is now in the shop. Every eligibility
    /// check already treats it that way from its day (WorkOrderRules), so this only keeps the
    /// stored status tidy; a failed write is logged and retried on the next load. Updates the
    /// in-memory records too, so the page lists them under Active right away.</summary>
    private async Task StartDueScheduledJobsAsync(List<MaintenanceRecord> records)
    {
        DateTime todayPh = PhilippineTime.Now.Date;
        foreach (MaintenanceRecord record in records.Where(r =>
                     r.Status == WorkOrderRules.Scheduled && WorkOrderRules.IsInShopNow(r.Status, r.DateLogged, r.ScheduledDate, todayPh)))
        {
            try
            {
                await Db.Collection("maintenance_logs").Document(record.MaintenanceId).UpdateAsync("status", WorkOrderRules.InProgress);
                record.Status = WorkOrderRules.InProgress;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start scheduled job {MaintenanceId}", record.MaintenanceId);
            }
        }
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
            DocumentReference docRef = Db.Collection("maintenance_logs").Document(maintenanceId);
            await docRef.UpdateAsync("status", "Dismissed");
            await ReturnUnitToServiceIfDoneAsync(docRef);
            return new GarageActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dismiss report {MaintenanceId}", maintenanceId);
            return new GarageActionResult { Ok = false, ErrorMessage = "Could not dismiss this report. Please try again." };
        }
    }

    /// <summary>Turns a pending driver report into a work order booked for <paramref name="shopDate"/>
    /// (a calendar date as midnight UTC, like the date inputs give). Today goes straight into
    /// the shop (In Progress); a later day waits under Upcoming Work Orders (Scheduled).</summary>
    public async Task<GarageActionResult> CreateTicketAsync(string maintenanceId, PriorityLevel priority, string mechanicInstructions, DateTime shopDate, DateTime? estimatedCompletionDate)
    {
        DateTime todayPh = PhilippineTime.Now.Date;
        if (shopDate.Date < todayPh)
        {
            return new GarageActionResult { Ok = false, ErrorMessage = "The shop date can't be in the past." };
        }
        if (estimatedCompletionDate.HasValue && estimatedCompletionDate.Value.Date < shopDate.Date)
        {
            return new GarageActionResult { Ok = false, ErrorMessage = "The expected finish can't be before the shop date." };
        }

        try
        {
            bool startsToday = shopDate.Date == todayPh;
            var updates = new Dictionary<string, object>
            {
                ["status"] = startsToday ? WorkOrderRules.InProgress : WorkOrderRules.Scheduled,
                ["scheduledDate"] = DateTime.SpecifyKind(shopDate.Date, DateTimeKind.Utc),
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

    /// <summary>Brings a Scheduled ticket into the shop today instead of on its booked day.</summary>
    public async Task<GarageActionResult> StartScheduledNowAsync(string maintenanceId)
    {
        try
        {
            await Db.Collection("maintenance_logs").Document(maintenanceId).UpdateAsync(new Dictionary<string, object>
            {
                ["status"] = WorkOrderRules.InProgress,
                ["scheduledDate"] = DateTime.SpecifyKind(PhilippineTime.Now.Date, DateTimeKind.Utc),
            });
            return new GarageActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start {MaintenanceId} now", maintenanceId);
            return new GarageActionResult { Ok = false, ErrorMessage = "Could not start this work order. Please try again." };
        }
    }

    /// <summary>Cancels a Scheduled ticket's booking: it goes back to Pending Driver Reports, to be
    /// rebooked or dismissed. A routine-check job (no driver report behind it) goes back to Upcoming
    /// Routine Checks - ScheduleRoutineCheckAsync deleted the check when booking it, so it's
    /// recreated, due on the day it was booked for - and the job itself is dismissed.</summary>
    public async Task<GarageActionResult> UnscheduleAsync(string maintenanceId)
    {
        try
        {
            DocumentReference docRef = Db.Collection("maintenance_logs").Document(maintenanceId);
            DocumentSnapshot snapshot = await docRef.GetSnapshotAsync();
            if (!snapshot.Exists)
            {
                return new GarageActionResult { Ok = false, ErrorMessage = "This work order no longer exists." };
            }

            MaintenanceRecord record = snapshot.ConvertTo<MaintenanceRecord>();
            if (record.Status != WorkOrderRules.Scheduled)
            {
                return new GarageActionResult { Ok = false, ErrorMessage = "This work order is no longer scheduled - refresh the page." };
            }

            bool fromDriverReport = !string.IsNullOrWhiteSpace(record.ReportedByDriverId);
            if (!fromDriverReport && record.MaintenanceType == MaintenanceType.RoutineCheckup)
            {
                await Db.Collection("routine_checks").AddAsync(new RoutineCheckItem
                {
                    TaxiId = record.TaxiId,
                    CheckName = record.IssueTitle,
                    DueDate = record.ScheduledDate ?? record.EstimatedCompletionDate,
                });
            }

            await docRef.UpdateAsync(new Dictionary<string, object>
            {
                ["status"] = fromDriverReport ? WorkOrderRules.Reported : "Dismissed",
                ["scheduledDate"] = FieldValue.Delete,
                ["estimatedCompletionDate"] = FieldValue.Delete,
            });
            return new GarageActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unschedule {MaintenanceId}", maintenanceId);
            return new GarageActionResult { Ok = false, ErrorMessage = "Could not cancel this booking. Please try again." };
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
            DocumentReference docRef = Db.Collection("maintenance_logs").Document(maintenanceId);
            await docRef.UpdateAsync(updates);
            await ReturnUnitToServiceIfDoneAsync(docRef);
            return new GarageActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark {MaintenanceId} resolved", maintenanceId);
            return new GarageActionResult { Ok = false, ErrorMessage = "Could not mark this resolved. Please try again." };
        }
    }

    /// <summary>
    /// A unit is marked Under Maintenance when a clock-in is denied (web or the manager app's
    /// "Deny &amp; Send to Garage") - always alongside a driver report or work order. Once the
    /// unit's last open job is resolved or dismissed, it goes back in service; while another
    /// pending report or work order is still open, it stays in maintenance. A failure here
    /// doesn't undo the job update - it's logged, and the next close retries it.
    /// </summary>
    private async Task ReturnUnitToServiceIfDoneAsync(DocumentReference closedJob)
    {
        try
        {
            DocumentSnapshot job = await closedJob.GetSnapshotAsync();
            string? taxiId = job.Exists && job.TryGetValue("taxiId", out string? id) ? id : null;
            if (string.IsNullOrWhiteSpace(taxiId))
            {
                return;
            }

            QuerySnapshot unitJobs = await Db.Collection("maintenance_logs").WhereEqualTo("taxiId", taxiId).GetSnapshotAsync();
            bool stillOpen = unitJobs.Documents.Any(d =>
                d.Id != closedJob.Id && d.TryGetValue("status", out string? s) && TaxiStatusRules.IsOpenJob(s));
            if (stillOpen)
            {
                return;
            }

            DocumentReference taxiRef = Db.Collection("taxis").Document(taxiId);
            DocumentSnapshot taxi = await taxiRef.GetSnapshotAsync();
            if (taxi.Exists && taxi.TryGetValue("status", out string? status) && TaxiStatusRules.IsUnderMaintenance(status))
            {
                await taxiRef.UpdateAsync("status", TaxiStatusRules.ActiveUnit);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to return the unit of job {MaintenanceId} to service", closedJob.Id);
        }
    }

    /// <summary>
    /// Auto-scheduling ("Next available"): starts at tomorrow (never today) and, for each
    /// candidate day, counts the work orders already booked to start that day (Scheduled or In
    /// Progress, by shop day) - at or above DailyScheduleCapacity, it moves to the next day,
    /// until it finds one under capacity. Returned as that calendar date at midnight UTC.
    /// </summary>
    public async Task<DateTime> GetNextAvailableScheduleDateAsync()
    {
        List<MaintenanceRecord> records = await GetAllAsync<MaintenanceRecord>("maintenance_logs");
        Dictionary<DateTime, int> countsByDay = records
            .Where(r => WorkOrderRules.IsShopStatus(r.Status))
            .GroupBy(r => WorkOrderRules.ShopStartUtc(r.DateLogged, r.ScheduledDate).ToPhilippineTime().Date)
            .ToDictionary(g => g.Key, g => g.Count());

        DateTime candidate = PhilippineTime.Now.Date.AddDays(1);
        while (countsByDay.TryGetValue(candidate, out int countThatDay) && countThatDay >= DailyScheduleCapacity)
        {
            candidate = candidate.AddDays(1);
        }

        return DateTime.SpecifyKind(candidate, DateTimeKind.Utc);
    }

    /// <summary>Turns an upcoming routine check straight into a work order booked for that day
    /// (skipping the driver-report stage, since it's manager/system-initiated) and removes it
    /// from the routine-check list. A one-day job: it's expected done the same day.</summary>
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
                Status = scheduledDate.Date <= PhilippineTime.Now.Date ? WorkOrderRules.InProgress : WorkOrderRules.Scheduled,
                PriorityLevel = PriorityLevel.Low,
                ScheduledDate = DateTime.SpecifyKind(scheduledDate.Date, DateTimeKind.Utc),
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

    /// <summary>
    /// Records the spare parts a maintenance job consumed. For each part, stock is deducted through
    /// InventoryAuditService.DeductPartAsync, which also writes the InventoryStockDeducted audit
    /// entry and the maintenance_parts_used row for this ticket. If the part then sits at or below its
    /// ReorderLevel, DeductPartAsync raises the LowStock alert (the same as a manual deduction).
    /// All quantities are validated before anything is deducted. Parts are then processed in order
    /// and are not rolled back as a group: if one fails, the parts before it stay deducted and logged.
    /// </summary>
    public async Task<GarageActionResult> LogPartsUsedAsync(string maintenanceId, IReadOnlyList<(string PartId, int Quantity)> parts, string? actorUserId = null)
    {
        if (parts.Count == 0 || parts.Any(p => string.IsNullOrWhiteSpace(p.PartId) || p.Quantity <= 0))
        {
            return new GarageActionResult { Ok = false, ErrorMessage = "Each part needs a selection and a quantity above zero." };
        }

        try
        {
            foreach ((string partId, int quantity) in parts)
            {
                // DeductPartAsync writes the maintenance_parts_used row itself (in the same
                // transaction as the stock change), linked to this ticket - no second row here.
                SparePart? updated = await _inventoryAudit.DeductPartAsync(partId, quantity, actorUserId, maintenanceId: maintenanceId);
                if (updated is null)
                {
                    return new GarageActionResult { Ok = false, ErrorMessage = "One of the selected parts no longer exists." };
                }
            }

            return new GarageActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log parts used for {MaintenanceId}", maintenanceId);
            return new GarageActionResult { Ok = false, ErrorMessage = "Could not log the parts used. Please check the stock and try again." };
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.SharedCore.Alerts;
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
                TaxiId = r.TaxiId,
                UnitLabel = FormatUnitLabel(r.TaxiId),
                IssueTitle = r.IssueTitle,
                MaintenanceType = r.MaintenanceType,
                MechanicInstructions = r.MechanicInstructions,
                ReportedByName = !string.IsNullOrEmpty(r.ReportedByDriverId) && driverNames.TryGetValue(r.ReportedByDriverId, out string? name) ? name : "—",
                DateLogged = r.DateLogged,
                DateResolved = r.DateResolved,
                Status = r.Status,
            })
            .ToList();
    }

    public async Task<GarageActionResult> DismissReportAsync(string maintenanceId, string? actorUserId = null)
    {
        try
        {
            DocumentReference docRef = Db.Collection("maintenance_logs").Document(maintenanceId);
            DocumentSnapshot snapshot = await docRef.GetSnapshotAsync();
            string taxiId = snapshot.Exists && snapshot.TryGetValue("taxiId", out string? id) ? id : "unknown unit";
            string issueTitle = snapshot.Exists && snapshot.TryGetValue("issueTitle", out string? title) ? title : "report";

            // The status change and its audit entry commit together, so a dismiss can't land unlogged.
            WriteBatch batch = Db.StartBatch();
            batch.Update(docRef, new Dictionary<string, object> { ["status"] = "Dismissed" });
            batch.Create(Db.Collection("audit_logs").Document(), new AuditLog
            {
                UserId = actorUserId ?? string.Empty,
                ActionType = "MaintenanceDismissed",
                AuditLogDetails = $"Dismissed maintenance report \"{issueTitle}\" for {taxiId}.",
                Timestamp = DateTime.UtcNow,
            });
            await batch.CommitAsync();

            await ReturnUnitToServiceIfDoneAsync(docRef);
            await SubmissionAlerts.MarkReviewedAsync(Db, SubmissionAlerts.DefectAlertId(maintenanceId)); // REQ-4.3-7: reviewed
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
    public async Task<GarageActionResult> CreateTicketAsync(string maintenanceId, PriorityLevel priority, string mechanicInstructions, DateTime shopDate, DateTime? estimatedCompletionDate, string? actorUserId = null)
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
            DocumentReference docRef = Db.Collection("maintenance_logs").Document(maintenanceId);
            DocumentSnapshot snapshot = await docRef.GetSnapshotAsync();
            string taxiId = snapshot.Exists && snapshot.TryGetValue("taxiId", out string? id) ? id : "unknown unit";
            string issueTitle = snapshot.Exists && snapshot.TryGetValue("issueTitle", out string? title) ? title : "report";

            bool startsToday = shopDate.Date == todayPh;
            var updates = new Dictionary<string, object>
            {
                ["status"] = startsToday ? WorkOrderRules.InProgress : WorkOrderRules.Scheduled,
                ["scheduledDate"] = DateTime.SpecifyKind(shopDate.Date, DateTimeKind.Utc),
                ["priorityLevel"] = new PriorityLevelConverter().ToFirestore(priority),
                ["mechanicInstructions"] = mechanicInstructions,
                // Proposal §4.2.4.7: the booking message (DriverNotifier) tells the driver this day.
                ["bookedAt"] = DateTime.UtcNow,
                ["driverNotifiedShopDay"] = DateTime.SpecifyKind(shopDate.Date, DateTimeKind.Utc),
            };
            if (estimatedCompletionDate.HasValue)
            {
                updates["estimatedCompletionDate"] = estimatedCompletionDate.Value;
            }

            WriteBatch batch = Db.StartBatch();
            batch.Update(docRef, updates);
            batch.Create(Db.Collection("audit_logs").Document(), new AuditLog
            {
                UserId = actorUserId ?? string.Empty,
                ActionType = "MaintenanceTicketCreated",
                AuditLogDetails = $"Created a {priority} work order ticket for \"{issueTitle}\" on {taxiId}, shop day {shopDate:yyyy-MM-dd}.",
                Timestamp = DateTime.UtcNow,
            });
            await batch.CommitAsync();
            await SubmissionAlerts.MarkReviewedAsync(Db, SubmissionAlerts.DefectAlertId(maintenanceId)); // REQ-4.3-7: reviewed
            return new GarageActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create ticket for {MaintenanceId}", maintenanceId);
            return new GarageActionResult { Ok = false, ErrorMessage = "Could not create this ticket. Please try again." };
        }
    }

    /// <summary>Brings a Scheduled ticket into the shop today instead of on its booked day.</summary>
    public async Task<GarageActionResult> StartScheduledNowAsync(string maintenanceId, string? actorUserId = null)
    {
        try
        {
            DocumentReference docRef = Db.Collection("maintenance_logs").Document(maintenanceId);
            DocumentSnapshot snapshot = await docRef.GetSnapshotAsync();
            string taxiId = snapshot.Exists && snapshot.TryGetValue("taxiId", out string? id) ? id : "unknown unit";

            WriteBatch batch = Db.StartBatch();
            batch.Update(docRef, new Dictionary<string, object>
            {
                ["status"] = WorkOrderRules.InProgress,
                ["scheduledDate"] = DateTime.SpecifyKind(PhilippineTime.Now.Date, DateTimeKind.Utc),
            });
            batch.Create(Db.Collection("audit_logs").Document(), new AuditLog
            {
                UserId = actorUserId ?? string.Empty,
                ActionType = "MaintenanceStartedEarly",
                AuditLogDetails = $"Brought {taxiId}'s scheduled work order into the shop today instead of its booked day.",
                Timestamp = DateTime.UtcNow,
            });
            await batch.CommitAsync();
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
    public async Task<GarageActionResult> UnscheduleAsync(string maintenanceId, string? actorUserId = null)
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

            WriteBatch batch = Db.StartBatch();
            if (!fromDriverReport && record.MaintenanceType == MaintenanceType.RoutineCheckup)
            {
                batch.Create(Db.Collection("routine_checks").Document(), new RoutineCheckItem
                {
                    TaxiId = record.TaxiId,
                    CheckName = record.IssueTitle,
                    DueDate = record.ScheduledDate ?? record.EstimatedCompletionDate,
                    RoutineType = record.RoutineType,
                });
            }

            batch.Update(docRef, new Dictionary<string, object>
            {
                ["status"] = fromDriverReport ? WorkOrderRules.Reported : "Dismissed",
                ["scheduledDate"] = FieldValue.Delete,
                ["estimatedCompletionDate"] = FieldValue.Delete,
            });
            batch.Create(Db.Collection("audit_logs").Document(), new AuditLog
            {
                UserId = actorUserId ?? string.Empty,
                ActionType = "MaintenanceUnscheduled",
                AuditLogDetails = $"Unscheduled the booked work order for \"{record.IssueTitle}\" on {record.TaxiId}.",
                Timestamp = DateTime.UtcNow,
            });
            await batch.CommitAsync();
            return new GarageActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unschedule {MaintenanceId}", maintenanceId);
            return new GarageActionResult { Ok = false, ErrorMessage = "Could not cancel this booking. Please try again." };
        }
    }

    public async Task<GarageActionResult> MarkResolvedAsync(string maintenanceId, string? actorUserId = null)
    {
        try
        {
            DocumentReference docRef = Db.Collection("maintenance_logs").Document(maintenanceId);
            DocumentSnapshot snapshot = await docRef.GetSnapshotAsync();
            string taxiId = snapshot.Exists && snapshot.TryGetValue("taxiId", out string? id) ? id : "unknown unit";
            string issueTitle = snapshot.Exists && snapshot.TryGetValue("issueTitle", out string? title) ? title : "work order";

            var updates = new Dictionary<string, object>
            {
                ["status"] = "Resolved",
                ["dateResolved"] = DateTime.UtcNow,
            };

            WriteBatch batch = Db.StartBatch();
            batch.Update(docRef, updates);
            batch.Create(Db.Collection("audit_logs").Document(), new AuditLog
            {
                UserId = actorUserId ?? string.Empty,
                ActionType = "MaintenanceResolved",
                AuditLogDetails = $"Marked \"{issueTitle}\" resolved for {taxiId}.",
                Timestamp = DateTime.UtcNow,
            });
            await batch.CommitAsync();

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
    public async Task<GarageActionResult> ScheduleRoutineCheckAsync(string checkId, DateTime scheduledDate, string? actorUserId = null)
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
                RoutineType = MaintenanceSchedule.TypeOf(check.RoutineType, check.CheckName),
                DateLogged = DateTime.UtcNow,
                Status = scheduledDate.Date <= PhilippineTime.Now.Date ? WorkOrderRules.InProgress : WorkOrderRules.Scheduled,
                PriorityLevel = PriorityLevel.Low,
                ScheduledDate = DateTime.SpecifyKind(scheduledDate.Date, DateTimeKind.Utc),
                EstimatedCompletionDate = scheduledDate,
                BookedAt = DateTime.UtcNow,
                DriverNotifiedShopDay = DateTime.SpecifyKind(scheduledDate.Date, DateTimeKind.Utc),
            };

            WriteBatch batch = Db.StartBatch();
            batch.Create(Db.Collection("maintenance_logs").Document(), record);
            batch.Delete(Db.Collection("routine_checks").Document(checkId));
            batch.Create(Db.Collection("audit_logs").Document(), new AuditLog
            {
                UserId = actorUserId ?? string.Empty,
                ActionType = "RoutineCheckScheduled",
                AuditLogDetails = $"Scheduled routine check \"{check.CheckName}\" for {check.TaxiId} on {scheduledDate:yyyy-MM-dd}.",
                Timestamp = DateTime.UtcNow,
            });
            await batch.CommitAsync();

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
    /// All quantities are validated - including against current stock (INV-006) - before anything
    /// is deducted. Parts are then processed in order and are not rolled back as a group: if one
    /// fails (e.g. another manager used the stock in between), the parts before it stay deducted
    /// and logged, and the message names the part that was short.
    /// </summary>
    public async Task<GarageActionResult> LogPartsUsedAsync(string maintenanceId, IReadOnlyList<(string PartId, int Quantity)> parts, string? actorUserId = null)
    {
        if (parts.Count == 0 || parts.Any(p => string.IsNullOrWhiteSpace(p.PartId) || p.Quantity <= 0))
        {
            return new GarageActionResult { Ok = false, ErrorMessage = "Each part needs a selection and a quantity above zero." };
        }

        try
        {
            // Stock check up front, so a job that asks for more than is on hand changes nothing.
            foreach (IGrouping<string, (string PartId, int Quantity)> group in parts.GroupBy(p => p.PartId))
            {
                DocumentSnapshot partDoc = await Db.Collection("spare_parts").Document(group.Key).GetSnapshotAsync();
                if (!partDoc.Exists)
                {
                    return new GarageActionResult { Ok = false, ErrorMessage = "One of the selected parts no longer exists." };
                }

                SparePart part = partDoc.ConvertTo<SparePart>();
                int requested = group.Sum(p => p.Quantity);
                if (requested > part.StockQuantity)
                {
                    string unit = string.IsNullOrWhiteSpace(part.Unit) ? "pcs" : part.Unit;
                    return new GarageActionResult { Ok = false, ErrorMessage = $"Only {Math.Max(0, part.StockQuantity)} {unit} of {part.DisplayName} in stock." };
                }
            }

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
        catch (InventoryAuditService.InsufficientStockException ex)
        {
            return new GarageActionResult { Ok = false, ErrorMessage = ex.Message };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log parts used for {MaintenanceId}", maintenanceId);
            return new GarageActionResult { Ok = false, ErrorMessage = "Could not log the parts used. Please check the stock and try again." };
        }
    }

    // ---------------------------------------------------------------------
    // MNT-014/015/016, UAT-042: recurring checks and their due alerts
    // (run by ManagerWeb's MaintenanceSchedulerService on startup and hourly)
    // ---------------------------------------------------------------------

    /// <summary>
    /// For every active unit, makes sure the next check of each recurring type exists in
    /// routine_checks. Nothing is created while one is already waiting - generated, or entered by
    /// hand under a matching name - or booked as an open work order. Otherwise the next one is due
    /// a full interval after the type's last completed work order (dateResolved), or after today
    /// when it's never been done. IDs are {unitId}_{type}_{due:yyyyMMdd} and written with Create,
    /// so restarts and repeated runs never duplicate. Existing checks are never changed.
    /// Returns how many checks were created.
    /// </summary>
    public async Task<int> EnsureRecurringChecksAsync()
    {
        MaintenanceIntervals intervals = await GetMaintenanceIntervalsAsync();
        List<TaxiUnit> taxis = await GetAllAsync<TaxiUnit>("taxis");
        List<RoutineCheckItem> checks = await GetAllAsync<RoutineCheckItem>("routine_checks");
        List<MaintenanceRecord> records = await GetAllAsync<MaintenanceRecord>("maintenance_logs");
        Dictionary<string, int> liveOdometer = BuildLiveOdometerMap(await GetSinceAsync<ShiftLog>("shifts", "shiftStart", DateTime.UtcNow.AddDays(-30)));
        DateTime todayPh = PhilippineTime.Now.Date;

        int created = 0;
        foreach (TaxiUnit taxi in taxis.Where(IsActiveUnit))
        {
            string taxiId = string.IsNullOrWhiteSpace(taxi.TaxiId) ? taxi.DocumentId : taxi.TaxiId;
            foreach (string type in MaintenanceSchedule.RecurringTypes)
            {
                bool waiting = checks.Any(c => c.TaxiId == taxiId && MaintenanceSchedule.TypeOf(c.RoutineType, c.CheckName) == type);
                List<MaintenanceRecord> jobs = records
                    .Where(r => r.TaxiId == taxiId
                                && r.MaintenanceType == MaintenanceType.RoutineCheckup
                                && MaintenanceSchedule.TypeOf(r.RoutineType, r.IssueTitle) == type)
                    .ToList();
                bool booked = jobs.Any(r => r.DateResolved is null && !string.Equals(r.Status, "Dismissed", StringComparison.OrdinalIgnoreCase));
                if (waiting || booked)
                {
                    continue;
                }

                DateTime? lastDone = jobs.Where(r => r.DateResolved is not null).Max(r => r.DateResolved);
                DateTime baseDatePh = lastDone?.ToPhilippineTime().Date ?? todayPh;
                DateTime dueDatePh = baseDatePh.AddDays(intervals.DaysFor(type));

                int? dueMileage = null;
                if (type == MaintenanceSchedule.OilChange && intervals.OilChangeKmFor(taxiId) is int km)
                {
                    int odometer = liveOdometer.TryGetValue(taxiId, out int live) ? live : taxi.CurrentMileage;
                    dueMileage = odometer + km;
                }

                string checkId = MaintenanceSchedule.CheckId(taxiId, type, dueDatePh);
                try
                {
                    await Db.Collection("routine_checks").Document(checkId).CreateAsync(new RoutineCheckItem
                    {
                        TaxiId = taxiId,
                        CheckName = MaintenanceSchedule.DisplayName(type),
                        DueDate = DateTime.SpecifyKind(dueDatePh, DateTimeKind.Utc),
                        DueMileage = dueMileage,
                        RoutineType = type,
                    });
                    checks.Add(new RoutineCheckItem { CheckId = checkId, TaxiId = taxiId, RoutineType = type });
                    created++;
                }
                catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.AlreadyExists)
                {
                    // Another instance (or an earlier run) made it already.
                }
            }
        }

        return created;
    }

    /// <summary>
    /// Raises a bell alert for every waiting routine check that's due within the DueSoon window
    /// (MaintenanceDueSoon) or past due (MaintenanceOverdue - by date, or by the unit's odometer for
    /// a mileage-based check). One alert per check per status (deterministic ID): an alert that
    /// already exists is left alone, so it's never raised twice. Returns how many were raised.
    /// </summary>
    /// <summary>A Scheduled work order whose driver needs a day-off notice today (DayOffReminderRules).</summary>
    public record DayOffNoticeDue(string MaintenanceId, string TaxiId, string IssueTitle, DateTime ShopDay,
        DateTime? EstimatedCompletionDate, DayOffNotice Notice);

    /// <summary>Proposal §4.2.4.7: the Scheduled work orders whose unit's driver should be told about the
    /// shop day today - a week-ahead reminder, or a changed date.</summary>
    public async Task<List<DayOffNoticeDue>> GetDayOffNoticesDueAsync()
    {
        DateTime todayPh = PhilippineTime.Now.Date;
        QuerySnapshot scheduled = await Db.Collection("maintenance_logs")
            .WhereEqualTo("status", WorkOrderRules.Scheduled)
            .GetSnapshotAsync();

        var due = new List<DayOffNoticeDue>();
        foreach (DocumentSnapshot doc in scheduled.Documents)
        {
            MaintenanceRecord record = doc.ConvertTo<MaintenanceRecord>();
            if (record.ScheduledDate is not DateTime shopDay || string.IsNullOrWhiteSpace(record.TaxiId))
            {
                continue;
            }

            DayOffNotice notice = DayOffReminderRules.Decide(shopDay, todayPh, record.BookedAt?.ToPhilippineTime(),
                record.DriverNotifiedShopDay, record.DayOffReminderSentFor);
            if (notice != DayOffNotice.None)
            {
                due.Add(new DayOffNoticeDue(doc.Id, record.TaxiId, record.IssueTitle, shopDay.Date,
                    record.EstimatedCompletionDate, notice));
            }
        }
        return due;
    }

    /// <summary>Records that the driver was told about <paramref name="shopDay"/> (and, within the
    /// week, that this counts as the reminder), so the notice isn't repeated.</summary>
    public async Task MarkDayOffNoticeSentAsync(string maintenanceId, DateTime shopDay)
    {
        DateTime day = DateTime.SpecifyKind(shopDay.Date, DateTimeKind.Utc);
        var updates = new Dictionary<string, object> { ["driverNotifiedShopDay"] = day };
        if ((shopDay.Date - PhilippineTime.Now.Date).TotalDays <= DayOffReminderRules.DaysAhead)
        {
            updates["dayOffReminderSentFor"] = day;
        }
        await Db.Collection("maintenance_logs").Document(maintenanceId).UpdateAsync(updates);
    }

    public async Task<int> RaiseRoutineCheckAlertsAsync()
    {
        MaintenanceIntervals intervals = await GetMaintenanceIntervalsAsync();
        List<RoutineCheckItem> checks = await GetAllAsync<RoutineCheckItem>("routine_checks");
        List<TaxiUnit> taxis = await GetAllAsync<TaxiUnit>("taxis");
        Dictionary<string, int> liveOdometer = BuildLiveOdometerMap(await GetSinceAsync<ShiftLog>("shifts", "shiftStart", DateTime.UtcNow.AddDays(-30)));
        DateTime todayPh = PhilippineTime.Now.Date;

        var wanted = new List<(DocumentReference Ref, SystemAlert Alert)>();
        foreach (RoutineCheckItem check in checks)
        {
            string unit = FormatUnitLabel(check.TaxiId);
            DateTime? dueDatePh = check.DueDate?.Date; // stored as the Philippine calendar date
            int odometer = liveOdometer.TryGetValue(check.TaxiId, out int live) ? live
                : taxis.FirstOrDefault(t => t.TaxiId == check.TaxiId || t.DocumentId == check.TaxiId)?.CurrentMileage ?? 0;
            bool overdueByKm = check.DueMileage is int dueKm && odometer > 0 && odometer >= dueKm;

            string? type = null;
            string message = string.Empty;
            if ((dueDatePh is DateTime d && d < todayPh) || overdueByKm)
            {
                type = MaintenanceSchedule.OverdueAlertType;
                message = dueDatePh is DateTime due && due < todayPh
                    ? $"{check.CheckName} for {unit} is overdue (was due {due:MMM d})."
                    : $"{check.CheckName} for {unit} is overdue (due at {check.DueMileage:N0} km, odometer {odometer:N0} km).";
            }
            else if (dueDatePh is DateTime soon && soon <= todayPh.AddDays(intervals.DueSoonDays))
            {
                type = MaintenanceSchedule.DueSoonAlertType;
                message = soon == todayPh
                    ? $"{check.CheckName} for {unit} is due today."
                    : $"{check.CheckName} for {unit} is due on {soon:MMM d}.";
            }

            if (type is null)
            {
                continue;
            }

            wanted.Add((Db.Collection("system_alerts").Document(MaintenanceSchedule.AlertId(check.CheckId, type)), new SystemAlert
            {
                Type = type,
                TaxiId = check.TaxiId,
                UnitLabel = unit,
                Message = message,
                Timestamp = DateTime.UtcNow,
                IsRead = false,
            }));
        }

        if (wanted.Count == 0)
        {
            return 0;
        }

        IList<DocumentSnapshot> existing = await Db.GetAllSnapshotsAsync(wanted.Select(w => w.Ref));
        var existingIds = new HashSet<string>(existing.Where(s => s.Exists).Select(s => s.Id));
        int raised = 0;
        foreach ((DocumentReference docRef, SystemAlert alert) in wanted.Where(w => !existingIds.Contains(w.Ref.Id)))
        {
            try
            {
                await docRef.CreateAsync(alert);
                raised++;
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.AlreadyExists)
            {
                // Raised in the meantime.
            }
        }
        return raised;
    }

    private async Task<MaintenanceIntervals> GetMaintenanceIntervalsAsync()
    {
        try
        {
            DocumentSnapshot config = await Db.Collection("system_configs").Document("global").GetSnapshotAsync();
            return MaintenanceIntervals.From(config.Exists ? config.ConvertTo<SystemConfig>() : null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Maintenance intervals unreadable - using the defaults");
            return MaintenanceIntervals.From(null);
        }
    }

    // Every unit in the fleet gets recurring checks unless it's been taken out of service.
    private static bool IsActiveUnit(TaxiUnit taxi)
    {
        string status = (taxi.Status ?? string.Empty).ToLowerInvariant();
        return !(status.Contains("retired") || status.Contains("decommission") || status.Contains("inactive") || status.Contains("sold"));
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
        PerfLog.Reads(collection, snapshot.Count);
        return ConvertDocuments<T>(snapshot, collection);
    }

    /// <summary>WhereGreaterThanOrEqualTo on a single field - auto-indexed, no composite
    /// index needed (same safe pattern used by FleetReportingService/FinancialLedgerService),
    /// unlike a WhereEqualTo(taxiId) + OrderBy(shiftStart) query would require.</summary>
    private async Task<List<T>> GetSinceAsync<T>(string collection, string dateField, DateTime sinceUtc) where T : class
    {
        QuerySnapshot snapshot = await Db.Collection(collection).WhereGreaterThanOrEqualTo(dateField, sinceUtc).GetSnapshotAsync();
        PerfLog.Reads(collection, snapshot.Count);
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

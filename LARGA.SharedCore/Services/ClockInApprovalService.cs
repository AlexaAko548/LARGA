using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Alerts;
using LARGA.SharedCore.Models.DriverShifts;
using Microsoft.Extensions.Logging;

namespace LARGA.SharedCore.Services;

/// <summary>
/// The manager's side of a flagged pre-shift inspection (paper Ch. IV, driver clock-in
/// process): "the manager is to examine the reported issue and the submitted photos to
/// determine whether or not the driver is eligible for their work shift ... if approved ...
/// the system proceeds with clock-in as normal ...; whereas if the manager determines the unit
/// requires immediate attention before deployment, clock-in is denied and the concern is
/// escalated into the vehicle maintenance reporting process for repair."
///
/// The driver's phone creates the clockin_requests document and clocks in itself once it sees
/// "Approved" (see the mobile ClockInPendingViewModel), so every clock-in still goes through
/// the same ShiftRules checks.
/// </summary>
public class ClockInApprovalService
{
    public const string PendingAlertType = "ClockInApproval";

    private readonly Lazy<FirestoreDb> _dbLazy;
    private readonly ILogger<ClockInApprovalService> _logger;

    private FirestoreDb Db => _dbLazy.Value;

    public ClockInApprovalService(Lazy<FirestoreDb> dbLazy, ILogger<ClockInApprovalService> logger)
    {
        _dbLazy = dbLazy;
        _logger = logger;
    }

    /// <summary>Requests waiting for a decision, oldest first, with the defects the driver reported.</summary>
    public async Task<List<ClockInApprovalItem>> GetPendingAsync()
    {
        QuerySnapshot pending = await Db.Collection("clockin_requests").WhereEqualTo("status", ClockInRequest.Pending).GetSnapshotAsync();
        if (pending.Count == 0)
        {
            return new List<ClockInApprovalItem>();
        }

        Dictionary<string, string> names = (await Db.Collection("users").GetSnapshotAsync()).Documents
            .ToDictionary(d => d.Id, d => d.TryGetValue("fullName", out string name) ? name : d.Id);

        var items = new List<ClockInApprovalItem>();
        foreach (DocumentSnapshot doc in pending.Documents)
        {
            ClockInRequest request;
            try
            {
                request = doc.ConvertTo<ClockInRequest>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping clockin_requests/{Id}: unreadable", doc.Id);
                continue;
            }

            items.Add(new ClockInApprovalItem
            {
                RequestId = request.RequestId,
                DriverId = request.DriverId,
                DriverName = names.TryGetValue(request.DriverId, out string? n) ? n : request.DriverId,
                TaxiId = request.TaxiId,
                CreatedAt = request.CreatedAt,
                FlagReasons = request.FlagReasons.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                HasFailedItems = HasFailedItems(request),
                IsBelowHalfTank = request.IsBelowHalfTank,
                StartMileage = request.StartMileage,
                FuelPhotoUrl = string.IsNullOrWhiteSpace(request.FuelDashboardUrl) ? null : request.FuelDashboardUrl,
                OdometerPhotoUrl = string.IsNullOrWhiteSpace(request.OdometerPhotoUrl) ? null : request.OdometerPhotoUrl,
                Defects = await GetDefectsAsync(request),
                BlockedReason = await GetBlockReasonAsync(request.DriverId, request.TaxiId),
            });
        }

        return items.OrderBy(i => i.CreatedAt).ToList();
    }

    /// <summary>"Approved despite the flagged concern": the driver's phone clocks in.</summary>
    public async Task<ClockInDecisionResult> ApproveAsync(string requestId, string? note, string? actorUserId = null)
    {
        try
        {
            DocumentReference docRef = Db.Collection("clockin_requests").Document(requestId);
            ClockInRequest? request = await GetPendingRequestAsync(docRef);
            if (request is null)
            {
                return new ClockInDecisionResult { Ok = false, ErrorMessage = "This request was already decided or withdrawn." };
            }

            // No license, no shift (LAR-97); no shift on a unit under maintenance (LAR-98).
            if (await GetBlockReasonAsync(request.DriverId, request.TaxiId) is string blocked)
            {
                return new ClockInDecisionResult { Ok = false, ErrorMessage = $"Can't approve: {blocked}. Deny it instead, or fix that first." };
            }

            string driverName = await GetDriverNameAsync(request.DriverId);

            // One transaction: the decision, its audit entry and its bell row land together, and only
            // while the request is still Pending - a manager deciding the same request on the phone at
            // the same moment can't be overwritten.
            bool decided = await Db.RunTransactionAsync(async transaction =>
            {
                DocumentSnapshot current = await transaction.GetSnapshotAsync(docRef);
                DocumentSnapshot bell = await transaction.GetSnapshotAsync(BellRef(requestId));
                if (!IsPending(current))
                {
                    return false;
                }

                transaction.Update(docRef, new Dictionary<string, object>
                {
                    ["status"] = ClockInRequest.Approved,
                    ["decidedAt"] = DateTime.UtcNow,
                    ["managerNote"] = note?.Trim() ?? string.Empty,
                });
                transaction.Create(Db.Collection("audit_logs").Document(), new AuditLog
                {
                    UserId = actorUserId ?? string.Empty,
                    ActionType = "ClockInApproved",
                    AuditLogDetails = string.IsNullOrWhiteSpace(note)
                        ? $"Approved clock-in for driver {driverName} on {request.TaxiId}."
                        : $"Approved clock-in for driver {driverName} on {request.TaxiId}: {note.Trim()}",
                    Timestamp = DateTime.UtcNow,
                });
                if (bell.Exists)
                {
                    transaction.Update(bell.Reference, "isRead", true);
                }
                return true;
            });

            return decided
                ? new ClockInDecisionResult { Ok = true }
                : new ClockInDecisionResult { Ok = false, ErrorMessage = "This request was already decided or withdrawn." };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to approve clock-in request {Id}", requestId);
            return new ClockInDecisionResult { Ok = false, ErrorMessage = "Could not save the approval. Please try again." };
        }
    }

    /// <summary>
    /// Clock-in denied; the concern is escalated to maintenance: defects the driver already
    /// reported are in the Garage queue (raised to High priority); failed checklist items with
    /// no report get one created. Optionally marks the unit Under Maintenance, which the Shift
    /// Scheduler shows (and offers a substitute unit for).
    /// </summary>
    public async Task<ClockInDecisionResult> DenyAsync(string requestId, string note, bool markUnitUnderMaintenance, string? actorUserId = null)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return new ClockInDecisionResult { Ok = false, ErrorMessage = "Tell the driver why - a note is required when denying." };
        }

        try
        {
            DocumentReference docRef = Db.Collection("clockin_requests").Document(requestId);
            ClockInRequest? request = await GetPendingRequestAsync(docRef);
            if (request is null)
            {
                return new ClockInDecisionResult { Ok = false, ErrorMessage = "This request was already decided or withdrawn." };
            }

            string driverName = await GetDriverNameAsync(request.DriverId);

            // Every write of the denial in one transaction (gap #13, same as the phone's Alert Center):
            // a failure part-way can no longer leave the unit Under Maintenance with the request still
            // Pending, or the reverse. Only while the request is still Pending.
            bool decided = await Db.RunTransactionAsync(async transaction =>
            {
                DocumentSnapshot current = await transaction.GetSnapshotAsync(docRef);
                DocumentSnapshot bell = await transaction.GetSnapshotAsync(BellRef(requestId));
                if (!IsPending(current))
                {
                    return false;
                }

                ClockInRequest pending = current.ConvertTo<ClockInRequest>();
                List<DocumentReference> defectRefs = DefectIds(pending)
                    .Select(id => Db.Collection("maintenance_logs").Document(id))
                    .ToList();
                // A listed report that no longer exists is skipped - updating a missing document would
                // fail the whole denial.
                List<DocumentSnapshot> defects = defectRefs.Count == 0
                    ? new List<DocumentSnapshot>()
                    : (await transaction.GetAllSnapshotsAsync(defectRefs)).Where(d => d.Exists).ToList();

                DateTime now = DateTime.UtcNow;
                foreach (DocumentSnapshot defect in defects)
                {
                    transaction.Update(defect.Reference, new Dictionary<string, object>
                    {
                        ["priorityLevel"] = new PriorityLevelConverter().ToFirestore(PriorityLevel.High),
                    });
                }

                if (defects.Count == 0 && HasFailedItems(pending))
                {
                    transaction.Create(Db.Collection("maintenance_logs").Document(), new MaintenanceRecord
                    {
                        TaxiId = pending.TaxiId,
                        MaintenanceType = MaintenanceType.BreakdownRepair,
                        IssueTitle = "Failed pre-shift inspection",
                        IssueDescription = $"Clock-in denied. Flagged: {pending.FlagReasons}. Manager: {note.Trim()}",
                        DateLogged = now,
                        PriorityLevel = PriorityLevel.High,
                        SupportingPhotoUrl = string.IsNullOrWhiteSpace(pending.OdometerPhotoUrl) ? null : pending.OdometerPhotoUrl,
                        ReportedByDriverId = pending.DriverId,
                        Status = "Reported",
                    });
                }

                if (markUnitUnderMaintenance && !string.IsNullOrWhiteSpace(pending.TaxiId))
                {
                    transaction.Update(Db.Collection("taxis").Document(pending.TaxiId), "status", TaxiStatusRules.UnderMaintenance);
                }

                transaction.Update(docRef, new Dictionary<string, object>
                {
                    ["status"] = ClockInRequest.Denied,
                    ["decidedAt"] = now,
                    ["managerNote"] = note.Trim(),
                });
                transaction.Create(Db.Collection("audit_logs").Document(), new AuditLog
                {
                    UserId = actorUserId ?? string.Empty,
                    ActionType = "ClockInDenied",
                    AuditLogDetails = $"Denied clock-in for driver {driverName} on {pending.TaxiId}: {note.Trim()}",
                    Timestamp = now,
                });
                if (bell.Exists)
                {
                    transaction.Update(bell.Reference, "isRead", true);
                }
                return true;
            });

            return decided
                ? new ClockInDecisionResult { Ok = true }
                : new ClockInDecisionResult { Ok = false, ErrorMessage = "This request was already decided or withdrawn." };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deny clock-in request {Id}", requestId);
            return new ClockInDecisionResult { Ok = false, ErrorMessage = "Could not save the decision. Please try again." };
        }
    }

    /// <summary>Run by ManagerWeb's background monitor: one bell alert per waiting request.</summary>
    public async Task RaisePendingAlertsAsync()
    {
        List<ClockInApprovalItem> pending = await GetPendingAsync();
        foreach (ClockInApprovalItem item in pending)
        {
            DocumentReference alertRef = BellRef(item.RequestId);
            if ((await alertRef.GetSnapshotAsync()).Exists)
            {
                continue;
            }

            await alertRef.SetAsync(new SystemAlert
            {
                Type = PendingAlertType,
                DriverId = item.DriverId,
                DriverName = item.DriverName,
                TaxiId = item.TaxiId,
                UnitLabel = item.TaxiId,
                Message = $"{item.DriverName}'s pre-shift inspection of {item.TaxiId} was flagged ({string.Join(", ", item.FlagReasons)}). Approve or deny their clock-in in Driver & Shifts.",
                Timestamp = DateTime.UtcNow,
                IsRead = false,
            });
        }
    }

    /// <summary>Why this driver can't start a shift on this unit today (ShiftEligibilityRules), or null.</summary>
    private async Task<string?> GetBlockReasonAsync(string driverId, string taxiId)
    {
        DateTime todayPh = PhilippineTime.Now.Date;

        DocumentSnapshot driverDoc = await Db.Collection("users").Document(driverId).GetSnapshotAsync();
        DateTime? licenseExpiry = driverDoc.Exists ? driverDoc.ConvertTo<UserProfile>().LicenseExpiryDate : null;
        if (ShiftEligibilityRules.LicenseBlockReason(licenseExpiry, todayPh) is string licenseReason)
        {
            return licenseReason;
        }

        if (string.IsNullOrWhiteSpace(taxiId))
        {
            return null;
        }

        DocumentSnapshot taxiDoc = await Db.Collection("taxis").Document(taxiId).GetSnapshotAsync();
        string? taxiStatus = taxiDoc.Exists && taxiDoc.TryGetValue("status", out string? s) ? s : null;
        QuerySnapshot jobs = await Db.Collection("maintenance_logs").WhereEqualTo("taxiId", taxiId).GetSnapshotAsync();
        var unitJobs = jobs.Documents
            .Select(d => { try { return d.ConvertTo<MaintenanceRecord>(); } catch { return null; } })
            .OfType<MaintenanceRecord>()
            .Select(m => ((string?)m.Status, WorkOrderRules.ShopStartUtc(m.DateLogged, m.ScheduledDate), m.EstimatedCompletionDate, (string?)m.IssueTitle));
        return ShiftEligibilityRules.UnitBlockReason(taxiId, taxiStatus, unitJobs, todayPh);
    }

    /// <summary>Driver's display name for an audit log entry, falling back to their id if the profile is missing.</summary>
    private async Task<string> GetDriverNameAsync(string driverId)
    {
        DocumentSnapshot doc = await Db.Collection("users").Document(driverId).GetSnapshotAsync();
        return doc.Exists && doc.TryGetValue("fullName", out string? name) && !string.IsNullOrWhiteSpace(name) ? name : driverId;
    }

    private static async Task<ClockInRequest?> GetPendingRequestAsync(DocumentReference docRef)
    {
        DocumentSnapshot snapshot = await docRef.GetSnapshotAsync();
        return IsPending(snapshot) ? snapshot.ConvertTo<ClockInRequest>() : null;
    }

    private static bool IsPending(DocumentSnapshot snapshot) =>
        snapshot.Exists && snapshot.ConvertTo<ClockInRequest>().Status == ClockInRequest.Pending;

    // The request's bell row (system_alerts), raised by RaisePendingAlertsAsync.
    private DocumentReference BellRef(string requestId) => Db.Collection("system_alerts").Document(AlertFeed.ClockInBellId(requestId));

    private async Task<List<ClockInDefect>> GetDefectsAsync(ClockInRequest request)
    {
        var defects = new List<ClockInDefect>();
        foreach (string id in DefectIds(request))
        {
            try
            {
                DocumentSnapshot doc = await Db.Collection("maintenance_logs").Document(id).GetSnapshotAsync();
                if (!doc.Exists)
                {
                    continue;
                }

                MaintenanceRecord record = doc.ConvertTo<MaintenanceRecord>();
                defects.Add(new ClockInDefect
                {
                    Title = record.IssueTitle,
                    Description = record.IssueDescription,
                    Priority = record.PriorityLevel.ToString(),
                    PhotoUrl = string.IsNullOrWhiteSpace(record.SupportingPhotoUrl) ? null : record.SupportingPhotoUrl,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't read defect report {Id} for clock-in request {RequestId}", id, request.RequestId);
            }
        }
        return defects;
    }

    private static List<string> DefectIds(ClockInRequest request) =>
        request.DefectReportIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool HasFailedItems(ClockInRequest request) =>
        !(request.TireCondition && request.UnderTheHood && request.LightsCondition && request.InteriorCleanliness && request.ExteriorCondition);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
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
    public async Task<ClockInDecisionResult> ApproveAsync(string requestId, string? note)
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

            await docRef.UpdateAsync(new Dictionary<string, object>
            {
                ["status"] = ClockInRequest.Approved,
                ["decidedAt"] = DateTime.UtcNow,
                ["managerNote"] = note?.Trim() ?? string.Empty,
            });
            await MarkAlertReadAsync(requestId);
            return new ClockInDecisionResult { Ok = true };
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
    public async Task<ClockInDecisionResult> DenyAsync(string requestId, string note, bool markUnitUnderMaintenance)
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

            DateTime now = DateTime.UtcNow;
            List<string> defectIds = DefectIds(request);
            foreach (string defectId in defectIds)
            {
                await Db.Collection("maintenance_logs").Document(defectId).UpdateAsync(new Dictionary<string, object>
                {
                    ["priorityLevel"] = new PriorityLevelConverter().ToFirestore(PriorityLevel.High),
                });
            }

            if (defectIds.Count == 0 && HasFailedItems(request))
            {
                await Db.Collection("maintenance_logs").AddAsync(new MaintenanceRecord
                {
                    TaxiId = request.TaxiId,
                    MaintenanceType = MaintenanceType.BreakdownRepair,
                    IssueTitle = "Failed pre-shift inspection",
                    IssueDescription = $"Clock-in denied. Flagged: {request.FlagReasons}. Manager: {note.Trim()}",
                    DateLogged = now,
                    PriorityLevel = PriorityLevel.High,
                    SupportingPhotoUrl = string.IsNullOrWhiteSpace(request.OdometerPhotoUrl) ? null : request.OdometerPhotoUrl,
                    ReportedByDriverId = request.DriverId,
                    Status = "Reported",
                });
            }

            if (markUnitUnderMaintenance && !string.IsNullOrWhiteSpace(request.TaxiId))
            {
                await Db.Collection("taxis").Document(request.TaxiId).UpdateAsync("status", TaxiStatusRules.UnderMaintenance);
            }

            await docRef.UpdateAsync(new Dictionary<string, object>
            {
                ["status"] = ClockInRequest.Denied,
                ["decidedAt"] = now,
                ["managerNote"] = note.Trim(),
            });
            await MarkAlertReadAsync(requestId);
            return new ClockInDecisionResult { Ok = true };
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
            DocumentReference alertRef = Db.Collection("system_alerts").Document($"{item.RequestId}_CLOCKIN");
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

    private static async Task<ClockInRequest?> GetPendingRequestAsync(DocumentReference docRef)
    {
        DocumentSnapshot snapshot = await docRef.GetSnapshotAsync();
        if (!snapshot.Exists)
        {
            return null;
        }

        ClockInRequest request = snapshot.ConvertTo<ClockInRequest>();
        return request.Status == ClockInRequest.Pending ? request : null;
    }

    private async Task MarkAlertReadAsync(string requestId)
    {
        DocumentReference alertRef = Db.Collection("system_alerts").Document($"{requestId}_CLOCKIN");
        if ((await alertRef.GetSnapshotAsync()).Exists)
        {
            await alertRef.UpdateAsync("isRead", true);
        }
    }

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

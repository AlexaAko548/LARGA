using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.SharedCore.Alerts;
using LARGA.SharedCore.Models.Alerts;
using LARGA.Shared.Models.Entities;
using Microsoft.Extensions.Logging;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Reads/writes the `system_alerts` collection for ManagerWeb - the notification bell shown
/// on every page header, and the source IdleAlertMonitorService creates "driver idle" alerts
/// into. Same Lazy&lt;FirestoreDb&gt; admin-SDK pattern as FleetReportingService/
/// DriverManagementService. Mobile reads this same collection directly via the client SDK
/// (see LARGA.MobileApp.ViewModels.Manager.AlertCenterViewModel) - this service is the
/// ManagerWeb-side reader/writer only.
/// </summary>
public class AlertService
{
    private readonly Lazy<FirestoreDb> _dbLazy;
    private readonly ILogger<AlertService> _logger;

    private FirestoreDb Db => _dbLazy.Value;

    public AlertService(Lazy<FirestoreDb> dbLazy, ILogger<AlertService> logger)
    {
        _dbLazy = dbLazy;
        _logger = logger;
    }

    /// <summary>The newest <see cref="AlertFeed.Limit"/> alerts - the same query the mobile Alert Center listens to
    /// (system_alerts by timestamp, newest first), so both show the same rows and unread count.</summary>
    public async Task<List<AlertNotice>> GetRecentAlertsAsync()
    {
        QuerySnapshot snapshot = await Db.Collection(AlertFeed.Collection)
            .OrderByDescending(AlertFeed.TimestampField)
            .Limit(AlertFeed.Limit)
            .GetSnapshotAsync();

        var alerts = new List<SystemAlert>(snapshot.Documents.Count);
        foreach (DocumentSnapshot doc in snapshot.Documents)
        {
            try
            {
                alerts.Add(doc.ConvertTo<SystemAlert>());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping system_alerts/{DocumentId}: failed to convert", doc.Id);
            }
        }

        return AlertFeed.Recent(alerts.Select(a => new AlertNotice
            {
                AlertId = a.AlertId,
                Type = a.Type,
                Message = a.Message,
                DriverName = a.DriverName,
                UnitLabel = a.UnitLabel,
                Timestamp = a.Timestamp,
                IsRead = a.IsRead,
            }));
    }

    public async Task MarkReadAsync(string alertId)
    {
        try
        {
            await Db.Collection("system_alerts").Document(alertId).UpdateAsync("isRead", true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark alert {AlertId} read", alertId);
        }
    }

    /// <summary>"Mark all as read" in the notification bell: every unread alert, not just the
    /// ones in the recent list, in batches of up to 500 (Firestore's batch limit). For an Assistant
    /// Manager (<paramref name="includeManagerOnly"/> false) the Manager-only rows (AlertFeed.ManagerOnly)
    /// stay unread.</summary>
    public async Task<bool> MarkAllReadAsync(bool includeManagerOnly = true)
    {
        try
        {
            QuerySnapshot unread = await Db.Collection("system_alerts").WhereEqualTo("isRead", false).GetSnapshotAsync();
            IEnumerable<DocumentSnapshot> toMark = includeManagerOnly
                ? unread.Documents
                : unread.Documents.Where(d => !AlertFeed.ManagerOnly(d.TryGetValue("type", out string? type) ? type : null));
            foreach (DocumentSnapshot[] chunk in toMark.Chunk(500))
            {
                WriteBatch batch = Db.StartBatch();
                foreach (DocumentSnapshot doc in chunk)
                {
                    batch.Update(doc.Reference, "isRead", true);
                }
                await batch.CommitAsync();
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark all alerts read");
            return false;
        }
    }

    /// <summary>Creates a "driver idle" alert, unless one already exists for this idle episode.
    /// Deterministic ID ({shiftId}_IDLE) makes this idempotent across repeated polling ticks -
    /// once raised for a shift, it won't be raised again even if that shift is still idle on
    /// the next check. Accepted simplification: a driver who starts moving and then goes idle
    /// again *within the same shift* won't get a second alert.</summary>
    public async Task<bool> CreateIdleAlertIfNeededAsync(IdleDriverInfo info)
    {
        DocumentReference docRef = Db.Collection("system_alerts").Document($"{info.ShiftId}_IDLE");

        try
        {
            DocumentSnapshot existing = await docRef.GetSnapshotAsync();
            if (existing.Exists)
            {
                return false;
            }

            var alert = new SystemAlert
            {
                Type = "DriverIdle",
                DriverId = info.DriverId,
                DriverName = info.DriverName,
                TaxiId = info.TaxiId,
                UnitLabel = info.UnitLabel,
                ShiftId = info.ShiftId,
                Message = $"{info.DriverName} ({info.UnitLabel}) has been idle for {info.IdleThresholdMinutes:0} minutes.",
                Timestamp = DateTime.UtcNow,
                IsRead = false,
            };
            await docRef.SetAsync(alert);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create idle alert for shift {ShiftId}", info.ShiftId);
            return false;
        }
    }

    public const string LowStockStatus = SparePartStockRules.LowStock;
    public const string CriticalStockStatus = SparePartStockRules.Critical;
    public const string ResolvedStatus = "Resolved";

    /// <summary>The one low-stock alert a part can have (INV-010).</summary>
    public static string LowStockAlertId(string partId) => $"lowstock_{partId}";

    /// <summary>Raises - or refreshes - the part's "LowStock" notification when its stock is at or
    /// below its minimum threshold. Called from InventoryAuditService (deductions from the Inventory
    /// page and garage jobs, and stock/minimum edits). One alert per part (lowstock_{partId}): while
    /// it's open, a further drop only updates its quantity, status (Low Stock or Critical - the Inventory
    /// page's rule, SparePartStockRules) and time, and marks it unread again. A resolved one (restocked)
    /// is reopened the same way.</summary>
    public async Task CreateLowStockAlertAsync(SparePart part)
    {
        try
        {
            (string status, string message) = LowStockStatusAndMessage(part);
            await Db.Collection("system_alerts").Document(LowStockAlertId(part.PartId)).SetAsync(new Dictionary<string, object>
            {
                ["type"] = "LowStock",
                ["partId"] = part.PartId,
                ["status"] = status,
                ["stockQuantity"] = part.StockQuantity,
                ["message"] = message,
                ["timestamp"] = DateTime.UtcNow,
                ["isRead"] = false,
            }, SetOptions.MergeAll);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create low-stock alert for part {PartId}", part.PartId);
        }
    }

    /// <summary>Brings the part's open low-stock alert in line with the Inventory page after its stock
    /// went up (a partial restock) or its minimum went down, while it's still at or below the minimum:
    /// status, quantity and message only. It stays read or unread and keeps its time, so an improvement
    /// doesn't notify anyone again. No alert, or one already resolved: nothing to do.</summary>
    public async Task RefreshLowStockAlertAsync(SparePart part)
    {
        try
        {
            DocumentReference docRef = Db.Collection("system_alerts").Document(LowStockAlertId(part.PartId));
            DocumentSnapshot existing = await docRef.GetSnapshotAsync();
            if (!existing.Exists || (existing.TryGetValue("status", out string? current) && current == ResolvedStatus))
            {
                return;
            }

            (string status, string message) = LowStockStatusAndMessage(part);
            await docRef.UpdateAsync(new Dictionary<string, object>
            {
                ["status"] = status,
                ["stockQuantity"] = part.StockQuantity,
                ["message"] = message,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh low-stock alert for part {PartId}", part.PartId);
        }
    }

    /// <summary>An open low-stock alert's status (SparePartStockRules) and message for the part's stock.</summary>
    private static (string Status, string Message) LowStockStatusAndMessage(SparePart part)
    {
        string unit = string.IsNullOrWhiteSpace(part.Unit) ? "pcs" : part.Unit;
        bool critical = SparePartStockRules.StatusOf(part) == SparePartStockRules.Critical;
        string message = part.StockQuantity <= 0
            ? $"{part.DisplayName} is out of stock (minimum {part.ReorderLevel})."
            : critical
                ? $"{part.DisplayName} is critically low: {part.StockQuantity} {unit} left (minimum {part.ReorderLevel})."
                : $"{part.DisplayName} is low on stock: {part.StockQuantity} {unit} left (minimum {part.ReorderLevel}).";
        return (critical ? CriticalStockStatus : LowStockStatus, message);
    }

    /// <summary>SRS REQ-4.5-1: an archived part isn't tracked any more, so its open low-stock alert is
    /// closed (Resolved, read). Restoring the part raises it again if it's still low.</summary>
    public async Task CloseLowStockAlertForArchivedPartAsync(SparePart part)
    {
        try
        {
            DocumentReference docRef = Db.Collection("system_alerts").Document(LowStockAlertId(part.PartId));
            DocumentSnapshot existing = await docRef.GetSnapshotAsync();
            if (!existing.Exists || (existing.TryGetValue("status", out string? status) && status == ResolvedStatus))
            {
                return;
            }

            await docRef.UpdateAsync(new Dictionary<string, object>
            {
                ["status"] = ResolvedStatus,
                ["message"] = $"{part.DisplayName} was archived - no longer tracked for low stock.",
                ["resolvedAt"] = DateTime.UtcNow,
                ["isRead"] = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close the low-stock alert for archived part {PartId}", part.PartId);
        }
    }

    /// <summary>Closes the part's open low-stock alert once it's restocked above its minimum, so the
    /// next drop reopens it (INV-010). No alert, or one already resolved: nothing to do.</summary>
    public async Task ResolveLowStockAlertAsync(SparePart part)
    {
        try
        {
            DocumentReference docRef = Db.Collection("system_alerts").Document(LowStockAlertId(part.PartId));
            DocumentSnapshot existing = await docRef.GetSnapshotAsync();
            if (!existing.Exists || (existing.TryGetValue("status", out string? status) && status == ResolvedStatus))
            {
                return;
            }

            string unit = string.IsNullOrWhiteSpace(part.Unit) ? "pcs" : part.Unit;
            await docRef.UpdateAsync(new Dictionary<string, object>
            {
                ["status"] = ResolvedStatus,
                ["stockQuantity"] = part.StockQuantity,
                ["message"] = $"{part.DisplayName} restocked: {part.StockQuantity} {unit} in stock (minimum {part.ReorderLevel}).",
                ["resolvedAt"] = DateTime.UtcNow,
                ["isRead"] = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve low-stock alert for part {PartId}", part.PartId);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
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
    // Driver-idle alerts are deduped per shift (one alert per idle episode, see
    // CreateIdleAlertIfNeededAsync), and incident-driven collections stay small over realistic
    // fleet sizes/timeframes - a capped, ordered fetch is cheap and keeps the bell simple.
    private const int RecentAlertsLimit = 50;

    private readonly Lazy<FirestoreDb> _dbLazy;
    private readonly ILogger<AlertService> _logger;

    private FirestoreDb Db => _dbLazy.Value;

    public AlertService(Lazy<FirestoreDb> dbLazy, ILogger<AlertService> logger)
    {
        _dbLazy = dbLazy;
        _logger = logger;
    }

    public async Task<List<AlertNotice>> GetRecentAlertsAsync()
    {
        List<SystemAlert> alerts = await GetAllAsync<SystemAlert>("system_alerts");
        return alerts
            .OrderByDescending(a => a.Timestamp)
            .Take(RecentAlertsLimit)
            .Select(a => new AlertNotice
            {
                AlertId = a.AlertId,
                Type = a.Type,
                Message = a.Message,
                DriverName = a.DriverName,
                UnitLabel = a.UnitLabel,
                Timestamp = a.Timestamp,
                IsRead = a.IsRead,
            })
            .ToList();
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
    /// ones in the recent list, in batches of up to 500 (Firestore's batch limit).</summary>
    public async Task<bool> MarkAllReadAsync()
    {
        try
        {
            QuerySnapshot unread = await Db.Collection("system_alerts").WhereEqualTo("isRead", false).GetSnapshotAsync();
            foreach (DocumentSnapshot[] chunk in unread.Documents.Chunk(500))
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

    public const string LowStockStatus = "Low Stock";
    public const string CriticalStockStatus = "Critical";
    public const string ResolvedStatus = "Resolved";

    /// <summary>The one low-stock alert a part can have (INV-010).</summary>
    public static string LowStockAlertId(string partId) => $"lowstock_{partId}";

    /// <summary>Raises - or refreshes - the part's "LowStock" notification when its stock is at or
    /// below its minimum threshold. Called from InventoryAuditService (deductions from the Inventory
    /// page and garage jobs, and stock/minimum edits). One alert per part (lowstock_{partId}): while
    /// it's open, a further drop only updates its quantity, status (Low Stock, or Critical at 0) and
    /// time, and marks it unread again. A resolved one (restocked) is reopened the same way.</summary>
    public async Task CreateLowStockAlertAsync(SparePart part)
    {
        try
        {
            string unit = string.IsNullOrWhiteSpace(part.Unit) ? "pcs" : part.Unit;
            bool critical = part.StockQuantity <= 0;
            await Db.Collection("system_alerts").Document(LowStockAlertId(part.PartId)).SetAsync(new Dictionary<string, object>
            {
                ["type"] = "LowStock",
                ["partId"] = part.PartId,
                ["status"] = critical ? CriticalStockStatus : LowStockStatus,
                ["stockQuantity"] = part.StockQuantity,
                ["message"] = critical
                    ? $"{part.DisplayName} is out of stock (minimum {part.ReorderLevel})."
                    : $"{part.DisplayName} is low on stock: {part.StockQuantity} {unit} left (minimum {part.ReorderLevel}).",
                ["timestamp"] = DateTime.UtcNow,
                ["isRead"] = false,
            }, SetOptions.MergeAll);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create low-stock alert for part {PartId}", part.PartId);
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

    private async Task<List<T>> GetAllAsync<T>(string collection) where T : class
    {
        QuerySnapshot snapshot = await Db.Collection(collection).GetSnapshotAsync();
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

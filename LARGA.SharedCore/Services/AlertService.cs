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

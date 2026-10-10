using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Emergency;
using Microsoft.Extensions.Logging;

namespace LARGA.SharedCore.Services;

/// <summary>An SOS as ManagerWeb's dispatch panel shows it.</summary>
public class SosAlertView
{
    public string AlertId { get; set; } = string.Empty;
    public string TriggerType { get; set; } = EmergencyAlert.Standard;
    public DateTime Timestamp { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public bool IsResolved { get; set; }

    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public bool HasLocation { get; set; }

    /// <summary>Street address from MapTiler reverse geocoding; null until it's been looked up
    /// (or when the lookup failed - the coordinates are shown then).</summary>
    public string? Address { get; set; }

    /// <summary>SOS caller validation: true/false once checked, null while pending.</summary>
    public bool? CallerVerified { get; set; }
    public string? CallerVerificationReason { get; set; }

    public string ShiftId { get; set; } = string.Empty;
    public string DriverId { get; set; } = string.Empty;
    public string DriverName { get; set; } = "Unknown driver";
    public string? PhoneNumber { get; set; }
    public string TaxiId { get; set; } = string.Empty;
    public string? PlateNumber { get; set; }
}

/// <summary>
/// ManagerWeb's SOS Dispatch &amp; Resolution panel (LAR-82): active emergency_alerts with the
/// driver, unit, text coordinates and trigger type, and resolving them. No map - the
/// coordinates are shown as text. Alerts written by older builds only carry shiftId, so the
/// driver and unit are looked up through the shift when the alert doesn't have them.
/// </summary>
public class SosDispatchService
{
    public const string SosAlertType = "SOS";

    // Resolved alerts stay listed this long, so a mis-click can be undone.
    private static readonly TimeSpan RecentlyResolvedWindow = TimeSpan.FromHours(24);

    private readonly Lazy<FirestoreDb> _dbLazy;
    private readonly MapTilerGeocoder _geocoder;
    private readonly ILogger<SosDispatchService> _logger;

    private FirestoreDb Db => _dbLazy.Value;

    public SosDispatchService(Lazy<FirestoreDb> dbLazy, MapTilerGeocoder geocoder, ILogger<SosDispatchService> logger)
    {
        _dbLazy = dbLazy;
        _geocoder = geocoder;
        _logger = logger;
    }

    /// <summary>How many SOS alerts are unresolved, leaving out ones whose caller failed
    /// validation - count aggregations, cheap enough for the every-page SOS banner to poll.</summary>
    public async Task<int> GetActiveCountAsync()
    {
        AggregateQuerySnapshot count = await Db.Collection("emergency_alerts")
            .WhereEqualTo("isResolved", false)
            .Count()
            .GetSnapshotAsync();
        AggregateQuerySnapshot unverified = await Db.Collection("emergency_alerts")
            .WhereEqualTo("isResolved", false)
            .WhereEqualTo("callerVerified", false)
            .Count()
            .GetSnapshotAsync();
        return (int)Math.Max(0, (count.Count ?? 0) - (unverified.Count ?? 0));
    }

    /// <summary>Unresolved alerts (newest first) and those resolved in the last 24 hours.</summary>
    public async Task<(List<SosAlertView> Active, List<SosAlertView> RecentlyResolved)> GetAlertsAsync()
    {
        QuerySnapshot active = await Db.Collection("emergency_alerts").WhereEqualTo("isResolved", false).GetSnapshotAsync();

        // Only alerts resolved inside the window, not every alert ever resolved (this page
        // refreshes every 10 seconds). A range on resolvedAt alone uses Firestore's automatic
        // single-field index - no composite index to deploy; isResolved is checked here instead,
        // since a reopened alert has its resolvedAt cleared anyway.
        DateTime cutoff = DateTime.UtcNow - RecentlyResolvedWindow;
        QuerySnapshot resolved = await Db.Collection("emergency_alerts").WhereGreaterThanOrEqualTo("resolvedAt", cutoff).GetSnapshotAsync();

        List<EmergencyAlert> activeAlerts = Convert(active);
        List<EmergencyAlert> recentResolved = Convert(resolved)
            .Where(a => a.IsResolved)
            .ToList();

        List<SosAlertView> views = await ToViewsAsync(activeAlerts.Concat(recentResolved).ToList());
        return (views.Where(v => !v.IsResolved).OrderByDescending(v => v.Timestamp).ToList(),
                views.Where(v => v.IsResolved).OrderByDescending(v => v.ResolvedAt).ToList());
    }

    /// <summary>The Resolved toggle (EmergencyAlert.IsResolved). Reopening clears ResolvedAt.</summary>
    public async Task SetResolvedAsync(string alertId, bool isResolved, string? actorUserId = null)
    {
        DocumentSnapshot snapshot = await Db.Collection("emergency_alerts").Document(alertId).GetSnapshotAsync();
        string who = snapshot.Exists && snapshot.TryGetValue("driverName", out string? driverName) ? driverName : "unknown driver";
        string unit = snapshot.Exists && snapshot.TryGetValue("taxiUnit", out string? taxiUnit) ? taxiUnit : "unknown unit";

        // The SOS update and its audit entry commit together, so a resolve can't land unlogged.
        DateTime now = DateTime.UtcNow;
        WriteBatch batch = Db.StartBatch();
        batch.Update(Db.Collection("emergency_alerts").Document(alertId), new Dictionary<string, object>
        {
            ["isResolved"] = isResolved,
            ["resolvedAt"] = isResolved ? (object)now : null!,
        });
        batch.Set(Db.Collection("audit_logs").Document(), new AuditLog
        {
            UserId = actorUserId ?? string.Empty,
            ActionType = isResolved ? "SosResolved" : "SosReopened",
            AuditLogDetails = $"{(isResolved ? "Resolved" : "Reopened")} SOS alert for {unit} ({who}).",
            Timestamp = now,
        });
        await batch.CommitAsync();

        if (isResolved)
        {
            DocumentReference bell = Db.Collection("system_alerts").Document($"{alertId}_SOS");
            if ((await bell.GetSnapshotAsync()).Exists)
            {
                await bell.UpdateAsync("isRead", true);
            }
        }
    }

    /// <summary>Run by ManagerWeb's background monitor: one bell alert per unresolved SOS.</summary>
    public async Task RaiseBellAlertsAsync()
    {
        (List<SosAlertView> active, _) = await GetAlertsAsync();
        foreach (SosAlertView sos in active.Where(a => a.CallerVerified != false))
        {
            DocumentReference bell = Db.Collection("system_alerts").Document($"{sos.AlertId}_SOS");
            if ((await bell.GetSnapshotAsync()).Exists)
            {
                continue;
            }

            await bell.SetAsync(new SystemAlert
            {
                Type = SosAlertType,
                DriverId = sos.DriverId,
                DriverName = sos.DriverName,
                TaxiId = sos.TaxiId,
                UnitLabel = sos.TaxiId,
                ShiftId = sos.ShiftId,
                Message = $"SOS ({TriggerLabel(sos.TriggerType)}) from {sos.DriverName} in {sos.TaxiId}"
                          + (string.IsNullOrWhiteSpace(sos.Address) ? string.Empty : $" near {sos.Address}")
                          + " - open SOS Dispatch.",
                Timestamp = DateTime.UtcNow,
                IsRead = false,
            });
        }
    }

    /// <summary>"Standard Breakdown", "Hostile Protocol" or "Crash Protocol".</summary>
    public static string TriggerLabel(string? triggerType) => EmergencyTypes.Label(triggerType);

    private List<EmergencyAlert> Convert(QuerySnapshot snapshot)
    {
        var alerts = new List<EmergencyAlert>();
        foreach (DocumentSnapshot doc in snapshot.Documents)
        {
            try
            {
                alerts.Add(doc.ConvertTo<EmergencyAlert>());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping emergency_alerts/{Id}: unreadable", doc.Id);
            }
        }
        return alerts;
    }

    private async Task<List<SosAlertView>> ToViewsAsync(List<EmergencyAlert> alerts)
    {
        if (alerts.Count == 0)
        {
            return new List<SosAlertView>();
        }

        var shiftsById = new Dictionary<string, ShiftLog>();
        foreach (string shiftId in alerts.Select(a => a.ShiftId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct())
        {
            DocumentSnapshot doc = await Db.Collection("shifts").Document(shiftId).GetSnapshotAsync();
            if (doc.Exists)
            {
                shiftsById[shiftId] = doc.ConvertTo<ShiftLog>();
            }
        }

        Dictionary<string, UserProfile> users = (await Db.Collection("users").GetSnapshotAsync()).Documents
            .Select(d => { try { return d.ConvertTo<UserProfile>(); } catch { return null; } })
            .OfType<UserProfile>()
            .ToDictionary(u => u.UserId);
        Dictionary<string, TaxiUnit> taxis = (await Db.Collection("taxis").GetSnapshotAsync()).Documents
            .Select(d => { try { return d.ConvertTo<TaxiUnit>(); } catch { return null; } })
            .OfType<TaxiUnit>()
            .ToDictionary(t => t.TaxiId);

        // Alerts from before SosPushService stored addresses (or whose lookup failed then) are
        // geocoded here; the geocoder caches, so the 10-second page refresh doesn't re-query.
        var addresses = new Dictionary<string, string?>();
        foreach (EmergencyAlert a in alerts)
        {
            addresses[a.AlertId] = await ResolveAddressAsync(a);
        }

        return alerts.Select(a =>
        {
            // The driver app (LAR-80) writes driverId/driverName and taxiUnit (the unit's plate as
            // shown on the phone); older alerts only have shiftId. The unit's ID comes from the shift.
            ShiftLog? shift = shiftsById.GetValueOrDefault(a.ShiftId);
            string driverId = !string.IsNullOrWhiteSpace(a.DriverId) ? a.DriverId : shift?.DriverId ?? string.Empty;
            string taxiId = shift?.TaxiId ?? string.Empty;
            UserProfile? driver = users.GetValueOrDefault(driverId);
            TaxiUnit? taxi = taxis.GetValueOrDefault(taxiId);

            return new SosAlertView
            {
                AlertId = a.AlertId,
                TriggerType = string.IsNullOrWhiteSpace(a.TriggerType) ? EmergencyAlert.Standard : a.TriggerType,
                Timestamp = a.Timestamp,
                ResolvedAt = a.ResolvedAt,
                IsResolved = a.IsResolved,
                Latitude = a.Latitude,
                Longitude = a.Longitude,
                HasLocation = a.Latitude != 0 || a.Longitude != 0,
                Address = addresses.GetValueOrDefault(a.AlertId),
                CallerVerified = a.CallerVerified,
                CallerVerificationReason = a.CallerVerificationReason,
                ShiftId = a.ShiftId,
                DriverId = driverId,
                DriverName = driver?.FullName ?? (string.IsNullOrWhiteSpace(a.DriverName) ? "Unknown driver" : a.DriverName),
                PhoneNumber = string.IsNullOrWhiteSpace(driver?.PhoneNumber) ? null : driver.PhoneNumber,
                TaxiId = taxiId,
                PlateNumber = !string.IsNullOrWhiteSpace(taxi?.PlateNumber) ? taxi.PlateNumber
                    : string.IsNullOrWhiteSpace(a.TaxiUnit) ? null : a.TaxiUnit,
            };
        }).ToList();
    }

    /// <summary>The alert's stored address, else a fresh MapTiler lookup (saved back to the
    /// alert, so the manager app sees it too). Null when there's no address to show.</summary>
    private async Task<string?> ResolveAddressAsync(EmergencyAlert alert)
    {
        if (alert.AddressSource == MapTilerGeocoder.SourceMapTiler && !string.IsNullOrWhiteSpace(alert.Address))
        {
            return alert.Address;
        }
        if (alert.Latitude == 0 && alert.Longitude == 0)
        {
            return null;
        }

        string? address = await _geocoder.ReverseGeocodeAsync(alert.Latitude, alert.Longitude);
        if (address is not null && !alert.IsResolved)
        {
            try
            {
                await Db.Collection("emergency_alerts").Document(alert.AlertId).UpdateAsync(new Dictionary<string, object>
                {
                    ["address"] = address,
                    ["addressSource"] = MapTilerGeocoder.SourceMapTiler,
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Couldn't save the address on emergency_alerts/{Id}", alert.AlertId);
            }
        }
        return address;
    }
}

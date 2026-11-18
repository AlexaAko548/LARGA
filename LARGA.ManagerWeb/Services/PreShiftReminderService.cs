using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.SharedCore;
using LARGA.SharedCore.Models.DriverShifts;
using LARGA.SharedCore.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// SRS REQ-4.1-9: the 5:30 AM (Philippine time) pre-shift reminder push to every driver expected to
/// work today (PreShiftReminderRules). Checks every few minutes; inside the send window it takes
/// today's Schedule Planner cell for each driver (DriverManagementService.GetWeekScheduleAsync -
/// assigned or lent unit, no DayOff, license eligible, unit not blocked), skips drivers with a shift
/// already open, claims pre_shift_reminders/{driverId}_{yyyyMMdd} with Create and only then pushes
/// (DriverNotifier), so each driver gets it once a day across restarts and instances. Same
/// hosted-service pattern as MaintenanceSchedulerService; never throws out of the host.
/// </summary>
public class PreShiftReminderService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly Lazy<FirestoreDb> _db;
    private readonly DriverManagementService _drivers;
    private readonly DriverNotifier _notifier;
    private readonly ILogger<PreShiftReminderService> _logger;
    private DateTime? _doneForDayPh;

    public PreShiftReminderService(Lazy<FirestoreDb> db, DriverManagementService drivers, DriverNotifier notifier,
        ILogger<PreShiftReminderService> logger)
    {
        _db = db;
        _drivers = drivers;
        _notifier = notifier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                DateTime nowPh = PhilippineTime.Now;
                if (PreShiftReminderRules.IsSendWindow(nowPh) && _doneForDayPh != nowPh.Date)
                {
                    // Done for the day once nothing is left to retry; otherwise the next check tries again.
                    bool retriesLeft = await SendTodaysRemindersAsync(nowPh);
                    if (!retriesLeft)
                    {
                        _doneForDayPh = nowPh.Date;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pre-shift reminders failed; retrying in {Interval}.", CheckInterval);
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Sends today's reminders that haven't gone out yet. True while a failed push may still be retried.</summary>
    private async Task<bool> SendTodaysRemindersAsync(DateTime nowPh)
    {
        DateTime todayPh = nowPh.Date;
        WeekSchedule week = await _drivers.GetWeekScheduleAsync(todayPh);

        // The message names the unit by plate (what the driver knows), falling back to the taxi ID.
        Dictionary<string, string> plates = (await _db.Value.Collection("taxis").GetSnapshotAsync()).Documents
            .Select(d => (Id: d.TryGetValue("taxiId", out string? id) ? id : d.Id,
                          Plate: d.TryGetValue("plateNumber", out string? plate) ? plate : null))
            .Where(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Plate))
            .GroupBy(t => t.Id!)
            .ToDictionary(g => g.Key, g => g.First().Plate!);

        QuerySnapshot open = await _db.Value.Collection("shifts").WhereEqualTo("status", "Active").GetSnapshotAsync();
        HashSet<string> onShift = open.Documents
            .Select(d => d.TryGetValue("driverId", out string? id) ? id : null)
            .OfType<string>()
            .ToHashSet();

        int sent = 0;
        bool retriesLeft = false;
        foreach (DriverScheduleRow row in week.Rows)
        {
            ScheduleDayCell? today = row.Days.FirstOrDefault(c => c.Date.Date == todayPh.Date);
            if (today?.TaxiId is not string taxiId || onShift.Contains(row.DriverId))
            {
                continue;
            }

            DocumentReference marker = _db.Value.Collection(PreShiftReminderRules.MarkerCollection)
                .Document(PreShiftReminderRules.MarkerId(row.DriverId, todayPh));
            int attempts;
            try
            {
                await marker.CreateAsync(new Dictionary<string, object>
                {
                    ["driverId"] = row.DriverId,
                    ["taxiId"] = taxiId,
                    ["day"] = todayPh.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["createdAt"] = DateTime.UtcNow,
                    ["attempts"] = 0,
                    ["pushed"] = false,
                    // Housekeeping only (a Firestore TTL policy on expireAt can clear old markers).
                    ["expireAt"] = DateTime.UtcNow.AddDays(30),
                });
                attempts = 0;
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.AlreadyExists)
            {
                // Already claimed today: only a failed push with tries left goes again. Markers from before
                // retries existed carry no "attempts" - treat them as one try.
                DocumentSnapshot existing = await marker.GetSnapshotAsync();
                bool alreadyPushed = existing.TryGetValue("pushed", out bool p) && p;
                attempts = existing.TryGetValue("attempts", out long a) ? (int)a : 1;
                if (!PreShiftReminderRules.ShouldRetry(alreadyPushed, attempts))
                {
                    continue;
                }
            }

            string unitLabel = PreShiftReminderRules.UnitLabel(taxiId, plates.GetValueOrDefault(taxiId));
            bool pushed = await _notifier.SendPreShiftReminderAsync(row.DriverId, taxiId, unitLabel, nowPh);
            attempts++;
            await marker.UpdateAsync(new Dictionary<string, object> { ["pushed"] = pushed, ["attempts"] = attempts });
            if (pushed) sent++;
            else if (PreShiftReminderRules.ShouldRetry(false, attempts)) retriesLeft = true;
        }

        _logger.LogInformation("Pre-shift reminders for {Day:yyyy-MM-dd}: {Sent} sent{Retry}.", todayPh, sent,
            retriesLeft ? "; failed pushes will be retried" : string.Empty);
        return retriesLeft;
    }
}

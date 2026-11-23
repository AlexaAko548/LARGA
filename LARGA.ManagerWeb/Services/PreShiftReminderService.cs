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
/// already open, claims each try on pre_shift_reminders/{driverId}_{yyyyMMdd} in a transaction and
/// only then pushes (DriverNotifier), so a try is sent by one instance only, across restarts and
/// instances (PreShiftReminderRules.CanClaim). Retry checks reuse the day's plan (schedule and plates
/// are read once a day) and only go through the drivers whose push failed. Same hosted-service
/// pattern as MaintenanceSchedulerService; never throws out of the host.
/// </summary>
public class PreShiftReminderService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly Lazy<FirestoreDb> _db;
    private readonly DriverManagementService _drivers;
    private readonly DriverNotifier _notifier;
    private readonly ILogger<PreShiftReminderService> _logger;
    private DateTime? _doneForDayPh;

    /// <summary>Today's reminders (built on the day's first check) and the drivers not finished with yet:
    /// not reached (a check that stopped partway) or due a retry.</summary>
    private DateTime? _planDayPh;
    private List<PlannedReminder> _plan = new();
    private readonly HashSet<string> _pendingDriverIds = new();

    private sealed record PlannedReminder(string DriverId, string TaxiId, string UnitLabel);

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
        if (_planDayPh != todayPh)
        {
            // The day's first check (or the first after a restart): everyone on today's schedule.
            _plan = await BuildPlanAsync(todayPh);
            _planDayPh = todayPh;
            _pendingDriverIds.Clear();
            _pendingDriverIds.UnionWith(_plan.Select(p => p.DriverId));
        }
        // Later checks only go through the drivers not finished with (a failed push, or not reached).
        List<PlannedReminder> due = _plan.Where(p => _pendingDriverIds.Contains(p.DriverId)).ToList();
        if (due.Count == 0)
        {
            return false;
        }

        QuerySnapshot open = await _db.Value.Collection("shifts").WhereEqualTo("status", "Active").GetSnapshotAsync();
        HashSet<string> onShift = open.Documents
            .Select(d => d.TryGetValue("driverId", out string? id) ? id : null)
            .OfType<string>()
            .ToHashSet();

        int sent = 0, noDevice = 0, failed = 0;
        foreach (PlannedReminder reminder in due)
        {
            if (onShift.Contains(reminder.DriverId))
            {
                _pendingDriverIds.Remove(reminder.DriverId); // already clocked in: no reminder needed
                continue;
            }

            DocumentReference marker = _db.Value.Collection(PreShiftReminderRules.MarkerCollection)
                .Document(PreShiftReminderRules.MarkerId(reminder.DriverId, todayPh));
            int? attempt = await ClaimTryAsync(marker, reminder, todayPh);
            if (attempt is not int attempts)
            {
                // Already sent, not retryable, or another instance is on it (its own retries are its job).
                _pendingDriverIds.Remove(reminder.DriverId);
                continue;
            }

            PreShiftPushOutcome outcome = await _notifier.SendPreShiftReminderAsync(reminder.DriverId, reminder.TaxiId, reminder.UnitLabel, nowPh);
            await marker.UpdateAsync(new Dictionary<string, object>
            {
                ["outcome"] = PreShiftReminderRules.OutcomeValue(outcome),
                ["outcomeAt"] = DateTime.UtcNow,
            });

            if (outcome == PreShiftPushOutcome.Sent) sent++;
            else if (outcome == PreShiftPushOutcome.NoDevice) noDevice++;
            else failed++;
            if (!PreShiftReminderRules.RetryLeft(outcome, attempts))
            {
                _pendingDriverIds.Remove(reminder.DriverId);
            }
        }

        bool retriesLeft = _pendingDriverIds.Count > 0;
        _logger.LogInformation(
            "Pre-shift reminders for {Day:yyyy-MM-dd}: {Sent} sent, {NoDevice} with no registered phone, {Failed} failed{Retry}.",
            todayPh, sent, noDevice, failed, retriesLeft ? $"; {_pendingDriverIds.Count} will be retried" : string.Empty);
        return retriesLeft;
    }

    /// <summary>Who gets a reminder today, read once a day: the Schedule Planner's cell for each driver,
    /// with the unit named by plate (what the driver knows), falling back to the taxi ID.</summary>
    private async Task<List<PlannedReminder>> BuildPlanAsync(DateTime todayPh)
    {
        WeekSchedule week = await _drivers.GetWeekScheduleAsync(todayPh);

        Dictionary<string, string> plates = (await _db.Value.Collection("taxis").GetSnapshotAsync()).Documents
            .Select(d => (Id: d.TryGetValue("taxiId", out string? id) ? id : d.Id,
                          Plate: d.TryGetValue("plateNumber", out string? plate) ? plate : null))
            .Where(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Plate))
            .GroupBy(t => t.Id!)
            .ToDictionary(g => g.Key, g => g.First().Plate!);

        var plan = new List<PlannedReminder>();
        foreach (DriverScheduleRow row in week.Rows)
        {
            ScheduleDayCell? today = row.Days.FirstOrDefault(c => c.Date.Date == todayPh.Date);
            if (today?.TaxiId is string taxiId)
            {
                plan.Add(new PlannedReminder(row.DriverId, taxiId,
                    PreShiftReminderRules.UnitLabel(taxiId, plates.GetValueOrDefault(taxiId))));
            }
        }
        return plan;
    }

    /// <summary>
    /// Claims the next try for this driver today in one transaction: creates the marker on the first try,
    /// otherwise bumps "attempts" when PreShiftReminderRules.CanClaim allows it. Returns the try's number,
    /// or null when nothing should be sent. Firestore retries the transaction if another instance touched
    /// the marker meanwhile, and that retry sees the other claim - so a try is only ever sent once.
    /// </summary>
    private Task<int?> ClaimTryAsync(DocumentReference marker, PlannedReminder reminder, DateTime todayPh) =>
        _db.Value.RunTransactionAsync(async tx =>
        {
            DocumentSnapshot snapshot = await tx.GetSnapshotAsync(marker);
            DateTime nowUtc = DateTime.UtcNow;

            if (!snapshot.Exists)
            {
                tx.Create(marker, new Dictionary<string, object>
                {
                    ["driverId"] = reminder.DriverId,
                    ["taxiId"] = reminder.TaxiId,
                    ["day"] = todayPh.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["createdAt"] = nowUtc,
                    ["attempts"] = 1,
                    ["lastClaimAt"] = nowUtc,
                    // Housekeeping only (a Firestore TTL policy on expireAt can clear old markers).
                    ["expireAt"] = nowUtc.AddDays(30),
                });
                return (int?)1;
            }

            // Markers from before claims were counted carry no "attempts" (one try) and no "lastClaimAt".
            int attempts = snapshot.TryGetValue("attempts", out long a) ? (int)a : 1;
            PreShiftPushOutcome? outcome = PreShiftReminderRules.ParseOutcome(
                snapshot.TryGetValue("outcome", out string? value) ? value : null,
                snapshot.TryGetValue("pushed", out bool pushed) ? pushed : null);
            DateTime? lastClaimUtc = snapshot.TryGetValue("lastClaimAt", out Timestamp claimed) ? claimed.ToDateTime()
                : snapshot.TryGetValue("createdAt", out Timestamp created) ? created.ToDateTime()
                : null;

            if (!PreShiftReminderRules.CanClaim(outcome, attempts, lastClaimUtc, nowUtc))
            {
                return null;
            }

            tx.Update(marker, new Dictionary<string, object>
            {
                ["attempts"] = attempts + 1,
                ["lastClaimAt"] = nowUtc,
                ["outcome"] = FieldValue.Delete,
            });
            return attempts + 1;
        });
}

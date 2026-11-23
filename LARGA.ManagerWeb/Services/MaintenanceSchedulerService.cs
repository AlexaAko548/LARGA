using System;
using System.Threading;
using System.Threading.Tasks;
using LARGA.SharedCore;
using LARGA.SharedCore.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// MNT-014/015/016, UAT-042: automatic recurring preventive maintenance. On startup and then every
/// hour, for every active unit it keeps the next Weekly inspection, Bi-weekly checkup, Monthly
/// comprehensive inspection and Oil change in routine_checks (GarageService.EnsureRecurringChecksAsync -
/// intervals from system_configs/global), then raises a DueSoon / Overdue bell alert for each check
/// that needs one (GarageService.RaiseRoutineCheckAlertsAsync). Both are idempotent (deterministic
/// IDs), so restarts and a second instance never duplicate anything. Same hosted-service pattern
/// as IdleAlertMonitorService; never throws out of the host.
/// </summary>
public class MaintenanceSchedulerService : BackgroundService
{
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(1);

    private readonly GarageService _garage;
    private readonly DriverNotifier _notifier;
    private readonly ILogger<MaintenanceSchedulerService> _logger;

    public MaintenanceSchedulerService(GarageService garage, DriverNotifier notifier, ILogger<MaintenanceSchedulerService> logger)
    {
        _garage = garage;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>Proposal §4.2.4.7: tells each Scheduled work order's driver a week before the shop day
    /// (and at once if the day moved) - DayOffReminderRules. Each notice is recorded on the work order
    /// once sent, so the hourly runs and restarts don't repeat it.</summary>
    private async Task<int> SendDayOffNoticesAsync()
    {
        int sent = 0;
        foreach (GarageService.DayOffNoticeDue due in await _garage.GetDayOffNoticesDueAsync())
        {
            bool ok = await _notifier.NotifyUnitDayOffAsync(due.TaxiId, due.ShopDay, due.EstimatedCompletionDate,
                due.IssueTitle, dateChanged: due.Notice == DayOffNotice.DateChanged);
            if (ok)
            {
                await _garage.MarkDayOffNoticeSentAsync(due.MaintenanceId, due.ShopDay);
                sent++;
            }
        }
        return sent;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int created = await _garage.EnsureRecurringChecksAsync();
                if (created > 0)
                {
                    _logger.LogInformation("Maintenance scheduler: created {Count} routine check(s).", created);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Recurring routine check generation failed");
            }

            try
            {
                int raised = await _garage.RaiseRoutineCheckAlertsAsync();
                if (raised > 0)
                {
                    _logger.LogInformation("Maintenance scheduler: raised {Count} due/overdue alert(s).", raised);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Routine check due alerts failed");
            }

            try
            {
                int notices = await SendDayOffNoticesAsync();
                if (notices > 0)
                {
                    _logger.LogInformation("Maintenance scheduler: sent {Count} Garage day-off notice(s).", notices);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Garage day-off reminders failed");
            }

            try
            {
                await Task.Delay(RunInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

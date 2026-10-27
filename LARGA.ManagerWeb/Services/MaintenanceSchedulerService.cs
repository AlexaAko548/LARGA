using System;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly ILogger<MaintenanceSchedulerService> _logger;

    public MaintenanceSchedulerService(GarageService garage, ILogger<MaintenanceSchedulerService> logger)
    {
        _garage = garage;
        _logger = logger;
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
                await Task.Delay(RunInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

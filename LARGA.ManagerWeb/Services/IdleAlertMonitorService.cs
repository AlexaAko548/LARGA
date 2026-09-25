using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LARGA.SharedCore.Models.Alerts;
using LARGA.SharedCore.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Background poller that turns "taxi is currently computed as Idle" into a standing
/// `system_alerts` row managers see on both ManagerWeb's notification bell and the mobile
/// Manager Alert Center. Runs for the lifetime of the ManagerWeb process - there's no
/// separate worker/cron infrastructure in this project, so this is the mechanism.
///
/// Polling instead of a live listener: FleetReportingService's idle computation already reads
/// several collections per call (taxis/users/shifts/emergency_alerts + a gps_telemetry lookup
/// per active shift) - running that on every GPS ping would be far more expensive than
/// checking it every few minutes. A driver crossing the idle threshold is inherently a
/// "noticed within a few minutes" event, not a "noticed within a second" one.
/// </summary>
public class IdleAlertMonitorService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(3);

    private readonly FleetReportingService _fleetReporting;
    private readonly AlertService _alertService;
    private readonly ILogger<IdleAlertMonitorService> _logger;

    public IdleAlertMonitorService(FleetReportingService fleetReporting, AlertService alertService, ILogger<IdleAlertMonitorService> logger)
    {
        _fleetReporting = fleetReporting;
        _alertService = alertService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                List<IdleDriverInfo> idleDrivers = await _fleetReporting.GetIdleDriversAsync();
                foreach (IdleDriverInfo info in idleDrivers)
                {
                    await _alertService.CreateIdleAlertIfNeededAsync(info);
                }
            }
            catch (Exception ex)
            {
                // A missing/misconfigured Firestore credential (see the Lazy<FirestoreDb>
                // comment in Program.cs) would otherwise crash this background loop
                // permanently on first tick - log and keep polling instead.
                _logger.LogWarning(ex, "Idle alert monitor tick failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }
}

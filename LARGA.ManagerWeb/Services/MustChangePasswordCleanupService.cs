using System;
using System.Threading;
using System.Threading.Tasks;
using LARGA.SharedCore.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Runs DriverManagementService.ClearReplacedTemporaryPasswordFlagsAsync on startup and then every
/// hour, so a user who already replaced their temporary password (before the app enforced
/// mustChangePassword, or through the forgot-password email) isn't kept on the forced change screen
/// until ManagerWeb next restarts. Same loop as MaintenanceSchedulerService: a failure is logged and
/// the next run retries; never throws out of the host.
/// </summary>
public class MustChangePasswordCleanupService : BackgroundService
{
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(1);

    private readonly DriverManagementService _drivers;
    private readonly ILogger<MustChangePasswordCleanupService> _logger;

    public MustChangePasswordCleanupService(DriverManagementService drivers, ILogger<MustChangePasswordCleanupService> logger)
    {
        _drivers = drivers;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int cleared = await _drivers.ClearReplacedTemporaryPasswordFlagsAsync();
                if (cleared > 0)
                {
                    _logger.LogInformation("Cleared mustChangePassword for {Count} user(s) who had already changed their password.", cleared);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "mustChangePassword cleanup failed; it will be retried in an hour.");
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

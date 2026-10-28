using System;
using System.Threading;
using System.Threading.Tasks;
using LARGA.SharedCore.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Runs DriverManagementService.ClearReplacedTemporaryPasswordFlagsAsync each time ManagerWeb starts,
/// so a user who already replaced their temporary password (before the app enforced
/// mustChangePassword, or through the forgot-password email) isn't sent to the forced change screen.
/// Same pattern as PaymentDriverIdBackfillService: a failure is logged and the next start retries.
/// </summary>
public class MustChangePasswordCleanupService : BackgroundService
{
    private readonly DriverManagementService _drivers;
    private readonly ILogger<MustChangePasswordCleanupService> _logger;

    public MustChangePasswordCleanupService(DriverManagementService drivers, ILogger<MustChangePasswordCleanupService> logger)
    {
        _drivers = drivers;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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
            _logger.LogWarning(ex, "mustChangePassword cleanup failed; it will be retried on the next start.");
        }
    }
}

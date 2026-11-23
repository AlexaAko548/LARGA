using System;
using System.Threading;
using System.Threading.Tasks;
using LARGA.SharedCore.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Runs FuelVerificationService.BackfillOrNumberClaimsAsync when ManagerWeb starts (Proposal
/// §4.2.4.5), so OR numbers on fuel logs from before the duplicate check are claimed too. It does
/// the work only once (marker document); later starts return straight away. A failure is logged and
/// never stops the site - the next start tries again. Same pattern as PaymentDriverIdBackfillService.
/// </summary>
public class FuelOrNumberBackfillService : BackgroundService
{
    private readonly FuelVerificationService _fuel;
    private readonly ILogger<FuelOrNumberBackfillService> _logger;

    public FuelOrNumberBackfillService(FuelVerificationService fuel, ILogger<FuelOrNumberBackfillService> logger)
    {
        _fuel = fuel;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            int claimed = await _fuel.BackfillOrNumberClaimsAsync();
            if (claimed > 0)
            {
                _logger.LogInformation("Claimed {Count} existing fuel OR number(s) in fuel_or_numbers.", claimed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "fuel_or_numbers backfill failed; it will be retried on the next start.");
        }
    }
}

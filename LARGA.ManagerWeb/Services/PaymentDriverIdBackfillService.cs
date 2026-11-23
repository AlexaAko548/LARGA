using System;
using System.Threading;
using System.Threading.Tasks;
using LARGA.SharedCore.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Runs FinancialLedgerService.BackfillPaymentDriverIdsAsync once each time ManagerWeb starts,
/// so boundary_payments written before payments carried a driverId stay visible to their
/// driver under firestore.rules. Runs in the background - a failure is logged and never stops
/// the site from starting; the next start simply tries again.
/// </summary>
public class PaymentDriverIdBackfillService : BackgroundService
{
    private readonly FinancialLedgerService _ledger;
    private readonly ILogger<PaymentDriverIdBackfillService> _logger;

    public PaymentDriverIdBackfillService(FinancialLedgerService ledger, ILogger<PaymentDriverIdBackfillService> logger)
    {
        _ledger = ledger;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            int updated = await _ledger.BackfillPaymentDriverIdsAsync();
            if (updated > 0)
            {
                _logger.LogInformation("Stamped driverId on {Count} boundary_payments document(s).", updated);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "boundary_payments driverId backfill failed; it will be retried on the next start.");
        }
    }
}

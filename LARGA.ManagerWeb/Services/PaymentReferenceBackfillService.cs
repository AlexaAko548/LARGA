using System;
using System.Threading;
using System.Threading.Tasks;
using LARGA.SharedCore.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Runs FinancialLedgerService.BackfillPaymentReferenceClaimsAsync when ManagerWeb starts (BRP-008,
/// consistency audit row #62), so GCash reference numbers on boundary_payments from before the
/// duplicate check existed are claimed too. It does the work only once (marker document); later
/// starts return straight away. A failure is logged and never stops the site - the next start
/// tries again. Same pattern as FuelOrNumberBackfillService.
/// </summary>
public class PaymentReferenceBackfillService : BackgroundService
{
    private readonly FinancialLedgerService _ledger;
    private readonly ILogger<PaymentReferenceBackfillService> _logger;

    public PaymentReferenceBackfillService(FinancialLedgerService ledger, ILogger<PaymentReferenceBackfillService> logger)
    {
        _ledger = ledger;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            int claimed = await _ledger.BackfillPaymentReferenceClaimsAsync();
            if (claimed > 0)
            {
                _logger.LogInformation("Claimed {Count} existing GCash reference number(s) in payment_references.", claimed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "payment_references backfill failed; it will be retried on the next start.");
        }
    }
}

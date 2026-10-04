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

    // The unpaid-debt check re-sums every driver's full shift/payment history, and a flag
    // measured in days doesn't need minute precision - so it runs every 30 minutes, not every tick.
    private static readonly TimeSpan DebtCheckInterval = TimeSpan.FromMinutes(30);
    private DateTime _lastDebtCheckUtc = DateTime.MinValue;

    private readonly FleetReportingService _fleetReporting;
    private readonly AlertService _alertService;
    private readonly ShiftDeadlineService _shiftDeadlines;
    private readonly FinancialLedgerService _ledger;
    private readonly ClockInApprovalService _clockInApprovals;
    private readonly SosDispatchService _sosDispatch;
    private readonly ILogger<IdleAlertMonitorService> _logger;

    public IdleAlertMonitorService(FleetReportingService fleetReporting, AlertService alertService, ShiftDeadlineService shiftDeadlines, FinancialLedgerService ledger, ClockInApprovalService clockInApprovals, SosDispatchService sosDispatch, ILogger<IdleAlertMonitorService> logger)
    {
        _fleetReporting = fleetReporting;
        _alertService = alertService;
        _shiftDeadlines = shiftDeadlines;
        _ledger = ledger;
        _clockInApprovals = clockInApprovals;
        _sosDispatch = sosDispatch;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // End-of-day rules first (10 PM late-return alerts, 6 AM auto-close), so a shift
            // closed as a missed clock-out isn't then reported as idle on the same tick.
            try
            {
                await _shiftDeadlines.ProcessOpenShiftsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Shift deadline check failed");
            }

            // Driver SOS -> a bell alert each (the SOS Dispatch page itself refreshes every 10 s).
            try
            {
                await _sosDispatch.RaiseBellAlertsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SOS alert check failed");
            }

            // Flagged pre-shift inspections waiting for approval -> a bell alert each.
            try
            {
                await _clockInApprovals.RaisePendingAlertsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Clock-in approval alert check failed");
            }

            if (DateTime.UtcNow - _lastDebtCheckUtc >= DebtCheckInterval)
            {
                try
                {
                    await _ledger.ProcessDebtFlagsAsync();
                    _lastDebtCheckUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unpaid debt check failed");
                }
            }

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

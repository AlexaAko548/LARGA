using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// FUEL-017 (BLM: auto-flag) and FUEL-004: the automatic pass over fuel submissions, as soon as
/// the driver app saves them. The odometer-vs-GPS comparison has to run here, not on the phone -
/// firestore.rules don't let a driver read gps_telemetry.
///
/// A live listener on fuel_logs that are Pending or Flagged, like SosPushService. Each submission
/// that still needs it (FuelVerificationService.NeedsAutoCheck) gets
/// FuelVerificationService.AutoCheckAsync: outside the system_configs/global tolerances it's set
/// Flagged with a readable reason, audited and raised in the bell; a phone-flagged one (not
/// Petron/Shell) gets its bell alert. Each log is marked once it's been handled (autoCheckedAt /
/// autoFlagAlertAt, in the same transaction), so restarts and a second instance don't repeat it,
/// and anything submitted while ManagerWeb was down is caught up on the listener's first snapshot.
/// Never throws out of the host - failures are logged and the listener is restarted.
/// </summary>
public class FuelAutoCheckService : BackgroundService
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(15);

    private readonly Lazy<FirestoreDb> _db;
    private readonly FuelVerificationService _fuel;
    private readonly ILogger<FuelAutoCheckService> _logger;

    public FuelAutoCheckService(Lazy<FirestoreDb> db, FuelVerificationService fuel, ILogger<FuelAutoCheckService> logger)
    {
        _db = db;
        _fuel = fuel;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            FirestoreChangeListener? listener = null;
            try
            {
                listener = _db.Value.Collection("fuel_logs")
                    .WhereIn("verificationStatus", new[] { "Pending", "Flagged" })
                    .Listen(OnSnapshotAsync, stoppingToken);

                // Completes only when the listener stops (cancellation) or faults.
                await listener.ListenerTask;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fuel auto-check listener stopped; restarting in {Delay}.", RestartDelay);
            }
            finally
            {
                if (listener is not null)
                {
                    try { await listener.StopAsync(); } catch { /* already stopped */ }
                }
            }

            try
            {
                await Task.Delay(RestartDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task OnSnapshotAsync(QuerySnapshot snapshot, CancellationToken token)
    {
        foreach (DocumentChange change in snapshot.Changes)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (change.ChangeType == DocumentChange.Type.Removed)
            {
                continue;
            }

            try
            {
                FuelLog log = change.Document.ConvertTo<FuelLog>();
                if (FuelVerificationService.NeedsAutoCheck(log))
                {
                    await _fuel.AutoCheckAsync(change.Document.Id);
                }
            }
            catch (Exception ex)
            {
                // One submission failing must not stop the others (or the listener).
                _logger.LogWarning(ex, "Fuel auto-check for fuel_logs/{FuelId} failed", change.Document.Id);
            }
        }
    }
}

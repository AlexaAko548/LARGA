using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.SharedCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Keeps system_configs/global.managerPhoneNumbers equal to every Manager's and Assistant
/// Manager's users.phoneNumber (ManagerPhoneAllowlist). That list is what a driver's phone
/// auto-answers after an automated SOS (LAR-86/87); drivers can't read staff users documents under
/// firestore.rules, so it's mirrored there. A live listener on users (same pattern as
/// FuelAutoCheckService): its first snapshot rebuilds the list on startup, and a new staff account,
/// a changed number or a role change updates it straight away. Written only when the list actually
/// changes. Never throws out of the host - failures are logged and the listener is restarted.
/// </summary>
public class ManagerPhoneAllowlistSyncService : BackgroundService
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(15);

    private readonly Lazy<FirestoreDb> _firestoreDb;
    private readonly ILogger<ManagerPhoneAllowlistSyncService> _logger;
    private List<string>? _lastWritten;

    public ManagerPhoneAllowlistSyncService(Lazy<FirestoreDb> firestoreDb, ILogger<ManagerPhoneAllowlistSyncService> logger)
    {
        _firestoreDb = firestoreDb;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            FirestoreChangeListener? listener = null;
            try
            {
                listener = _firestoreDb.Value.Collection("users").Listen(OnSnapshotAsync, stoppingToken);
                await listener.ListenerTask;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Manager phone allowlist listener stopped; restarting in {Delay}.", RestartDelay);
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

    private async Task OnSnapshotAsync(QuerySnapshot users, CancellationToken token)
    {
        try
        {
            List<string> numbers = ManagerPhoneAllowlist.Build(users.Documents.Select(doc => (
                doc.TryGetValue("role", out object? role) ? role?.ToString() : null,
                doc.TryGetValue("phoneNumber", out object? phone) ? phone?.ToString() : null)));

            if (_lastWritten is not null && _lastWritten.SequenceEqual(numbers))
            {
                return;
            }

            // Only this field is written, so the rest of the config document is left untouched.
            await _firestoreDb.Value.Collection("system_configs").Document("global").SetAsync(
                new Dictionary<string, object> { ["managerPhoneNumbers"] = numbers },
                SetOptions.MergeFields("managerPhoneNumbers"),
                token);
            _lastWritten = numbers;

            _logger.LogInformation("SOS auto-answer allowlist set to {Count} Manager/Assistant Manager number(s).", numbers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manager phone allowlist sync failed; the next users change retries it.");
        }
    }
}

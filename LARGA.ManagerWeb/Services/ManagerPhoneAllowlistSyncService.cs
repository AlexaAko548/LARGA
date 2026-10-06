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
/// Rebuilds system_configs/global.managerPhoneNumbers from every Manager's users.phoneNumber
/// once each time ManagerWeb starts. That list is what a driver's phone auto-answers after an
/// automated SOS (LAR-86/87); drivers can't read managers' users documents under firestore.rules,
/// so it's mirrored there. Runs in the background - a failure is logged and never stops the site
/// from starting; the next start simply tries again.
/// </summary>
public class ManagerPhoneAllowlistSyncService : BackgroundService
{
    private readonly Lazy<FirestoreDb> _firestoreDb;
    private readonly ILogger<ManagerPhoneAllowlistSyncService> _logger;

    public ManagerPhoneAllowlistSyncService(Lazy<FirestoreDb> firestoreDb, ILogger<ManagerPhoneAllowlistSyncService> logger)
    {
        _firestoreDb = firestoreDb;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            FirestoreDb db = _firestoreDb.Value;
            // The role is matched case-insensitively, the same way the apps' logins treat it.
            QuerySnapshot users = await db.Collection("users").GetSnapshotAsync(stoppingToken);

            List<string> numbers = users.Documents
                .Where(doc => doc.TryGetValue("role", out object? role)
                              && string.Equals(role?.ToString(), "Manager", StringComparison.OrdinalIgnoreCase))
                .Select(doc => doc.TryGetValue("phoneNumber", out object? phone) ? phone?.ToString() : null)
                .Select(InputValidator.NormalizePhilippineMobile)
                .OfType<string>()
                .Distinct()
                .ToList();

            // Only this field is written, so the rest of the config document is left untouched.
            await db.Collection("system_configs").Document("global").SetAsync(
                new Dictionary<string, object> { ["managerPhoneNumbers"] = numbers },
                SetOptions.MergeFields("managerPhoneNumbers"),
                stoppingToken);

            _logger.LogInformation("SOS auto-answer allowlist set to {Count} manager number(s).", numbers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manager phone allowlist sync failed; it will be retried on the next start.");
        }
    }
}

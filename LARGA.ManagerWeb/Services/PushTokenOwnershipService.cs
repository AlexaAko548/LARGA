using Google.Cloud.Firestore;
using LARGA.SharedCore.Push;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Keeps every push token on one account only. The phone writes its token under the account that
/// just signed in (users/{uid}/fcmTokens), and firestore.rules don't let it touch anyone else's
/// tokens - so this live listener does that part: when a token shows up under a second account,
/// the older copy is deleted (<see cref="PushTokenRules.Superseded"/>). Until it has, the send side
/// already skips the older copy.
///
/// On start it also removes the old single users/{uid}.fcmToken / tokenUpdatedAt fields (before
/// users/{uid}/fcmTokens), which every staff account could read. Never throws out of the host.
/// </summary>
public class PushTokenOwnershipService : BackgroundService
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(15);
    private static readonly string[] LegacyFields = { "fcmToken", "tokenUpdatedAt" };

    private readonly Lazy<FirestoreDb> _db;
    private readonly PushTokenStore _tokens;
    private readonly ILogger<PushTokenOwnershipService> _logger;

    public PushTokenOwnershipService(Lazy<FirestoreDb> db, PushTokenStore tokens, ILogger<PushTokenOwnershipService> logger)
    {
        _db = db;
        _tokens = tokens;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RemoveLegacyTokenFieldsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            FirestoreChangeListener? listener = null;
            try
            {
                listener = _db.Value.CollectionGroup(PushTokenRules.Collection).Listen(OnSnapshotAsync, stoppingToken);
                await listener.ListenerTask;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Push token listener stopped; restarting in {Delay}.", RestartDelay);
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
        if (!snapshot.Changes.Any(c => c.ChangeType != DocumentChange.Type.Removed))
        {
            return;
        }

        try
        {
            IReadOnlyList<PushTokenRules.DeviceToken> stale = PushTokenRules.Superseded(PushTokenStore.Parse(snapshot.Documents));
            if (stale.Count == 0) return;

            await _tokens.DeleteAsync(stale);
            foreach (PushTokenRules.DeviceToken t in stale)
            {
                _logger.LogInformation("Push token re-registered by another account; removed it from {Uid}.", t.Uid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Removing superseded push tokens failed; the send side skips them anyway.");
        }
    }

    private async Task RemoveLegacyTokenFieldsAsync(CancellationToken token)
    {
        try
        {
            QuerySnapshot users = await _db.Value.Collection("users").GetSnapshotAsync(token);
            int cleared = 0;
            foreach (DocumentSnapshot user in users.Documents.Where(u => LegacyFields.Any(u.ContainsField)))
            {
                await user.Reference.UpdateAsync(LegacyFields.ToDictionary(f => f, _ => (object)FieldValue.Delete), cancellationToken: token);
                cleared++;
            }
            if (cleared > 0)
            {
                _logger.LogInformation("Removed the old users.fcmToken field from {Count} account(s).", cleared);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Removing the old users.fcmToken fields failed; tried again on the next start.");
        }
    }
}

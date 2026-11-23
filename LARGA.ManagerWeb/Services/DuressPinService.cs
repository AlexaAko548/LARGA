using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// The duress PIN (system_configs/global.duressPin): one PIN shared by all drivers. Entered through
/// the "Enter PIN" link on the driver app's clock-out screen, it sends a silent Hostile SOS while
/// the clock-out carries on as normal. Edited from Manager Settings. The Admin SDK bypasses
/// firestore.rules, so the format is enforced here. Changes are audited without the PIN itself.
/// </summary>
public class DuressPinService
{
    public const int MinLength = 4;
    public const int MaxLength = 6;

    private readonly Lazy<FirestoreDb> _db;

    public DuressPinService(Lazy<FirestoreDb> db)
    {
        _db = db;
    }

    private DocumentReference Config => _db.Value.Collection("system_configs").Document("global");

    /// <summary>The current PIN, or an empty string when none is set.</summary>
    public async Task<string> GetAsync()
    {
        DocumentSnapshot snapshot = await Config.GetSnapshotAsync();
        return snapshot.Exists && snapshot.TryGetValue("duressPin", out string pin) ? pin ?? string.Empty : string.Empty;
    }

    /// <summary>Null when saved, otherwise why it wasn't.</summary>
    public async Task<string?> SetAsync(string? pin, string actorUserId)
    {
        string entry = (pin ?? string.Empty).Trim();
        if (entry.Length < MinLength || entry.Length > MaxLength || !entry.All(char.IsAsciiDigit))
        {
            return $"Use {MinLength} to {MaxLength} digits.";
        }

        WriteBatch batch = _db.Value.StartBatch();
        batch.Set(Config, new Dictionary<string, object> { ["duressPin"] = entry }, SetOptions.MergeAll);
        batch.Create(_db.Value.Collection("audit_logs").Document(), new AuditLog
        {
            UserId = actorUserId,
            ActionType = "DuressPinChanged",
            AuditLogDetails = "Changed the drivers' emergency (duress) PIN.",
            Timestamp = DateTime.UtcNow,
        });
        await batch.CommitAsync();
        return null;
    }
}

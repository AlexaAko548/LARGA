using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Ledger;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// The manager's authorized payout accounts (system_configs/global.authorizedPayoutAccounts): the
/// GCash numbers and bank accounts drivers may send boundary payments to. The Quick Ledger and the
/// Financial Ledger flag e-receipts sent anywhere else (PayoutAccountMatcher). Edited from Manager
/// Settings; every change is audited, since it decides which payments get flagged.
/// </summary>
public class PayoutAccountService
{
    public const int MaxAccountLength = 60;

    private readonly Lazy<FirestoreDb> _db;

    public PayoutAccountService(Lazy<FirestoreDb> db)
    {
        _db = db;
    }

    private DocumentReference Config => _db.Value.Collection("system_configs").Document("global");

    public async Task<List<string>> GetAsync()
    {
        DocumentSnapshot snapshot = await Config.GetSnapshotAsync();
        return snapshot.Exists ? snapshot.ConvertTo<SystemConfig>().AuthorizedPayoutAccounts ?? new List<string>() : new List<string>();
    }

    /// <summary>Null when added, otherwise why it wasn't.</summary>
    public async Task<string?> AddAsync(string? account, string actorUserId)
    {
        string entry = (account ?? string.Empty).Trim();
        string digits = PayoutAccountMatcher.CanonicalDigits(entry);
        if (entry.Length > MaxAccountLength)
        {
            return $"Keep it under {MaxAccountLength} characters.";
        }
        if (digits.Length < 6)
        {
            return "Enter a GCash number (e.g. 0917 123 4567) or a bank account number, optionally with the bank's name.";
        }

        List<string> existing = await GetAsync();
        if (existing.Any(a => PayoutAccountMatcher.CanonicalDigits(a) == digits))
        {
            return "That account is already on the list.";
        }

        await WriteAsync(FieldValue.ArrayUnion(entry), actorUserId, $"Added authorized payout account {entry}.");
        return null;
    }

    public Task RemoveAsync(string account, string actorUserId) =>
        WriteAsync(FieldValue.ArrayRemove(account), actorUserId, $"Removed authorized payout account {account}.");

    private async Task WriteAsync(object change, string actorUserId, string details)
    {
        WriteBatch batch = _db.Value.StartBatch();
        batch.Set(Config, new Dictionary<string, object> { ["authorizedPayoutAccounts"] = change }, SetOptions.MergeAll);
        batch.Set(_db.Value.Collection("audit_logs").Document(), new AuditLog
        {
            UserId = actorUserId,
            ActionType = "PayoutAccountsChanged",
            AuditLogDetails = details,
            Timestamp = DateTime.UtcNow,
        });
        await batch.CommitAsync();
    }
}

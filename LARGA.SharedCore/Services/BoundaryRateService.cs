using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Ledger;

namespace LARGA.SharedCore.Services;

/// <summary>
/// The daily boundary rate, edited from Manager Settings (system_configs/global). The rate being
/// replaced is kept in boundaryRateHistory with the time of the change, so shifts that had already
/// started stay priced as they were (BoundaryRateSchedule). The Admin SDK bypasses
/// firestore.rules, so the limits are enforced here. Every change is audited.
/// </summary>
public class BoundaryRateService
{
    public const int MinRate = 1;
    public const int MaxRate = 10_000;

    private readonly Lazy<FirestoreDb> _db;

    public BoundaryRateService(Lazy<FirestoreDb> db)
    {
        _db = db;
    }

    private DocumentReference Config => _db.Value.Collection("system_configs").Document("global");

    /// <summary>The rate new shifts are charged.</summary>
    public async Task<decimal> GetAsync() => CurrentRate(await Config.GetSnapshotAsync());

    /// <summary>Why <paramref name="rate"/> can't be the boundary rate, or null when it can.</summary>
    public static string? Validate(decimal? rate)
    {
        if (rate is not decimal value) return "Enter the new rate in pesos.";
        if (value != decimal.Truncate(value)) return "Use whole pesos, without centavos.";
        return value < MinRate || value > MaxRate ? $"Use an amount from ₱{MinRate:N0} to ₱{MaxRate:N0}." : null;
    }

    /// <summary>Null when saved, otherwise why it wasn't.</summary>
    public async Task<string?> SetAsync(decimal rate, string actorUserId)
    {
        if (Validate(rate) is string invalid) return invalid;

        // A transaction, so two changes at once can't both record the same "old" rate.
        return await _db.Value.RunTransactionAsync<string?>(async transaction =>
        {
            decimal current = CurrentRate(await transaction.GetSnapshotAsync(Config));
            if (current == rate) return $"The boundary rate is already ₱{rate:N0}.";

            DateTime now = ShiftClock.UtcNow;
            transaction.Set(Config, new Dictionary<string, object>
            {
                ["defaultBoundaryRate"] = (double)rate,
                ["boundaryRateHistory"] = FieldValue.ArrayUnion(BoundaryRateSchedule.HistoryEntry(now, current)),
            }, SetOptions.MergeAll);
            transaction.Create(_db.Value.Collection("audit_logs").Document(), new AuditLog
            {
                UserId = actorUserId,
                ActionType = "BoundaryRateChanged",
                AuditLogDetails = $"Changed the daily boundary rate from PHP {current:N2} to PHP {rate:N2}. Shifts started before the change keep the old rate.",
                Timestamp = DateTime.UtcNow,
            });
            return null;
        });
    }

    private static decimal CurrentRate(DocumentSnapshot snapshot)
    {
        double rate = SystemConfigReader.Read(snapshot).DefaultBoundaryRate;
        return rate > 0 ? (decimal)rate : ShiftRules.DefaultBoundaryRate;
    }
}

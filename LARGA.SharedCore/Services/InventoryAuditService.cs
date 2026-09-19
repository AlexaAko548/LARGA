using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using Microsoft.Extensions.Logging;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Reads and updates the collections behind ManagerWeb's Inventory and Audit Logs pages.
/// </summary>
public class InventoryAuditService
{
    private const int DefaultAuditLogLimit = 250;

    private readonly Lazy<FirestoreDb> _dbLazy;
    private readonly ILogger<InventoryAuditService> _logger;

    private FirestoreDb Db => _dbLazy.Value;

    public InventoryAuditService(Lazy<FirestoreDb> dbLazy, ILogger<InventoryAuditService> logger)
    {
        _dbLazy = dbLazy;
        _logger = logger;
    }

    public async Task<List<SparePart>> GetSparePartsAsync()
    {
        QuerySnapshot snapshot = await Db.Collection("spare_parts")
            .OrderBy("partName")
            .GetSnapshotAsync();

        List<SparePart> parts = ConvertDocuments<SparePart>(snapshot, "spare_parts");
        foreach (SparePart part in parts)
        {
            if (string.IsNullOrWhiteSpace(part.Unit))
            {
                part.Unit = "pcs";
            }
        }

        return parts;
    }

    public async Task<SparePart> AddSparePartAsync(SparePart part)
    {
        part.PartName = part.PartName.Trim();
        part.Unit = string.IsNullOrWhiteSpace(part.Unit) ? "pcs" : part.Unit.Trim();
        part.StockQuantity = Math.Max(0, part.StockQuantity);
        part.ReorderLevel = Math.Max(0, part.ReorderLevel);

        DocumentReference doc = Db.Collection("spare_parts").Document();
        part.PartId = doc.Id;

        await doc.SetAsync(part, SetOptions.Overwrite);
        return part;
    }

    public async Task<SparePart?> DeductPartAsync(string partId, int amount = 1)
    {
        if (string.IsNullOrWhiteSpace(partId))
        {
            return null;
        }

        int deduction = Math.Max(1, amount);

        return await Db.RunTransactionAsync(async transaction =>
        {
            DocumentReference doc = Db.Collection("spare_parts").Document(partId);
            DocumentSnapshot snapshot = await transaction.GetSnapshotAsync(doc);

            if (!snapshot.Exists)
            {
                return null;
            }

            SparePart part = snapshot.ConvertTo<SparePart>();
            part.PartId = snapshot.Id;

            int updatedQuantity = Math.Max(0, part.StockQuantity - deduction);
            transaction.Update(doc, "stockQuantity", updatedQuantity);

            part.StockQuantity = updatedQuantity;
            if (string.IsNullOrWhiteSpace(part.Unit))
            {
                part.Unit = "pcs";
            }

            return part;
        });
    }

    public async Task<List<AuditLogListItem>> GetAuditLogsAsync(int limit = DefaultAuditLogLimit)
    {
        QuerySnapshot snapshot = await Db.Collection("audit_logs")
            .OrderByDescending("timestamp")
            .Limit(Math.Max(1, limit))
            .GetSnapshotAsync();

        List<AuditLog> logs = ConvertDocuments<AuditLog>(snapshot, "audit_logs")
            .OrderByDescending(l => l.Timestamp)
            .ToList();

        Dictionary<string, string> actorNames = await ResolveActorNamesAsync(logs);

        return logs.Select(log => new AuditLogListItem
        {
            AuditLogId = log.AuditLogId,
            Timestamp = log.Timestamp,
            Actor = ResolveActorName(log.UserId, actorNames),
            Action = HumanizeAction(log.ActionType),
            Target = log.AuditLogDetails,
            Icon = ActionIcon(log.ActionType),
        }).ToList();
    }

    private async Task<Dictionary<string, string>> ResolveActorNamesAsync(List<AuditLog> logs)
    {
        List<string> userIds = logs
            .Select(l => l.UserId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var actors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (userIds.Count == 0)
        {
            return actors;
        }

        Dictionary<string, Task<DocumentSnapshot>> tasks = userIds.ToDictionary(
            id => id,
            id => Db.Collection("users").Document(id).GetSnapshotAsync(),
            StringComparer.Ordinal);

        await Task.WhenAll(tasks.Values);

        foreach ((string userId, Task<DocumentSnapshot> snapshotTask) in tasks)
        {
            try
            {
                DocumentSnapshot doc = snapshotTask.Result;
                if (!doc.Exists)
                {
                    continue;
                }

                UserProfile profile = doc.ConvertTo<UserProfile>();
                if (!string.IsNullOrWhiteSpace(profile.FullName))
                {
                    actors[userId] = profile.FullName;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve actor name for user {UserId}", userId);
            }
        }

        return actors;
    }

    private static string ResolveActorName(string userId, IReadOnlyDictionary<string, string> actorNames)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return "System";
        }

        return actorNames.TryGetValue(userId, out string? fullName)
            ? fullName
            : userId.Length <= 12 ? userId : userId[..12] + "...";
    }

    private static string HumanizeAction(string actionType)
    {
        if (string.IsNullOrWhiteSpace(actionType))
        {
            return "Activity";
        }

        var sb = new StringBuilder(actionType.Length + 8);
        sb.Append(actionType[0]);

        for (int i = 1; i < actionType.Length; i++)
        {
            char current = actionType[i];
            char previous = actionType[i - 1];
            if (char.IsUpper(current) && !char.IsUpper(previous) && previous != ' ')
            {
                sb.Append(' ');
            }
            sb.Append(current);
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(sb.ToString().ToLowerInvariant());
    }

    private static string ActionIcon(string actionType)
    {
        return actionType.Trim().ToLowerInvariant() switch
        {
            "login" => "👤",
            "resolvemaintenanceticket" => "🔧",
            "verifyfuelreceipt" => "⛽",
            "flagfuelreceipt" => "⚑",
            "emergencyalerttriggered" => "⚠",
            _ => "•",
        };
    }

    private List<T> ConvertDocuments<T>(QuerySnapshot snapshot, string collection) where T : class
    {
        var results = new List<T>(snapshot.Documents.Count);
        foreach (DocumentSnapshot doc in snapshot.Documents)
        {
            try
            {
                T item = doc.ConvertTo<T>();
                if (item is AuditLog log && string.IsNullOrWhiteSpace(log.AuditLogId))
                {
                    log.AuditLogId = doc.Id;
                }
                if (item is SparePart part && string.IsNullOrWhiteSpace(part.PartId))
                {
                    part.PartId = doc.Id;
                }
                results.Add(item);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping {Collection}/{DocumentId}: failed to convert to {Type}",
                    collection, doc.Id, typeof(T).Name);
            }
        }

        return results;
    }

    public class AuditLogListItem
    {
        public string AuditLogId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Actor { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
    }
}

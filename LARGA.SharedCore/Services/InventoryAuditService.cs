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
    private readonly AlertService _alerts;

    private FirestoreDb Db => _dbLazy.Value;

    public InventoryAuditService(Lazy<FirestoreDb> dbLazy, ILogger<InventoryAuditService> logger, AlertService alerts)
    {
        _dbLazy = dbLazy;
        _logger = logger;
        _alerts = alerts;
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

    public async Task<SparePart> AddSparePartAsync(SparePart part, string? actorUserId = null)
    {
        part.PartName = part.PartName.Trim();
        part.Unit = string.IsNullOrWhiteSpace(part.Unit) ? "pcs" : part.Unit.Trim();
        part.StockQuantity = Math.Max(0, part.StockQuantity);
        part.ReorderLevel = Math.Max(0, part.ReorderLevel);

        DocumentReference partDoc = Db.Collection("spare_parts").Document();
        DocumentReference auditDoc = Db.Collection("audit_logs").Document();
        part.PartId = partDoc.Id;

        var audit = new AuditLog
        {
            UserId = actorUserId ?? string.Empty,
            ActionType = "InventoryPartCreated",
            AuditLogDetails = $"Logged new spare part '{part.PartName}' with {part.StockQuantity} {part.Unit} (reorder level: {part.ReorderLevel}).",
            Timestamp = DateTime.UtcNow,
        };

        WriteBatch batch = Db.StartBatch();
        batch.Set(partDoc, part, SetOptions.Overwrite);
        batch.Set(auditDoc, audit, SetOptions.Overwrite);
        await batch.CommitAsync();
        return part;
    }

    /// <summary>
    /// Takes stock out (it was used) and records the usage in maintenance_parts_used - the
    /// part's usage history - at the part's current unit price, linked to the maintenance
    /// ticket it went into when one is given.
    /// </summary>
    public async Task<SparePart?> DeductPartAsync(string partId, int amount = 1, string? actorUserId = null,
        string? maintenanceId = null, string? note = null)
    {
        if (string.IsNullOrWhiteSpace(partId))
        {
            return null;
        }

        int deduction = Math.Max(1, amount);

        MaintenanceRecord? ticket = null;
        if (!string.IsNullOrWhiteSpace(maintenanceId))
        {
            DocumentSnapshot ticketDoc = await Db.Collection("maintenance_logs").Document(maintenanceId).GetSnapshotAsync();
            ticket = ticketDoc.Exists ? ticketDoc.ConvertTo<MaintenanceRecord>() : null;
        }

        SparePart? updated = await Db.RunTransactionAsync(async transaction =>
        {
            DocumentReference doc = Db.Collection("spare_parts").Document(partId);
            DocumentReference auditDoc = Db.Collection("audit_logs").Document();
            DocumentReference usageDoc = Db.Collection("maintenance_parts_used").Document();
            DocumentSnapshot snapshot = await transaction.GetSnapshotAsync(doc);

            if (!snapshot.Exists)
            {
                return null;
            }

            SparePart part = snapshot.ConvertTo<SparePart>();
            part.PartId = snapshot.Id;

            // Only what was actually in stock counts as used.
            int used = Math.Min(deduction, Math.Max(0, part.StockQuantity));
            int updatedQuantity = Math.Max(0, part.StockQuantity - deduction);
            transaction.Update(doc, "stockQuantity", updatedQuantity);

            if (used > 0)
            {
                transaction.Set(usageDoc, new MaintenancePartsUsed
                {
                    MaintenanceId = ticket?.MaintenanceId ?? string.Empty,
                    PartId = part.PartId,
                    QuantityUsed = used,
                    UsedAt = DateTime.UtcNow,
                    UnitCost = (double)part.UnitPrice,
                    TaxiId = string.IsNullOrWhiteSpace(ticket?.TaxiId) ? null : ticket.TaxiId,
                    RecordedBy = string.IsNullOrWhiteSpace(actorUserId) ? null : actorUserId,
                    Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                });
            }

            string unit = string.IsNullOrWhiteSpace(part.Unit) ? "pcs" : part.Unit;
            string usedFor = ticket is null ? string.Empty : $" for {ticket.TaxiId} - {ticket.IssueTitle}";
            var audit = new AuditLog
            {
                UserId = actorUserId ?? string.Empty,
                ActionType = "InventoryStockDeducted",
                AuditLogDetails = $"Deducted {deduction} {unit} from '{part.PartName}'{usedFor}. Stock: {part.StockQuantity} -> {updatedQuantity}.",
                Timestamp = DateTime.UtcNow,
            };
            transaction.Set(auditDoc, audit);

            part.StockQuantity = updatedQuantity;
            if (string.IsNullOrWhiteSpace(part.Unit))
            {
                part.Unit = "pcs";
            }

            return part;
        });

        // Outside the transaction: a failed notification must not roll back the stock change.
        if (updated is not null && updated.StockQuantity <= updated.ReorderLevel)
        {
            await _alerts.CreateLowStockAlertAsync(updated);
        }

        return updated;
    }

    /// <summary>Restocks a part. <paramref name="unitPrice"/>, when given, becomes the part's
    /// unit price from now on (past usage keeps the price it was recorded at).</summary>
    public async Task<SparePart?> AddPartStockAsync(string partId, int amount = 1, string? actorUserId = null, decimal? unitPrice = null)
    {
        if (string.IsNullOrWhiteSpace(partId))
        {
            return null;
        }

        int increment = Math.Max(1, amount);

        return await Db.RunTransactionAsync(async transaction =>
        {
            DocumentReference doc = Db.Collection("spare_parts").Document(partId);
            DocumentReference auditDoc = Db.Collection("audit_logs").Document();
            DocumentSnapshot snapshot = await transaction.GetSnapshotAsync(doc);

            if (!snapshot.Exists)
            {
                return null;
            }

            SparePart part = snapshot.ConvertTo<SparePart>();
            part.PartId = snapshot.Id;

            int updatedQuantity = Math.Max(0, part.StockQuantity + increment);
            var updates = new Dictionary<string, object> { ["stockQuantity"] = updatedQuantity };
            string priceNote = string.Empty;
            if (unitPrice is decimal price && price >= 0 && price != part.UnitPrice)
            {
                updates["unitPrice"] = (double)price;
                priceNote = $" Unit price: ₱{part.UnitPrice:0.00} -> ₱{price:0.00}.";
                part.UnitPrice = price;
            }
            transaction.Update(doc, updates);

            string unit = string.IsNullOrWhiteSpace(part.Unit) ? "pcs" : part.Unit;
            var audit = new AuditLog
            {
                UserId = actorUserId ?? string.Empty,
                ActionType = "InventoryStockAdded",
                AuditLogDetails = $"Added {increment} {unit} to '{part.PartName}'. Stock: {part.StockQuantity} -> {updatedQuantity}.{priceNote}",
                Timestamp = DateTime.UtcNow,
            };
            transaction.Set(auditDoc, audit);

            part.StockQuantity = updatedQuantity;
            if (string.IsNullOrWhiteSpace(part.Unit))
            {
                part.Unit = "pcs";
            }

            return part;
        });
    }

    /// <summary>
    /// One part's usage history (LAR-84): every maintenance_parts_used row for it, newest
    /// first, with the unit and maintenance ticket it went into. Rows written before usage
    /// carried a date/unit cost (seed data) take them from the maintenance record and the
    /// part's current price, and are marked IsCostEstimated.
    /// </summary>
    public async Task<PartUsageHistory> GetPartUsageHistoryAsync(SparePart part)
    {
        QuerySnapshot snapshot = await Db.Collection("maintenance_parts_used")
            .WhereEqualTo("partId", part.PartId)
            .GetSnapshotAsync();
        List<MaintenancePartsUsed> usages = ConvertDocuments<MaintenancePartsUsed>(snapshot, "maintenance_parts_used");

        var tickets = new Dictionary<string, MaintenanceRecord>();
        foreach (string id in usages.Select(u => u.MaintenanceId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct())
        {
            DocumentSnapshot doc = await Db.Collection("maintenance_logs").Document(id).GetSnapshotAsync();
            if (doc.Exists)
            {
                try
                {
                    tickets[id] = doc.ConvertTo<MaintenanceRecord>();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping maintenance_logs/{Id} in part history", id);
                }
            }
        }

        List<PartUsageEntry> entries = usages.Select(u =>
        {
            MaintenanceRecord? ticket = tickets.GetValueOrDefault(u.MaintenanceId);
            decimal unitCost = u.UnitCost is double cost ? (decimal)cost : part.UnitPrice;
            return new PartUsageEntry
            {
                UsedAt = u.UsedAt ?? ticket?.DateResolved ?? ticket?.DateLogged,
                Quantity = u.QuantityUsed,
                UnitCost = unitCost,
                IsCostEstimated = u.UnitCost is null,
                TaxiId = !string.IsNullOrWhiteSpace(u.TaxiId) ? u.TaxiId : ticket?.TaxiId,
                TicketTitle = ticket?.IssueTitle,
                TicketType = ticket is null ? null : MaintenanceTypeLabel(ticket.MaintenanceType),
                Note = u.Note,
            };
        })
        .OrderByDescending(e => e.UsedAt ?? DateTime.MinValue)
        .ToList();

        return new PartUsageHistory { Entries = entries };
    }

    /// <summary>Maintenance tickets a deducted part can be booked against - the open ones
    /// first, then the most recently logged.</summary>
    public async Task<List<MaintenanceTicketOption>> GetTicketOptionsAsync(int limit = 40)
    {
        QuerySnapshot snapshot = await Db.Collection("maintenance_logs").GetSnapshotAsync();
        return ConvertDocuments<MaintenanceRecord>(snapshot, "maintenance_logs")
            .Where(m => !string.Equals(m.Status, "Dismissed", StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DateResolved is null ? 0 : 1)
            .ThenByDescending(m => m.DateLogged)
            .Take(limit)
            .Select(m => new MaintenanceTicketOption
            {
                MaintenanceId = m.MaintenanceId,
                Label = $"{m.TaxiId} · {(string.IsNullOrWhiteSpace(m.IssueTitle) ? MaintenanceTypeLabel(m.MaintenanceType) : m.IssueTitle)}"
                    + $" · {m.DateLogged.ToPhilippineTime():MMM d}{(m.DateResolved is null ? " (open)" : "")}",
            })
            .ToList();
    }

    private static string MaintenanceTypeLabel(MaintenanceType type) => type switch
    {
        MaintenanceType.BreakdownRepair => "Breakdown repair",
        MaintenanceType.AccidentCorrection => "Accident correction",
        _ => "Routine checkup",
    };

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
                if (item is MaintenanceRecord record && string.IsNullOrWhiteSpace(record.MaintenanceId))
                {
                    record.MaintenanceId = doc.Id;
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

    public class PartUsageEntry
    {
        public DateTime? UsedAt { get; set; }
        public int Quantity { get; set; }
        public decimal UnitCost { get; set; }
        public decimal LineCost => Quantity * UnitCost;

        /// <summary>The row predates recorded unit costs - priced at the part's current unit price.</summary>
        public bool IsCostEstimated { get; set; }

        public string? TaxiId { get; set; }
        public string? TicketTitle { get; set; }
        public string? TicketType { get; set; }
        public string? Note { get; set; }
    }

    public class PartUsageHistory
    {
        public List<PartUsageEntry> Entries { get; set; } = new();
        public int TotalQuantity => Entries.Sum(e => e.Quantity);
        public decimal TotalCost => Entries.Sum(e => e.LineCost);
        public decimal AverageUnitCost => TotalQuantity == 0 ? 0 : TotalCost / TotalQuantity;
        public DateTime? LastUsed => Entries.Max(e => e.UsedAt);
    }

    public class MaintenanceTicketOption
    {
        public string MaintenanceId { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
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

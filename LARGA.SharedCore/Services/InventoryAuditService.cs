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
    /// <summary>Audit Logs page size (ANA-015).</summary>
    public const int AuditLogPageSize = 50;

    // Batch size for reading a whole date range (search and export).
    private const int AuditLogBatchSize = 500;

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
        part.Brand = (part.Brand ?? string.Empty).Trim();
        part.Supplier = (part.Supplier ?? string.Empty).Trim();
        part.Unit = string.IsNullOrWhiteSpace(part.Unit) ? "pcs" : part.Unit.Trim();
        // INV-007: a part is logged with the stock bought - checked here too, not just on the form.
        if (part.StockQuantity < 1)
        {
            throw new ArgumentException("Enter a quantity of at least 1 - a new part is logged with the stock you have.");
        }
        part.ReorderLevel = Math.Max(0, part.ReorderLevel);

        DocumentReference partDoc = Db.Collection("spare_parts").Document();
        DocumentReference auditDoc = Db.Collection("audit_logs").Document();
        part.PartId = partDoc.Id;

        var audit = new AuditLog
        {
            UserId = actorUserId ?? string.Empty,
            ActionType = "InventoryPartCreated",
            AuditLogDetails = $"Logged new spare part '{part.DisplayName}' at ₱{part.UnitPrice:0.00} with {part.StockQuantity} {part.Unit} (reorder level: {part.ReorderLevel}).",
            Timestamp = DateTime.UtcNow,
        };

        WriteBatch batch = Db.StartBatch();
        batch.Set(partDoc, part, SetOptions.Overwrite);
        batch.Create(auditDoc, audit);
        await batch.CommitAsync();
        return part;
    }

    /// <summary>
    /// Corrects a part's details (the same ones asked for when it was logged). Usage history is
    /// keyed by the part's id, so it stays with the part. The audit entry lists what changed;
    /// nothing is written when nothing changed. Returns null when the part no longer exists.
    /// </summary>
    public async Task<SparePart?> UpdateSparePartAsync(SparePart edited, string? actorUserId = null)
    {
        if (string.IsNullOrWhiteSpace(edited.PartId))
        {
            return null;
        }

        string name = edited.PartName.Trim();
        string brand = (edited.Brand ?? string.Empty).Trim();
        string supplier = (edited.Supplier ?? string.Empty).Trim();
        string unit = string.IsNullOrWhiteSpace(edited.Unit) ? "pcs" : edited.Unit.Trim();
        int stock = Math.Max(0, edited.StockQuantity);
        int reorder = Math.Max(0, edited.ReorderLevel);
        decimal price = Math.Max(0m, edited.UnitPrice);

        bool stockDropped = false;
        SparePart? updated = await Db.RunTransactionAsync(async transaction =>
        {
            stockDropped = false; // the transaction can re-run
            DocumentReference doc = Db.Collection("spare_parts").Document(edited.PartId);
            DocumentSnapshot snapshot = await transaction.GetSnapshotAsync(doc);
            if (!snapshot.Exists)
            {
                return null;
            }

            SparePart part = snapshot.ConvertTo<SparePart>();
            part.PartId = snapshot.Id;
            if (part.IsArchived)
            {
                throw new PartArchivedException(part);
            }
            string oldName = part.DisplayName;
            string oldUnit = string.IsNullOrWhiteSpace(part.Unit) ? "pcs" : part.Unit;

            var updates = new Dictionary<string, object>();
            var changes = new List<string>();
            void Change(string field, object value, string label, string from, string to)
            {
                if (from == to) return;
                updates[field] = value;
                changes.Add($"{label}: {(from.Length == 0 ? "(none)" : from)} -> {(to.Length == 0 ? "(none)" : to)}");
            }

            Change("partName", name, "Name", part.PartName, name);
            Change("brand", brand, "Brand", part.Brand ?? string.Empty, brand);
            Change("supplier", supplier, "Supplier", part.Supplier ?? string.Empty, supplier);
            Change("unit", unit, "Unit", oldUnit, unit);
            Change("stockQuantity", stock, "Stock", part.StockQuantity.ToString(), stock.ToString());
            Change("reorderLevel", reorder, "Min stock", part.ReorderLevel.ToString(), reorder.ToString());
            Change("unitPrice", (double)price, "Unit price", $"₱{part.UnitPrice:0.00}", $"₱{price:0.00}");

            if (updates.Count == 0)
            {
                part.Unit = oldUnit;
                return part;
            }

            stockDropped = stock < part.StockQuantity || reorder > part.ReorderLevel;
            transaction.Update(doc, updates);
            transaction.Create(Db.Collection("audit_logs").Document(), new AuditLog
            {
                UserId = actorUserId ?? string.Empty,
                ActionType = "InventoryPartEdited",
                AuditLogDetails = $"Edited spare part '{oldName}'. {string.Join("; ", changes)}.",
                Timestamp = DateTime.UtcNow,
            });

            part.PartName = name;
            part.Brand = brand;
            part.Supplier = supplier;
            part.Unit = unit;
            part.StockQuantity = stock;
            part.ReorderLevel = reorder;
            part.UnitPrice = price;
            return part;
        });

        // Outside the transaction: a failed notification must not roll back the edit.
        if (updated is not null && stockDropped && updated.StockQuantity <= updated.ReorderLevel)
        {
            await _alerts.CreateLowStockAlertAsync(updated);
        }
        else if (updated is not null && updated.StockQuantity > updated.ReorderLevel)
        {
            await _alerts.ResolveLowStockAlertAsync(updated);
        }

        return updated;
    }

    /// <summary>
    /// Takes stock out (it was used) and records the usage in maintenance_parts_used - the
    /// part's usage history - at the part's current unit price, linked to the maintenance
    /// ticket it went into when one is given. The stock read and the deduction are one
    /// transaction: asking for more than is in stock throws <see cref="InsufficientStockException"/>
    /// and changes nothing (INV-006), so the audit entry always matches what was taken out.
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
            if (part.IsArchived)
            {
                throw new PartArchivedException(part);
            }

            string unit = string.IsNullOrWhiteSpace(part.Unit) ? "pcs" : part.Unit;
            int inStock = Math.Max(0, part.StockQuantity);
            if (deduction > inStock)
            {
                // Thrown out of the transaction callback, so nothing is written.
                throw new InsufficientStockException(part.DisplayName, inStock, unit);
            }

            int used = deduction;
            int updatedQuantity = inStock - used;
            transaction.Update(doc, "stockQuantity", updatedQuantity);

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

            string usedFor = ticket is null ? string.Empty : $" for {ticket.TaxiId} - {ticket.IssueTitle}";
            var audit = new AuditLog
            {
                UserId = actorUserId ?? string.Empty,
                ActionType = "InventoryStockDeducted",
                AuditLogDetails = $"Deducted {used} {unit} from '{part.DisplayName}'{usedFor}. Stock: {inStock} -> {updatedQuantity}."
                    + (string.IsNullOrWhiteSpace(note) ? string.Empty : $" Note: {note.Trim()}"),
                Timestamp = DateTime.UtcNow,
            };
            transaction.Create(auditDoc, audit);

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
    /// <summary>
    /// SRS REQ-4.5-1: archives a part instead of deleting it - usage history (maintenance_parts_used)
    /// keeps its ID, name and price. Records who and when, writes an InventoryPartArchived audit entry
    /// in the same transaction, and closes the part's open low-stock alert. Returns null when the part
    /// no longer exists; an already archived part is returned unchanged.
    /// </summary>
    public async Task<SparePart?> ArchivePartAsync(string partId, string? actorUserId = null)
    {
        if (string.IsNullOrWhiteSpace(partId))
        {
            return null;
        }

        bool changed = false;
        SparePart? archived = await Db.RunTransactionAsync(async transaction =>
        {
            changed = false; // the transaction can re-run
            DocumentReference doc = Db.Collection("spare_parts").Document(partId);
            DocumentSnapshot snapshot = await transaction.GetSnapshotAsync(doc);
            if (!snapshot.Exists)
            {
                return null;
            }

            SparePart part = snapshot.ConvertTo<SparePart>();
            part.PartId = snapshot.Id;
            if (part.IsArchived)
            {
                return part;
            }

            DateTime now = DateTime.UtcNow;
            transaction.Update(doc, new Dictionary<string, object>
            {
                ["isArchived"] = true,
                ["archivedAt"] = now,
                ["archivedBy"] = actorUserId ?? string.Empty,
            });
            string unit = string.IsNullOrWhiteSpace(part.Unit) ? "pcs" : part.Unit;
            transaction.Create(Db.Collection("audit_logs").Document(), new AuditLog
            {
                UserId = actorUserId ?? string.Empty,
                ActionType = SparePartArchiveRules.ArchivedAction,
                AuditLogDetails = $"Archived spare part '{part.DisplayName}' ({part.StockQuantity} {unit} in stock).",
                Timestamp = now,
            });

            part.IsArchived = true;
            part.ArchivedAt = now;
            part.ArchivedBy = actorUserId;
            changed = true;
            return part;
        });

        if (archived is not null && changed)
        {
            await _alerts.CloseLowStockAlertForArchivedPartAsync(archived);
        }
        return archived;
    }

    /// <summary>Brings an archived part back into the inventory (InventoryPartRestored audit entry);
    /// a part still at or below its minimum gets its low-stock alert again. Returns null when the part
    /// no longer exists.</summary>
    public async Task<SparePart?> RestorePartAsync(string partId, string? actorUserId = null)
    {
        if (string.IsNullOrWhiteSpace(partId))
        {
            return null;
        }

        bool changed = false;
        SparePart? restored = await Db.RunTransactionAsync(async transaction =>
        {
            changed = false;
            DocumentReference doc = Db.Collection("spare_parts").Document(partId);
            DocumentSnapshot snapshot = await transaction.GetSnapshotAsync(doc);
            if (!snapshot.Exists)
            {
                return null;
            }

            SparePart part = snapshot.ConvertTo<SparePart>();
            part.PartId = snapshot.Id;
            if (!part.IsArchived)
            {
                return part;
            }

            transaction.Update(doc, new Dictionary<string, object>
            {
                ["isArchived"] = false,
                ["archivedAt"] = FieldValue.Delete,
                ["archivedBy"] = FieldValue.Delete,
            });
            transaction.Create(Db.Collection("audit_logs").Document(), new AuditLog
            {
                UserId = actorUserId ?? string.Empty,
                ActionType = SparePartArchiveRules.RestoredAction,
                AuditLogDetails = $"Restored archived spare part '{part.DisplayName}'.",
                Timestamp = DateTime.UtcNow,
            });

            part.IsArchived = false;
            part.ArchivedAt = null;
            part.ArchivedBy = null;
            changed = true;
            return part;
        });

        if (restored is not null && changed && restored.StockQuantity <= restored.ReorderLevel)
        {
            await _alerts.CreateLowStockAlertAsync(restored);
        }
        return restored;
    }

    public async Task<SparePart?> AddPartStockAsync(string partId, int amount = 1, string? actorUserId = null, decimal? unitPrice = null)
    {
        if (string.IsNullOrWhiteSpace(partId))
        {
            return null;
        }

        int increment = Math.Max(1, amount);

        SparePart? restocked = await Db.RunTransactionAsync(async transaction =>
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
            if (part.IsArchived)
            {
                throw new PartArchivedException(part);
            }

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
                AuditLogDetails = $"Added {increment} {unit} to '{part.DisplayName}'. Stock: {part.StockQuantity} -> {updatedQuantity}.{priceNote}",
                Timestamp = DateTime.UtcNow,
            };
            transaction.Create(auditDoc, audit);

            part.StockQuantity = updatedQuantity;
            if (string.IsNullOrWhiteSpace(part.Unit))
            {
                part.Unit = "pcs";
            }

            return part;
        });

        // Back above the minimum: close the part's low-stock alert (INV-010). Outside the
        // transaction, like the alert itself.
        if (restocked is not null && restocked.StockQuantity > restocked.ReorderLevel)
        {
            await _alerts.ResolveLowStockAlertAsync(restocked);
        }

        return restocked;
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

    /// <summary>
    /// One page of the audit trail within [fromUtc, toUtc), newest first (ANA-015). Firestore
    /// cursor paging: pass the previous page's <see cref="AuditLogPage.Cursor"/> as
    /// <paramref name="after"/> for the next one. A range plus an order on the same single field
    /// (timestamp) only needs Firestore's automatic single-field index - no composite index.
    /// </summary>
    public async Task<AuditLogPage> GetAuditLogPageAsync(DateTime fromUtc, DateTime toUtc, DocumentSnapshot? after = null, int pageSize = AuditLogPageSize)
    {
        // One extra document tells whether there's a next page.
        QuerySnapshot snapshot = await AuditRangeQuery(fromUtc, toUtc, after).Limit(pageSize + 1).GetSnapshotAsync();
        List<DocumentSnapshot> docs = snapshot.Documents.Take(pageSize).ToList();

        List<AuditLog> logs = ConvertAuditDocuments(docs);
        return new AuditLogPage
        {
            Items = await ToListItemsAsync(logs),
            Cursor = docs.Count > 0 ? docs[^1] : after,
            HasMore = snapshot.Documents.Count > pageSize,
        };
    }

    /// <summary>Every audit entry within [fromUtc, toUtc), newest first, read in batches of 500 -
    /// what the page's search and both exports (CSV, PDF) work from, so they cover the whole range
    /// and not just the loaded page.</summary>
    public async Task<List<AuditLogListItem>> GetAllAuditLogsAsync(DateTime fromUtc, DateTime toUtc)
    {
        var logs = new List<AuditLog>();
        DocumentSnapshot? after = null;
        while (true)
        {
            QuerySnapshot batch = await AuditRangeQuery(fromUtc, toUtc, after).Limit(AuditLogBatchSize).GetSnapshotAsync();
            logs.AddRange(ConvertAuditDocuments(batch.Documents));
            if (batch.Documents.Count < AuditLogBatchSize)
            {
                break;
            }
            after = batch.Documents[^1];
        }

        return await ToListItemsAsync(logs);
    }

    private Query AuditRangeQuery(DateTime fromUtc, DateTime toUtc, DocumentSnapshot? after)
    {
        Query query = Db.Collection("audit_logs")
            .WhereGreaterThanOrEqualTo("timestamp", DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc))
            .WhereLessThan("timestamp", DateTime.SpecifyKind(toUtc, DateTimeKind.Utc))
            .OrderByDescending("timestamp");
        return after is null ? query : query.StartAfter(after);
    }

    private List<AuditLog> ConvertAuditDocuments(IEnumerable<DocumentSnapshot> docs)
    {
        var logs = new List<AuditLog>();
        foreach (DocumentSnapshot doc in docs)
        {
            try
            {
                AuditLog log = doc.ConvertTo<AuditLog>();
                if (string.IsNullOrWhiteSpace(log.AuditLogId))
                {
                    log.AuditLogId = doc.Id;
                }
                logs.Add(log);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping audit_logs/{DocumentId}: failed to convert", doc.Id);
            }
        }
        return logs;
    }

    private async Task<List<AuditLogListItem>> ToListItemsAsync(List<AuditLog> logs)
    {
        Dictionary<string, (string Name, string Role)> actors = await ResolveActorsAsync(logs);

        return logs.Select(log => new AuditLogListItem
        {
            AuditLogId = log.AuditLogId,
            Timestamp = log.Timestamp,
            Actor = ResolveActorName(log.UserId, actors),
            Role = string.IsNullOrWhiteSpace(log.UserId) ? "System"
                : actors.TryGetValue(log.UserId, out var actor) && !string.IsNullOrWhiteSpace(actor.Role) ? actor.Role : "—",
            Action = HumanizeAction(log.ActionType),
            Target = log.AuditLogDetails,
            Icon = ActionIcon(log.ActionType),
        }).ToList();
    }

    private async Task<Dictionary<string, (string Name, string Role)>> ResolveActorsAsync(List<AuditLog> logs)
    {
        List<string> userIds = logs
            .Select(l => l.UserId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var actors = new Dictionary<string, (string Name, string Role)>(StringComparer.Ordinal);
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
                actors[userId] = (profile.FullName?.Trim() ?? string.Empty, profile.Role?.Trim() ?? string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve actor name for user {UserId}", userId);
            }
        }

        return actors;
    }

    private static string ResolveActorName(string userId, IReadOnlyDictionary<string, (string Name, string Role)> actors)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return "System";
        }

        return actors.TryGetValue(userId, out var actor) && !string.IsNullOrWhiteSpace(actor.Name)
            ? actor.Name
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

    // Lucide icon names - ManagerWeb's Audit Logs page draws them with its <Icon> component.
    private static string ActionIcon(string actionType)
    {
        return actionType.Trim().ToLowerInvariant() switch
        {
            "login" => "user",
            "resolvemaintenanceticket" => "wrench",
            "verifyfuelreceipt" => "fuel",
            "flagfuelreceipt" => "flag",
            "emergencyalerttriggered" => "triangle-alert",
            _ => "dot",
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

    /// <summary>A deduction asked for more than the part has in stock; nothing was changed.
    /// Message is safe to show to the manager.</summary>
    public class InsufficientStockException : InvalidOperationException
    {
        public InsufficientStockException(string partName, int inStock, string unit)
            : base($"Only {inStock} {unit} of {partName} in stock.")
        {
            PartName = partName;
            InStock = inStock;
        }

        public string PartName { get; }
        public int InStock { get; }
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

    public class AuditLogPage
    {
        public List<AuditLogListItem> Items { get; set; } = new();

        /// <summary>The last document of this page - pass it back for the next page.</summary>
        public DocumentSnapshot? Cursor { get; set; }

        public bool HasMore { get; set; }
    }

    public class AuditLogListItem
    {
        public string AuditLogId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Actor { get; set; } = string.Empty;

        /// <summary>The actor's users/{uid}.role ("Manager", "Driver"...), "System" for system entries.</summary>
        public string Role { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
    }
}

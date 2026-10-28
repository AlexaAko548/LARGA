using System;
using System.Collections.Generic;
using System.Linq;
using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore;

/// <summary>
/// SRS REQ-4.5-1: a spare part is never deleted - maintenance_parts_used keeps pointing at its ID,
/// and usage history still shows its name and price. A part that's no longer stocked is archived
/// instead (spare_parts.isArchived): it drops out of the Inventory list (unless "Show archived"),
/// the Garage parts picker and low-stock alerts, and can be restored. Manager-only, like the rest
/// of Inventory.
/// </summary>
public static class SparePartArchiveRules
{
    public const string ArchivedAction = "InventoryPartArchived";
    public const string RestoredAction = "InventoryPartRestored";

    /// <summary>Parts that can be used, restocked and alerted on (not archived).</summary>
    public static IEnumerable<SparePart> Active(IEnumerable<SparePart> parts) => parts.Where(p => !p.IsArchived);

    /// <summary>The confirm dialog's message - with a warning when stock is still on hand.</summary>
    public static string ArchiveConfirmMessage(SparePart part)
    {
        string unit = string.IsNullOrWhiteSpace(part.Unit) ? "pcs" : part.Unit;
        string message = $"'{part.DisplayName}' will be hidden from the inventory list, the Garage parts picker and low-stock alerts. Its usage history is kept, and you can restore it from Show archived.";
        return part.StockQuantity > 0
            ? $"{message} Warning: {part.StockQuantity} {unit} are still in stock - they won't be available for work orders while it's archived."
            : message;
    }

    /// <summary>Why a change to an archived part is refused (it has to be restored first).</summary>
    public static string ArchivedChangeMessage(SparePart part) =>
        $"'{part.DisplayName}' is archived - restore it first.";

    /// <summary>The Log New Part form's message when the exact variant is archived.</summary>
    public const string ArchivedDuplicateMessage =
        "This exact part (same name, brand, supplier and price) is archived - restore it from Show archived instead.";
}

/// <summary>A stock or detail change on an archived part (SparePartArchiveRules). Message is safe to show.</summary>
public sealed class PartArchivedException : InvalidOperationException
{
    public PartArchivedException(SparePart part) : base(SparePartArchiveRules.ArchivedChangeMessage(part)) { }
}

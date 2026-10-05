using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

[FirestoreData]
public class MaintenancePartsUsed
{
    [FirestoreDocumentId]
    public string UsageId { get; set; } = string.Empty;

    [FirestoreProperty("maintenanceId")]
    public string MaintenanceId { get; set; } = string.Empty;

    [FirestoreProperty("partId")]
    public string PartId { get; set; } = string.Empty;

    [FirestoreProperty("quantityUsed")]
    public int QuantityUsed { get; set; }

    // LAR-84 (Spare Parts usage history). Written by the Inventory page's Deduct; older
    // (seeded) rows don't have them - their date and unit come from the maintenance record
    // and their cost from the part's current unit price.

    /// <summary>When the stock was taken out.</summary>
    [FirestoreProperty("usedAt")]
    public DateTime? UsedAt { get; set; }

    /// <summary>The part's unit price at the time - later price changes don't rewrite history.</summary>
    [FirestoreProperty("unitCost")]
    public double? UnitCost { get; set; }

    /// <summary>The unit it went into, when known (from the linked maintenance record).</summary>
    [FirestoreProperty("taxiId")]
    public string? TaxiId { get; set; }

    [FirestoreProperty("recordedBy")]
    public string? RecordedBy { get; set; }

    [FirestoreProperty("note")]
    public string? Note { get; set; }
}

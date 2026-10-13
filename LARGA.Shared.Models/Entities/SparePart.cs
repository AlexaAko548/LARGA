using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

[FirestoreData]
public class SparePart
{
    [FirestoreDocumentId]
    public string PartId { get; set; } = string.Empty;

    [FirestoreProperty("partName")]
    public string PartName { get; set; } = string.Empty;

    [FirestoreProperty("stockQuantity")]
    public int StockQuantity { get; set; }

    [FirestoreProperty("reorderLevel")]
    public int ReorderLevel { get; set; }

    [FirestoreProperty("unit")]
    public string Unit { get; set; } = "pcs";

    [FirestoreProperty("unitPrice", ConverterType = typeof(DecimalConverter))]
    public decimal UnitPrice { get; set; }

    // LAR-114: the same part from a different brand or supplier, or bought at a different price,
    // is its own row (a variant) instead of being lumped into one stock count. Blank on parts
    // logged before variants existed.
    [FirestoreProperty("brand")]
    public string Brand { get; set; } = string.Empty;

    [FirestoreProperty("supplier")]
    public string Supplier { get; set; } = string.Empty;

    /// <summary>"Brake Pads · Bendix · AutoParts PH" - the part name plus whatever tells this
    /// variant apart. Not stored.</summary>
    public string DisplayName
    {
        get
        {
            string[] details = new[] { Brand, Supplier }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray();
            return details.Length == 0 ? PartName : $"{PartName} · {string.Join(" · ", details)}";
        }
    }

    /// <summary>Same part name, brand, supplier and unit price (case-insensitive) - i.e. the same row.</summary>
    public bool IsSameVariant(string partName, string? brand, string? supplier, decimal unitPrice) =>
        string.Equals(PartName.Trim(), partName.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals((Brand ?? string.Empty).Trim(), (brand ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals((Supplier ?? string.Empty).Trim(), (supplier ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
        && UnitPrice == unitPrice;
}
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

    [FirestoreProperty("unitPrice")]
    public decimal UnitPrice { get; set; }
}
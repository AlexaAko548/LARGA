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
}
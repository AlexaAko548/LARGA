using Google.Cloud.Firestore;

namespace LARGA.Shared.Models.Entities;

[FirestoreData]
public class SystemConfig
{
    [FirestoreDocumentId]
    public string ConfigId { get; set; } = string.Empty;

    [FirestoreProperty("standardLatePenalty")]
    public double StandardLatePenalty { get; set; }

    [FirestoreProperty("defaultBoundaryRate")]
    public double DefaultBoundaryRate { get; set; }
}
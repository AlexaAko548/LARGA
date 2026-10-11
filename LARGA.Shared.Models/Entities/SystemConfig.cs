using System.Collections.Generic;
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

    [FirestoreProperty("idleThresholdMinutes")]
    public double IdleThresholdMinutes { get; set; } = 15;

    // LAR-86/87: numbers the driver app auto-answers after an automated SOS. Kept here (any
    // signed-in user may read system_configs) because firestore.rules don't let a driver read
    // managers' users documents to discover them.
    [FirestoreProperty("managerPhoneNumbers")]
    public List<string> ManagerPhoneNumbers { get; set; } = new();
}
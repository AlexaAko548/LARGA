using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

[FirestoreData]
public class ShiftSchedule
{
    [FirestoreDocumentId]
    public string ScheduleId { get; set; } = string.Empty;

    [FirestoreProperty("driverId")]
    public string DriverId { get; set; } = string.Empty;

    [FirestoreProperty("taxiId")]
    public string TaxiId { get; set; } = string.Empty;

    [FirestoreProperty("scheduledStartTime")]
    public DateTime ScheduledStartTime { get; set; }

    [FirestoreProperty("status")]
    public string Status { get; set; } = "Planned";
}
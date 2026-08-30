using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

[FirestoreData]
public class ShiftLog
{
    // Captures the auto-generated Firestore document ID (e.g., 3qHnvbY...)
    [FirestoreDocumentId]
    public string DocumentId { get; set; } = string.Empty;

    [FirestoreProperty("shiftId")]
    public string ShiftId { get; set; } = string.Empty;

    [FirestoreProperty("driverId")]
    public string DriverId { get; set; } = string.Empty;

    [FirestoreProperty("taxiId")]
    public string TaxiId { get; set; } = string.Empty;

    [FirestoreProperty("shiftStart")]
    public DateTime? ShiftStart { get; set; }

    [FirestoreProperty("shiftEnd")]
    public DateTime? ShiftEnd { get; set; }

    [FirestoreProperty("startMileage")]
    public int StartMileage { get; set; }

    [FirestoreProperty("endMileage")]
    public int EndMileage { get; set; }

    [FirestoreProperty("status")]
    public string Status { get; set; } = string.Empty;

    [FirestoreProperty("managerNote")]
    public string ManagerNote { get; set; } = string.Empty;
}
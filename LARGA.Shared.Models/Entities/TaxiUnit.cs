using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

[FirestoreData]
public class TaxiUnit
{
	// Captures the Firestore document ID (e.g., TAXI_001)
	[FirestoreDocumentId]
	public string DocumentId { get; set; } = string.Empty;

	[FirestoreProperty("taxiId")]
	public string TaxiId { get; set; } = string.Empty;

	[FirestoreProperty("model")]
	public string Model { get; set; } = string.Empty;

	[FirestoreProperty("yearManufactured")]
	public int YearManufactured { get; set; }

	[FirestoreProperty("currentMileage")]
	public int CurrentMileage { get; set; }

	[FirestoreProperty("status")]
	public string Status { get; set; } = string.Empty;

	[FirestoreProperty("lastServicedDate")]
	public DateTime? LastServicedDate { get; set; }
}
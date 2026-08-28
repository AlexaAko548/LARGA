using Google.Cloud.Firestore;
using System;

namespace LARGA.Shared.Models.Entities;

[FirestoreData]
public class UserProfile
{
    // Uses the actual Document ID (e.g., rRXJpVetg...) as the UserId
    [FirestoreDocumentId]
    public string UserId { get; set; } = string.Empty;

    [FirestoreProperty("fullName")]
    public string FullName { get; set; } = string.Empty;

    [FirestoreProperty("email")]
    public string Email { get; set; } = string.Empty;

    [FirestoreProperty("phoneNumber")]
    public string PhoneNumber { get; set; } = string.Empty;

    [FirestoreProperty("role")]
    public string Role { get; set; } = string.Empty;

    [FirestoreProperty("assignedTaxiId")]
    public string AssignedTaxiId { get; set; } = string.Empty;

    [FirestoreProperty("licenseNumber")]
    public string LicenseNumber { get; set; } = string.Empty;

    [FirestoreProperty("licenseClassification")]
    public string LicenseClassification { get; set; } = string.Empty;

    [FirestoreProperty("licenseRestrictionCode")]
    public string LicenseRestrictionCode { get; set; } = string.Empty;

    [FirestoreProperty("licenseExpiryDate")]
    public DateTime? LicenseExpiryDate { get; set; }

    [FirestoreProperty("currentArrears")]
    public double CurrentArrears { get; set; }

    [FirestoreProperty("performanceScore")]
    public int PerformanceScore { get; set; }
}
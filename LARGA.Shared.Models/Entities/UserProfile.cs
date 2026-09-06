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

    [FirestoreProperty("phoneNumber", ConverterType = typeof(LenientStringConverter))]
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

    // Nullable: these don't apply to Manager-role accounts, whose documents legitimately
    // store an explicit Firestore null here rather than a number - a non-nullable int/double
    // throws ArgumentException("Unable to convert null value...") when that happens.
    [FirestoreProperty("currentArrears")]
    public double? CurrentArrears { get; set; }

    [FirestoreProperty("performanceScore")]
    public int? PerformanceScore { get; set; }

    [FirestoreProperty("deviceTokens")]
    public List<string> DeviceTokens { get; set; } = new();

    // Added for Driver & Shift Management (ManagerWeb) - see docs/ERD.md.
    [FirestoreProperty("address")]
    public string? Address { get; set; }

    [FirestoreProperty("dateJoined")]
    public DateTime? DateJoined { get; set; }

    [FirestoreProperty("ltoIdPhotoUrl")]
    public string? LtoIdPhotoUrl { get; set; }

    // Free-text note a manager leaves for a driver. Written here; nothing in
    // LARGA.MobileApp displays it yet - that's a separate, future mobile-side build.
    [FirestoreProperty("managerNote")]
    public string? ManagerNote { get; set; }

    // Set true when a manager creates a driver's account with a temporary password.
    // Nothing enforces this on login yet (mobile-side work, not yet built) - it's here
    // so that enforcement has something to check whenever it is built.
    [FirestoreProperty("mustChangePassword")]
    public bool MustChangePassword { get; set; }
}
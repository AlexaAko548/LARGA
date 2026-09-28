using LARGA.Shared.Models.Entities;
using Microsoft.Maui.Storage;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LARGA.SharedCore.Services;

public interface IShiftManagementService
{
    Task<bool> CreateShiftScheduleAsync(ShiftSchedule schedule);
    Task<string> StartShiftLogAsync(ShiftLog shift);
    Task<bool> UpdateTaxiStatusAsync(string taxiId, string newStatus);
    Task<TaxiUnit> GetTaxiUnitAsync(string taxiId);
    Task<TaxiUnit> GetCurrentUserAssignedTaxiAsync();
    Task<string> ClockInAsync(string taxiId, int startMileage);
    Task ClockOutAsync(string shiftDocumentId, int endMileage, string managerNote = "");
    Task SetOnBreakAsync(string shiftDocumentId, bool isOnBreak);
    Task<bool> SubmitHandoverChecklistAsync(HandoverChecklistSubmission checklist);
}

/// <summary>
/// One pre-/end-shift inspection as the driver completed it on the phone, written to
/// handover_checklists so ManagerWeb's Shift Logs can show it. Plain values here; the
/// Firestore string encodings (e.g. "Pre-Shift", "Half-tank") are applied on write to match
/// HandoverChecklist's converters.
/// </summary>
public class HandoverChecklistSubmission
{
    public string ShiftId { get; set; } = string.Empty;
    public bool IsEndShift { get; set; }
    public bool TireCondition { get; set; }
    public bool UnderTheHood { get; set; }
    public bool LightsCondition { get; set; }
    public bool InteriorCleanliness { get; set; }
    public bool ExteriorCondition { get; set; }
    public bool IsBelowHalfTank { get; set; }
    public string? FuelDashboardUrl { get; set; }
    public string? OdometerPhotoUrl { get; set; }
}

public class ShiftManagementService : IShiftManagementService
{
    public async Task<bool> CreateShiftScheduleAsync(ShiftSchedule schedule)
    {
        try
        {
            await CrossFirebaseFirestore.Current
                .GetCollection("shift_schedules")
                .AddDocumentAsync(schedule);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Schedule Error: {ex.Message}");
            return false;
        }
    }

    public async Task<string> StartShiftLogAsync(ShiftLog shift)
    {
        try
        {
            var docRef = await CrossFirebaseFirestore.Current
                .GetCollection("shifts")
                .AddDocumentAsync(shift);
            return docRef.Id;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Shift Error: {ex.Message}");
            return string.Empty;
        }
    }

    public async Task<bool> UpdateTaxiStatusAsync(string taxiId, string newStatus)
    {
        try
        {
            await CrossFirebaseFirestore.Current
                .GetCollection("taxis")
                .GetDocument(taxiId)
                .UpdateDataAsync(new Dictionary<object, object> { { "status", newStatus } });
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Taxi Status Error: {ex.Message}");
            return false;
        }
    }

    public async Task<TaxiUnit> GetTaxiUnitAsync(string taxiId)
    {
        try
        {
            var document = await CrossFirebaseFirestore.Current
                .GetCollection("taxis")
                .GetDocument(taxiId)
                .GetDocumentSnapshotAsync<TaxiUnitProxy>();

            if (document != null && document.Data != null)
            {
                var data = document.Data;
                return new TaxiUnit
                {
                    DocumentId = document.Reference.Id,
                    TaxiId = data.TaxiId ?? string.Empty,
                    Model = data.Model ?? string.Empty,
                    PlateNumber = data.PlateNumber ?? string.Empty,
                    Status = data.Status ?? string.Empty,
                    YearManufactured = data.YearManufactured
                };
            }
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Fetch Taxi Error: {ex.Message}");
            return null;
        }
    }

    public async Task<TaxiUnit> GetCurrentUserAssignedTaxiAsync()
    {
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user == null) return null;

        var profile = await CrossFirebaseFirestore.Current
            .GetCollection("users")
            .GetDocument(user.Uid)
            .GetDocumentSnapshotAsync<UserProfileProxy>();

        if (string.IsNullOrWhiteSpace(profile?.Data?.AssignedTaxiId)) return null;

        return await GetTaxiUnitAsync(profile.Data.AssignedTaxiId);
    }

    public async Task<string> ClockInAsync(string taxiId, int startMileage)
    {
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user == null) throw new Exception("No authenticated driver found.");

        var shiftProxy = new ShiftLogProxy
        {
            DriverId = user.Uid,
            TaxiId = taxiId,
            ShiftStart = DateTime.UtcNow,
            StartMileage = startMileage,
            Status = "Active",
            ShiftId = string.Empty,
            IsOnBreak = false,
            ManagerNote = ""
        };

        var documentReference = await CrossFirebaseFirestore.Current
            .GetCollection("shifts")
            .AddDocumentAsync(shiftProxy);

        // Everything else that points at a shift (fuel_logs, handover_checklists,
        // boundary_payments, ManagerWeb's lookups) uses the shiftId field and the document ID
        // interchangeably, so they must be the same value. The ID only exists once the
        // document does, hence the follow-up write.
        try
        {
            await documentReference.UpdateDataAsync(new Dictionary<object, object> { { "shiftId", documentReference.Id } });
        }
        catch (Exception ex)
        {
            // The shift itself is started - ManagerWeb falls back to the document ID when
            // shiftId is missing, so this isn't worth failing the clock-in over.
            System.Diagnostics.Debug.WriteLine($"Shift Id Backfill Error: {ex.Message}");
        }

        return documentReference.Id;
    }

    public async Task SetOnBreakAsync(string shiftDocumentId, bool isOnBreak)
    {
        try
        {
            await CrossFirebaseFirestore.Current
                .GetCollection("shifts")
                .GetDocument(shiftDocumentId)
                .UpdateDataAsync(new Dictionary<object, object> { { "isOnBreak", isOnBreak } });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Break Status Error: {ex.Message}");
        }
    }

    public async Task<bool> SubmitHandoverChecklistAsync(HandoverChecklistSubmission checklist)
    {
        try
        {
            var proxy = new HandoverChecklistProxy
            {
                ShiftId = checklist.ShiftId,
                ChecklistType = checklist.IsEndShift ? "End-Shift" : "Pre-Shift",
                TireCondition = checklist.TireCondition,
                OilLevel = checklist.UnderTheHood,
                CoolantLevel = checklist.UnderTheHood,
                LightsCondition = checklist.LightsCondition,
                InteriorCleanliness = checklist.InteriorCleanliness,
                ExteriorScratches = checklist.ExteriorCondition,
                FuelVerification = checklist.IsBelowHalfTank ? "Below half-tank" : "Half-tank",
                FuelDashboardUrl = checklist.FuelDashboardUrl ?? string.Empty,
                OdometerPhotoUrl = checklist.OdometerPhotoUrl ?? string.Empty,
                Timestamp = DateTime.UtcNow,
            };

            // Deterministic ID (one Pre + one End per shift, same "{shiftId}_PRE" convention
            // as the seed data) so a retried submit overwrites instead of duplicating.
            string docId = $"{checklist.ShiftId}_{(checklist.IsEndShift ? "END" : "PRE")}";
            await CrossFirebaseFirestore.Current
                .GetCollection("handover_checklists")
                .GetDocument(docId)
                .SetDataAsync(proxy);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Checklist Submit Error: {ex.Message}");
            return false;
        }
    }

    public async Task ClockOutAsync(string activeShiftId, int endMileage, string managerNote = "")
    {
        try
        {
            // CORRECTED: Keys now match the exact expected Firestore schema
            var updateData = new Dictionary<object, object>
        {
            { "shiftEnd", DateTime.UtcNow },
            { "endMileage", endMileage },
            { "status", "Completed" },
            { "managerNote", managerNote }
        };

            await CrossFirebaseFirestore.Current
                .GetCollection("shifts")
                .GetDocument(activeShiftId)
                .UpdateDataAsync(updateData);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Service Error: {ex.Message}");
            throw;
        }
    }

    public class TaxiUnitProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("taxiId")]
        public string TaxiId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("model")]
        public string Model { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("plateNumber")]
        public string PlateNumber { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("status")]
        public string Status { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("yearManufactured")]
        public int YearManufactured { get; set; }
    }

    private class UserProfileProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("assignedTaxiId")]
        public string AssignedTaxiId { get; set; }
    }

    private class HandoverChecklistProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("shiftId")]
        public string ShiftId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("checklistType")]
        public string ChecklistType { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("tireCondition")]
        public bool TireCondition { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("oilLevel")]
        public bool OilLevel { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("coolantLevel")]
        public bool CoolantLevel { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("lightsCondition")]
        public bool LightsCondition { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("interiorCleanliness")]
        public bool InteriorCleanliness { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("exteriorScratches")]
        public bool ExteriorScratches { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("fuelVerification")]
        public string FuelVerification { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("fuelDashboardUrl")]
        public string FuelDashboardUrl { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("odometerPhotoUrl")]
        public string OdometerPhotoUrl { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    private class ShiftLogProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("driverId")]
        public string DriverId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("taxiId")]
        public string TaxiId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("shiftStart")]
        public DateTime ShiftStart { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("startMileage")]
        public int StartMileage { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("status")]
        public string Status { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("shiftId")]
        public string ShiftId { get; set; }

        // ADDED: Missing fields for initial clock-in
        [Plugin.Firebase.Firestore.FirestoreProperty("isOnBreak")]
        public bool IsOnBreak { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("managerNote")]
        public string ManagerNote { get; set; }
    }
}
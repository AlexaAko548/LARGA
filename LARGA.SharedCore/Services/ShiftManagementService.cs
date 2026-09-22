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
            ShiftId = $"SHIFT_{DateTime.Now:yyyyMMdd}_{new Random().Next(100, 999)}",
            IsOnBreak = false, // ADDED
            ManagerNote = ""   // ADDED
        };

        var documentReference = await CrossFirebaseFirestore.Current
            .GetCollection("shifts")
            .AddDocumentAsync(shiftProxy);

        return documentReference.Id;
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
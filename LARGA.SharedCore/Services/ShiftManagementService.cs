using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Plugin.Firebase.Firestore;
using Plugin.Firebase.Auth;
using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore.Services;

public interface IShiftManagementService
{
    Task<bool> CreateShiftScheduleAsync(ShiftSchedule schedule);
    Task<string> StartShiftLogAsync(ShiftLog shift);
    Task<bool> UpdateTaxiStatusAsync(string taxiId, string newStatus);
    Task<TaxiUnit> GetTaxiUnitAsync(string taxiId);
    Task<TaxiUnit> GetCurrentUserAssignedTaxiAsync();
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
                .AddDocumentAsync(shift);            return docRef.Id;
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
                .GetDocumentSnapshotAsync<TaxiUnitProxy>(); // FIX: Use the mobile proxy

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
        if (user == null)
        {
            return null;
        }

        var profile = await CrossFirebaseFirestore.Current
            .GetCollection("users")
            .GetDocument(user.Uid)
            .GetDocumentSnapshotAsync<UserProfileProxy>();

        if (string.IsNullOrWhiteSpace(profile?.Data?.AssignedTaxiId))
        {
            return null;
        }

        return await GetTaxiUnitAsync(profile.Data.AssignedTaxiId);
    }

    // Proxy class using mobile-specific Plugin.Firebase attributes
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
}
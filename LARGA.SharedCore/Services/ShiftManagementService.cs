using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Plugin.Firebase.Firestore;
using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore.Services;

public interface IShiftManagementService
{
    Task<bool> CreateShiftScheduleAsync(ShiftSchedule schedule);
    Task<string> StartShiftLogAsync(ShiftLog shift);
    Task<bool> UpdateTaxiStatusAsync(string taxiId, string newStatus);
    Task<TaxiUnit> GetTaxiUnitAsync(string taxiId);
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
                .GetCollection("shift_logs")
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
            // Explicitly request a Dictionary to prevent mobile SDK deserialization crashes with web attributes
            var document = await CrossFirebaseFirestore.Current
                .GetCollection("taxis")
                .GetDocument(taxiId)
                .GetDocumentSnapshotAsync<Dictionary<string, object>>();

            if (document != null && document.Data != null)
            {
                return new TaxiUnit
                {
                    DocumentId = document.Reference.Id,
                    TaxiId = document.Data.ContainsKey("taxiId") ? document.Data["taxiId"]?.ToString() : string.Empty,
                    Model = document.Data.ContainsKey("model") ? document.Data["model"]?.ToString() : string.Empty,
                    PlateNumber = document.Data.ContainsKey("plateNumber") ? document.Data["plateNumber"]?.ToString() : string.Empty,
                    Status = document.Data.ContainsKey("status") ? document.Data["status"]?.ToString() : string.Empty,
                    YearManufactured = document.Data.ContainsKey("yearManufactured") ? Convert.ToInt32(document.Data["yearManufactured"]) : 0
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
}
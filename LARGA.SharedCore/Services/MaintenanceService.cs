using System;
using System.Threading.Tasks;
using Plugin.Firebase.Firestore;
using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore.Services;

public interface IMaintenanceService
{
    Task<string> SubmitMaintenanceRecordAsync(MaintenanceRecord record);
}

public class MaintenanceService : IMaintenanceService
{
    public async Task<string> SubmitMaintenanceRecordAsync(MaintenanceRecord record)
    {
        try
        {
            var docRef = await CrossFirebaseFirestore.Current
                .GetCollection("maintenance_logs")
                .AddDocumentAsync(record);
            return docRef.Id;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Maintenance Record Error: {ex.Message}");
            return string.Empty;
        }
    }
}
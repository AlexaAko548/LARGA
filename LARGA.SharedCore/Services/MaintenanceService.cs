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
            var proxy = new MaintenanceRecordProxy
            {
                TaxiId = record.TaxiId,
                ManagerId = record.ManagerId ?? string.Empty,
                MaintenanceType = record.MaintenanceType.ToString(),
                IssueTitle = record.IssueTitle,
                IssueDescription = record.IssueDescription,
                DateLogged = record.DateLogged,
                PriorityLevel = record.PriorityLevel.ToString(),
                SupportingPhotoUrl = record.SupportingPhotoUrl ?? string.Empty,
                ReportedByDriverId = record.ReportedByDriverId ?? string.Empty,
                Status = record.Status
            };

            var docRef = await CrossFirebaseFirestore.Current
                .GetCollection("maintenance_logs")
                .AddDocumentAsync(proxy);

            return docRef.Id;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Maintenance Record Error: {ex.Message}");
            return string.Empty;
        }
    }

    private class MaintenanceRecordProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("taxiId")]
        public string TaxiId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("managerId")]
        public string ManagerId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("maintenanceType")]
        public string MaintenanceType { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("issueTitle")]
        public string IssueTitle { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("issueDescription")]
        public string IssueDescription { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("dateLogged")]
        public DateTime DateLogged { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("priorityLevel")]
        public string PriorityLevel { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("supportingPhotoUrl")]
        public string SupportingPhotoUrl { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("reportedByDriverId")]
        public string ReportedByDriverId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("status")]
        public string Status { get; set; }
    }
}
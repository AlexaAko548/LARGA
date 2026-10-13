using System;
using System.Linq;
using System.Threading.Tasks;
using Plugin.Firebase.Firestore;
using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore.Services;

public interface IMaintenanceService
{
    Task<string> SubmitMaintenanceRecordAsync(MaintenanceRecord record);

    /// <summary>LAR-124: the id of a report this driver already filed for the same unit and title
    /// within <paramref name="window"/> that's still pending, or null. Lets a re-tapped Submit
    /// (slow connection) count as the same report instead of filing a copy.</summary>
    Task<string?> FindRecentDuplicateReportAsync(string driverId, string taxiId, string issueTitle, TimeSpan window);
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
                ShiftId = record.ShiftId ?? string.Empty,
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

    public async Task<string?> FindRecentDuplicateReportAsync(string driverId, string taxiId, string issueTitle, TimeSpan window)
    {
        if (string.IsNullOrWhiteSpace(driverId))
        {
            return null;
        }

        try
        {
            // One equality filter (no composite index); drivers may read maintenance_logs.
            var snapshot = await CrossFirebaseFirestore.Current
                .GetCollection("maintenance_logs")
                .WhereEqualsTo("reportedByDriverId", driverId)
                .GetDocumentsAsync<DuplicateCheckProxy>();

            DateTimeOffset since = DateTimeOffset.UtcNow - window;
            string title = issueTitle.Trim();
            return snapshot.Documents
                .Where(d => d.Data is not null
                    && string.Equals(d.Data.Status, "Reported", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(d.Data.TaxiId, taxiId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(d.Data.IssueTitle?.Trim(), title, StringComparison.OrdinalIgnoreCase)
                    && d.Data.DateLogged >= since)
                .Select(d => d.Reference.Id)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            // Can't check (e.g. offline): let the submit go ahead - the button lock still stops repeat taps.
            System.Diagnostics.Debug.WriteLine($"Duplicate report check failed: {ex.Message}");
            return null;
        }
    }

    private class DuplicateCheckProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("taxiId")]
        public string TaxiId { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("issueTitle")]
        public string IssueTitle { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("status")]
        public string Status { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("dateLogged")]
        public DateTimeOffset DateLogged { get; set; }
    }

    private class MaintenanceRecordProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("taxiId")]
        public string TaxiId { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("shiftId")]
        public string ShiftId { get; set; }

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
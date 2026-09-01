using LARGA.Shared.Models;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.Services;

public class FirestoreService
{
    private const string ChecklistsCollection = "handoverChecklists";
    private const string ShiftLogsCollection = "shiftLogs";
    private const string MaintenanceRecordsCollection = "maintenanceRecords";

    public async Task<string> SaveHandoverChecklistAsync(HandoverChecklist checklist)
    {
        var data = new Dictionary<string, object>
        {
            { "shiftId", checklist.ShiftId },
            { "checklistType", checklist.ChecklistType.ToString() },
            { "tireCondition", checklist.TireCondition },
            { "oilLevel", checklist.OilLevel },
            { "coolantLevel", checklist.CoolantLevel },
            { "interiorCleanliness", checklist.InteriorCleanliness },
            { "exteriorScratches", checklist.ExteriorScratches },
            { "fuelVerification", checklist.FuelVerification.ToString() },
            { "scratchesPhotoUrl", checklist.ScratchesPhotoUrl ?? string.Empty },
            { "fuelDashboardUrl", checklist.FuelDashboardUrl ?? string.Empty },
            { "timestamp", checklist.Timestamp }
        };

        var docRef = CrossFirebaseFirestore.Current
            .GetCollection(ChecklistsCollection)
            .GetDocument();

        await docRef.SetDataAsync(data);
        return docRef.Id;
    }

    public async Task<List<Dictionary<string, object>>> GetPendingShiftApprovalsAsync()
    {
        var snapshot = await CrossFirebaseFirestore.Current
            .GetCollection(ShiftLogsCollection)
            .WhereEqualsTo("status", "PendingApproval")
            .GetDocumentsAsync();

        var results = new List<Dictionary<string, object>>();
        foreach (var doc in snapshot.Documents)
        {
            results.Add(doc.Data);
        }
        return results;
    }

    public async Task<string> CreateMaintenanceRecordAsync(MaintenanceRecord record)
    {
        var data = new Dictionary<string, object>
        {
            { "taxiId", record.TaxiId },
            { "shiftId", record.ShiftId },
            { "maintenanceType", record.MaintenanceType.ToString() },
            { "issueTitle", record.IssueTitle },
            { "issueDescription", record.IssueDescription ?? string.Empty },
            { "dateLogged", record.DateLogged },
            { "supportingPhotoUrl", record.SupportingPhotoUrl ?? string.Empty },
            { "priorityLevel", record.PriorityLevel.ToString() }
        };

        var docRef = CrossFirebaseFirestore.Current
            .GetCollection(MaintenanceRecordsCollection)
            .GetDocument();

        await docRef.SetDataAsync(data);
        return docRef.Id;
    }

    public async Task UpdateShiftLogStatusAsync(string shiftId, string newStatus, string? managerNote = null)
    {
        var data = new Dictionary<string, object>
        {
            { "status", newStatus }
        };
        if (managerNote != null)
        {
            data["managerNote"] = managerNote;
        }

        await CrossFirebaseFirestore.Current
            .GetCollection(ShiftLogsCollection)
            .GetDocument(shiftId)
            .UpdateDataAsync(data);
    }
}
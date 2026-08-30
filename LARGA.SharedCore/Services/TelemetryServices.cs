using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LARGA.SharedCore.Services;

public class TelemetryService
{
    private readonly FirestoreDb _firestoreDb;
    private const string TelemetryCollection = "GpsTelemetry";
    private const string AlertsCollection = "EmergencyAlerts";

    public TelemetryService(FirestoreDb firestoreDb)
    {
        _firestoreDb = firestoreDb;
    }

    #region GPS Telemetry

    public async Task<string> RecordGpsPointAsync(GpsTelemetry telemetry)
    {
        CollectionReference collection = _firestoreDb.Collection(TelemetryCollection);

        if (string.IsNullOrWhiteSpace(telemetry.LogId))
        {
            DocumentReference docRef = await collection.AddAsync(telemetry);
            telemetry.LogId = docRef.Id;
            return docRef.Id;
        }

        DocumentReference existingDoc = collection.Document(telemetry.LogId);
        await existingDoc.SetAsync(telemetry, SetOptions.Overwrite);
        return telemetry.LogId;
    }

    public async Task<List<GpsTelemetry>> GetTelemetryByShiftIdAsync(string shiftId)
    {
        Query query = _firestoreDb.Collection(TelemetryCollection)
            .WhereEqualTo("shiftId", shiftId)
            .OrderBy("timestamp");

        QuerySnapshot snapshot = await query.GetSnapshotAsync();
        List<GpsTelemetry> results = new();

        foreach (DocumentSnapshot doc in snapshot.Documents)
        {
            results.Add(doc.ConvertTo<GpsTelemetry>());
        }
        return results;
    }

    #endregion

    #region Emergency Alerts

    public async Task<string> TriggerAlertAsync(EmergencyAlert alert)
    {
        CollectionReference collection = _firestoreDb.Collection(AlertsCollection);

        if (string.IsNullOrWhiteSpace(alert.AlertId))
        {
            DocumentReference docRef = await collection.AddAsync(alert);
            alert.AlertId = docRef.Id;
            return docRef.Id;
        }

        DocumentReference existingDoc = collection.Document(alert.AlertId);
        await existingDoc.SetAsync(alert, SetOptions.Overwrite);
        return alert.AlertId;
    }

    public async Task<List<EmergencyAlert>> GetActiveAlertsAsync()
    {
        Query query = _firestoreDb.Collection(AlertsCollection)
            .WhereEqualTo("isResolved", false)
            .OrderByDescending("timestamp");

        QuerySnapshot snapshot = await query.GetSnapshotAsync();
        List<EmergencyAlert> results = new();

        foreach (DocumentSnapshot doc in snapshot.Documents)
        {
            results.Add(doc.ConvertTo<EmergencyAlert>());
        }
        return results;
    }

    public async Task ResolveAlertAsync(string alertId)
    {
        DocumentReference docRef = _firestoreDb.Collection(AlertsCollection).Document(alertId);
        await docRef.UpdateAsync(new Dictionary<string, object>
        {
            { "isResolved", true }
        });
    }

    #endregion
}
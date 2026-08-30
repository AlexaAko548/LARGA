using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LARGA.SharedCore.Services;

public class FleetService
{
    private readonly FirestoreDb _firestoreDb;
    private const string TaxiUnitsCollection = "TaxiUnits";
    private const string MaintenanceCollection = "MaintenanceRecords";
    private const string SparePartsCollection = "SpareParts";
    private const string PartsUsedCollection = "MaintenancePartsUsed";

    public FleetService(FirestoreDb firestoreDb)
    {
        _firestoreDb = firestoreDb;
    }

    #region Taxi Units

    public async Task<string> UpsertTaxiUnitAsync(TaxiUnit taxiUnit)
    {
        CollectionReference collection = _firestoreDb.Collection(TaxiUnitsCollection);

        if (string.IsNullOrWhiteSpace(taxiUnit.TaxiId))
        {
            DocumentReference docRef = await collection.AddAsync(taxiUnit);
            taxiUnit.TaxiId = docRef.Id;
            return docRef.Id;
        }

        DocumentReference existingDoc = collection.Document(taxiUnit.TaxiId);
        await existingDoc.SetAsync(taxiUnit, SetOptions.Overwrite);
        return taxiUnit.TaxiId;
    }

    public async Task<TaxiUnit?> GetTaxiUnitByIdAsync(string taxiId)
    {
        DocumentReference docRef = _firestoreDb.Collection(TaxiUnitsCollection).Document(taxiId);
        DocumentSnapshot snapshot = await docRef.GetSnapshotAsync();

        return snapshot.Exists ? snapshot.ConvertTo<TaxiUnit>() : null;
    }

    public async Task<List<TaxiUnit>> GetAllTaxiUnitsAsync()
    {
        QuerySnapshot snapshot = await _firestoreDb.Collection(TaxiUnitsCollection).GetSnapshotAsync();
        List<TaxiUnit> results = new();

        foreach (DocumentSnapshot doc in snapshot.Documents)
        {
            results.Add(doc.ConvertTo<TaxiUnit>());
        }
        return results;
    }

    #endregion

    #region Maintenance Records

    public async Task<string> CreateMaintenanceRecordAsync(MaintenanceRecord record)
    {
        CollectionReference collection = _firestoreDb.Collection(MaintenanceCollection);

        if (string.IsNullOrWhiteSpace(record.MaintenanceId))
        {
            DocumentReference docRef = await collection.AddAsync(record);
            record.MaintenanceId = docRef.Id;
            return docRef.Id;
        }

        DocumentReference existingDoc = collection.Document(record.MaintenanceId);
        await existingDoc.SetAsync(record, SetOptions.Overwrite);
        return record.MaintenanceId;
    }

    public async Task<List<MaintenanceRecord>> GetMaintenanceByTaxiIdAsync(string taxiId)
    {
        Query query = _firestoreDb.Collection(MaintenanceCollection).WhereEqualTo("taxiId", taxiId);
        QuerySnapshot snapshot = await query.GetSnapshotAsync();

        List<MaintenanceRecord> results = new();
        foreach (DocumentSnapshot doc in snapshot.Documents)
        {
            results.Add(doc.ConvertTo<MaintenanceRecord>());
        }
        return results;
    }

    #endregion

    #region Spare Parts & Usage

    public async Task<string> UpsertSparePartAsync(SparePart part)
    {
        CollectionReference collection = _firestoreDb.Collection(SparePartsCollection);

        if (string.IsNullOrWhiteSpace(part.PartId))
        {
            DocumentReference docRef = await collection.AddAsync(part);
            part.PartId = docRef.Id;
            return docRef.Id;
        }

        DocumentReference existingDoc = collection.Document(part.PartId);
        await existingDoc.SetAsync(part, SetOptions.Overwrite);
        return part.PartId;
    }

    public async Task<List<SparePart>> GetAllSparePartsAsync()
    {
        QuerySnapshot snapshot = await _firestoreDb.Collection(SparePartsCollection).GetSnapshotAsync();
        List<SparePart> results = new();

        foreach (DocumentSnapshot doc in snapshot.Documents)
        {
            results.Add(doc.ConvertTo<SparePart>());
        }
        return results;
    }

    public async Task<string> LogPartUsageAsync(MaintenancePartsUsed partsUsed)
    {
        CollectionReference collection = _firestoreDb.Collection(PartsUsedCollection);

        if (string.IsNullOrWhiteSpace(partsUsed.UsageId))
        {
            DocumentReference docRef = await collection.AddAsync(partsUsed);
            partsUsed.UsageId = docRef.Id;
            return docRef.Id;
        }

        DocumentReference existingDoc = collection.Document(partsUsed.UsageId);
        await existingDoc.SetAsync(partsUsed, SetOptions.Overwrite);
        return partsUsed.UsageId;
    }

    #endregion
}
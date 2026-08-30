using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LARGA.SharedCore.Services;

public class FinancialService
{
    private readonly FirestoreDb _firestoreDb;
    private const string BoundaryCollection = "BoundaryPayments";
    private const string FuelLogCollection = "FuelLogs";

    public FinancialService(FirestoreDb firestoreDb)
    {
        _firestoreDb = firestoreDb;
    }

    #region Boundary Payments

    public async Task<string> RecordBoundaryPaymentAsync(BoundaryPayment payment)
    {
        CollectionReference collection = _firestoreDb.Collection(BoundaryCollection);

        if (string.IsNullOrWhiteSpace(payment.PaymentId))
        {
            DocumentReference docRef = await collection.AddAsync(payment);
            payment.PaymentId = docRef.Id;
            return docRef.Id;
        }

        DocumentReference existingDoc = collection.Document(payment.PaymentId);
        await existingDoc.SetAsync(payment, SetOptions.Overwrite);
        return payment.PaymentId;
    }

    public async Task<BoundaryPayment?> GetBoundaryPaymentByIdAsync(string paymentId)
    {
        DocumentReference docRef = _firestoreDb.Collection(BoundaryCollection).Document(paymentId);
        DocumentSnapshot snapshot = await docRef.GetSnapshotAsync();

        return snapshot.Exists ? snapshot.ConvertTo<BoundaryPayment>() : null;
    }

    public async Task<List<BoundaryPayment>> GetBoundaryPaymentsByShiftIdAsync(string shiftId)
    {
        Query query = _firestoreDb.Collection(BoundaryCollection).WhereEqualTo("shiftId", shiftId);
        QuerySnapshot snapshot = await query.GetSnapshotAsync();

        List<BoundaryPayment> results = new();
        foreach (DocumentSnapshot document in snapshot.Documents)
        {
            results.Add(document.ConvertTo<BoundaryPayment>());
        }
        return results;
    }

    #endregion

    #region Fuel Logs

    public async Task<string> RecordFuelLogAsync(FuelLog fuelLog)
    {
        CollectionReference collection = _firestoreDb.Collection(FuelLogCollection);

        if (string.IsNullOrWhiteSpace(fuelLog.FuelId))
        {
            DocumentReference docRef = await collection.AddAsync(fuelLog);
            fuelLog.FuelId = docRef.Id;
            return docRef.Id;
        }

        DocumentReference existingDoc = collection.Document(fuelLog.FuelId);
        await existingDoc.SetAsync(fuelLog, SetOptions.Overwrite);
        return fuelLog.FuelId;
    }

    public async Task<FuelLog?> GetFuelLogByIdAsync(string fuelId)
    {
        DocumentReference docRef = _firestoreDb.Collection(FuelLogCollection).Document(fuelId);
        DocumentSnapshot snapshot = await docRef.GetSnapshotAsync();

        return snapshot.Exists ? snapshot.ConvertTo<FuelLog>() : null;
    }

    public async Task<List<FuelLog>> GetFuelLogsByShiftIdAsync(string shiftId)
    {
        Query query = _firestoreDb.Collection(FuelLogCollection).WhereEqualTo("shiftId", shiftId);
        QuerySnapshot snapshot = await query.GetSnapshotAsync();

        List<FuelLog> results = new();
        foreach (DocumentSnapshot document in snapshot.Documents)
        {
            results.Add(document.ConvertTo<FuelLog>());
        }
        return results;
    }

    public async Task UpdateFuelVerificationStatusAsync(string fuelId, FuelVerificationStatus status)
    {
        DocumentReference docRef = _firestoreDb.Collection(FuelLogCollection).Document(fuelId);
        var converter = new FuelVerificationStatusConverter();
        
        await docRef.UpdateAsync(new Dictionary<string, object>
        {
            { "verificationStatus", converter.ToFirestore(status) }
        });
    }

    #endregion
}
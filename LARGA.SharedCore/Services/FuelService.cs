using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LARGA.Shared.Models.Entities;
using Plugin.Firebase.Firestore;

namespace LARGA.SharedCore.Services;

public interface IFuelService
{
    Task<string> SubmitFuelReportAsync(FuelLog record);
}

public class FuelService : IFuelService
{
    public async Task<string> SubmitFuelReportAsync(FuelLog record)
    {
        try
        {
            // submittedAt is stamped by the Firestore server, not the phone (firestore.rules
            // refuse anything else) - it's the date the Past Fuel Reports list shows, separate
            // from receiptTimestamp (the date printed on the receipt). A dictionary, because
            // FieldValue doesn't fit a typed proxy (same as ShiftManagementService.ClockInAsync).
            var fields = new Dictionary<object, object>
            {
                { "shiftId", record.ShiftId },
                { "fuelStation", record.FuelStation ?? string.Empty },
                { "litersRefueled", (double)record.LitersRefueled },
                { "fuelCost", (double)record.FuelCost },
                { "orNumber", record.ORNumber ?? string.Empty },
                { "receiptImageUrl", record.ReceiptImageUrl ?? string.Empty },
                { "receiptTimestamp", record.ReceiptTimestamp ?? DateTime.UtcNow },
                { "submittedAt", FieldValue.ServerTimestamp() },
                { "verificationStatus", record.VerificationStatus.ToString() },
                { "fuelLogDetails", record.FuelLogDetails ?? string.Empty },
                { "odometerReading", record.OdometerReading },
                { "odometerPhotoUrl", record.OdometerPhotoUrl ?? string.Empty },
                { "driverId", record.DriverId },
                { "isCostManuallyEdited", record.IsCostManuallyEdited },
                { "isQuantityManuallyEdited", record.IsQuantityManuallyEdited },
                { "isFuelStationManuallyEdited", record.IsFuelStationManuallyEdited },
                { "isReceiptDateManuallyEdited", record.IsReceiptDateManuallyEdited },
                { "isOrNumberManuallyEdited", record.IsOrNumberManuallyEdited },
                { "isAnyFieldManuallyEdited", record.IsAnyFieldManuallyEdited },
            };

            IDocumentReference docRef = CrossFirebaseFirestore.Current
                .GetCollection("fuel_logs")
                .CreateDocument();
            await docRef.SetDataAsync(fields);

            return docRef.Id;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to submit fuel report: {ex.Message}");
            return string.Empty;
        }
    }
}

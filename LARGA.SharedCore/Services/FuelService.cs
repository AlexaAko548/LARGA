using System;
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
            // Map the domain entity to the Plugin.Firebase attributed proxy class
            var proxy = new FuelLogProxy
            {
                ShiftId = record.ShiftId,
                FuelStation = record.FuelStation ?? string.Empty,
                LitersRefueled = (double)record.LitersRefueled,
                FuelCost = (double)record.FuelCost,
                ORNumber = record.ORNumber ?? string.Empty,
                ReceiptImageUrl = record.ReceiptImageUrl ?? string.Empty,
                ReceiptTimestamp = record.ReceiptTimestamp ?? DateTime.UtcNow,
                VerificationStatus = record.VerificationStatus.ToString(),
                FuelLogDetails = record.FuelLogDetails ?? string.Empty,
                OdometerReading = record.OdometerReading,
                OdometerPhotoUrl = record.OdometerPhotoUrl ?? string.Empty,
                DriverId = record.DriverId,
                IsCostManuallyEdited = record.IsCostManuallyEdited,
                IsQuantityManuallyEdited = record.IsQuantityManuallyEdited,
                IsFuelStationManuallyEdited = record.IsFuelStationManuallyEdited,
                IsReceiptDateManuallyEdited = record.IsReceiptDateManuallyEdited,
                IsAnyFieldManuallyEdited = record.IsAnyFieldManuallyEdited
            };

            var docRef = await CrossFirebaseFirestore.Current
                .GetCollection("fuel_logs")
                .AddDocumentAsync(proxy);

            return docRef.Id;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to submit fuel report: {ex.Message}");
            return string.Empty;
        }
    }

    // Internal proxy using the correct Plugin.Firebase attributes
    private class FuelLogProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("shiftId")]
        public string ShiftId { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("fuelStation")]
        public string FuelStation { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("litersRefueled")]
        public double LitersRefueled { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("fuelCost")]
        public double FuelCost { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("orNumber")]
        public string ORNumber { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("receiptImageUrl")]
        public string ReceiptImageUrl { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("receiptTimestamp")]
        public DateTime? ReceiptTimestamp { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("verificationStatus")]
        public string VerificationStatus { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("fuelLogDetails")]
        public string FuelLogDetails { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("odometerReading")]
        public int OdometerReading { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("odometerPhotoUrl")]
        public string OdometerPhotoUrl { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("driverId")]
        public string DriverId { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("isCostManuallyEdited")]
        public bool IsCostManuallyEdited { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("isQuantityManuallyEdited")]
        public bool IsQuantityManuallyEdited { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("isFuelStationManuallyEdited")]
        public bool IsFuelStationManuallyEdited { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("isReceiptDateManuallyEdited")]
        public bool IsReceiptDateManuallyEdited { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("isAnyFieldManuallyEdited")]
        public bool IsAnyFieldManuallyEdited { get; set; }
    }
}
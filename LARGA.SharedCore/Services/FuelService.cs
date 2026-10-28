using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LARGA.Shared.Models.Entities;
using Plugin.Firebase.Firestore;

namespace LARGA.SharedCore.Services;

public interface IFuelService
{
    /// <summary>Saves the fuel log. <see cref="FuelSubmitResult.Id"/> is empty when it couldn't be saved.</summary>
    Task<FuelSubmitResult> SubmitFuelReportAsync(FuelLog record);
}

/// <summary>The saved fuel log's ID (empty on failure), and whether it went in Flagged because its OR
/// number had already been submitted (FuelOrNumberRules).</summary>
public record FuelSubmitResult(string Id, bool DuplicateOrNumber);

public class FuelService : IFuelService
{
    public async Task<FuelSubmitResult> SubmitFuelReportAsync(FuelLog record)
    {
        try
        {
            IFirebaseFirestore firestore = CrossFirebaseFirestore.Current;
            IDocumentReference docRef = firestore.GetCollection("fuel_logs").CreateDocument();

            // Proposal §4.2.4.5: the OR number is claimed in the same transaction as the log.
            string? orKey = FuelOrNumberRules.Normalize(record.ORNumber);
            IDocumentReference? claimRef = orKey is null ? null : firestore.GetCollection(FuelOrNumberRules.Collection).GetDocument(orKey);
            bool duplicate = false;

            // The body runs on a native thread and may be retried, so it only sets a flag (same as
            // QuickLedgerService's payment transaction).
            await firestore.RunTransactionAsync(transaction =>
            {
                // All reads before any write. Every claim carries a fuelLogId, so a missing one means no claim.
                duplicate = claimRef is not null
                    && !string.IsNullOrEmpty(transaction.GetDocument<OrClaimProxy>(claimRef)?.Data?.FuelLogId);

                transaction.SetData(docRef, Fields(record, duplicate), SetOptions.Merge());
                if (claimRef is not null && !duplicate)
                {
                    transaction.SetData(claimRef, new Dictionary<object, object>
                    {
                        ["fuelLogId"] = docRef.Id,
                        ["createdAt"] = FieldValue.ServerTimestamp(),
                    }, SetOptions.Merge());
                }
                return true;
            });

            return new FuelSubmitResult(docRef.Id, duplicate);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to submit fuel report: {ex.Message}");
            return new FuelSubmitResult(string.Empty, false);
        }
    }

    private static Dictionary<object, object> Fields(FuelLog record, bool duplicateOrNumber)
    {
        // submittedAt is stamped by the Firestore server, not the phone (firestore.rules
        // refuse anything else) - it's the date the Past Fuel Reports list shows, separate
        // from receiptTimestamp (the date printed on the receipt). A dictionary, because
        // FieldValue doesn't fit a typed proxy (same as ShiftManagementService.ClockInAsync).
        bool flagged = record.VerificationStatus == FuelVerificationStatus.Flagged || duplicateOrNumber;
        string? details = duplicateOrNumber
            ? FuelOrNumberRules.CombineReasons(new[] { record.FuelLogDetails, FuelOrNumberRules.DuplicateReason })
            : record.FuelLogDetails;

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
            { "verificationStatus", (flagged ? FuelVerificationStatus.Flagged : record.VerificationStatus).ToString() },
            { "fuelLogDetails", details ?? string.Empty },
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

        // Flagged on submit by the app's own checks (FUEL-004: not a Petron/Shell receipt; a
        // duplicate OR number) - firestore.rules accept Flagged from a driver only like this, at
        // server time.
        if (flagged)
        {
            fields["autoFlagged"] = true;
            fields["flaggedAt"] = FieldValue.ServerTimestamp();
        }

        return fields;
    }

    private class OrClaimProxy
    {
        [FirestoreProperty("fuelLogId")]
        public string? FuelLogId { get; set; }
    }
}

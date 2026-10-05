using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LARGA.SharedCore.Services;
using Plugin.Firebase.Auth;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Shared by the Pre-Shift and End-Shift step-2 screens: uploads the fuel-level and odometer
/// photos, then writes the inspection to handover_checklists so ManagerWeb's Shift Logs shows
/// it. Never throws - the shift has already started/ended by the time this runs, and a missing
/// checklist shouldn't undo that.
/// </summary>
public static class ShiftChecklistUploader
{
    public static async Task SubmitAsync(
        IShiftManagementService shiftService,
        IPhotoStorageService photoStorage,
        string shiftId,
        bool isEndShift,
        IReadOnlyDictionary<string, bool> inspection,
        bool isBelowHalfTank,
        string? fuelPhotoPath,
        string? odometerPhotoPath)
    {
        try
        {
            string driverId = CrossFirebaseAuth.Current.CurrentUser?.Uid ?? "unknown_driver";
            string prefix = $"handover_checklists/{driverId}/{shiftId}/{(isEndShift ? "end" : "pre")}";

            string? fuelUrl = await UploadIfPresentAsync(photoStorage, fuelPhotoPath, $"{prefix}_fuel.jpg");
            string? odometerUrl = await UploadIfPresentAsync(photoStorage, odometerPhotoPath, $"{prefix}_odometer.jpg");

            await shiftService.SubmitHandoverChecklistAsync(new HandoverChecklistSubmission
            {
                ShiftId = shiftId,
                IsEndShift = isEndShift,
                TireCondition = Passed(inspection, "Tires"),
                UnderTheHood = Passed(inspection, "Hood"),
                LightsCondition = Passed(inspection, "Lights"),
                InteriorCleanliness = Passed(inspection, "Interior"),
                ExteriorCondition = Passed(inspection, "Exterior"),
                IsBelowHalfTank = isBelowHalfTank,
                FuelDashboardUrl = fuelUrl,
                OdometerPhotoUrl = odometerUrl,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Shift Checklist Upload Error: {ex.Message}");
        }
    }

    private static bool Passed(IReadOnlyDictionary<string, bool> inspection, string item) =>
        inspection.TryGetValue(item, out bool passed) && passed;

    private static async Task<string?> UploadIfPresentAsync(IPhotoStorageService photoStorage, string? localPath, string storagePath)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            return null;
        }

        byte[] bytes = await File.ReadAllBytesAsync(localPath);
        return await photoStorage.UploadPhotoAsync(storagePath, bytes);
    }
}

using System;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Emergency;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Looks up the shift and the driver behind an SOS and runs SosCallerValidator, then stores the
/// outcome on the alert (callerVerified / callerVerificationReason) so every screen shows it.
/// </summary>
public class SosCallerVerificationService
{
    private readonly Lazy<FirestoreDb> _dbLazy;

    private FirestoreDb Db => _dbLazy.Value;

    public SosCallerVerificationService(Lazy<FirestoreDb> dbLazy)
    {
        _dbLazy = dbLazy;
    }

    public async Task<SosCallerValidator.Result> VerifyAsync(EmergencyAlert alert)
    {
        ShiftLog? shift = null;
        if (!string.IsNullOrWhiteSpace(alert.ShiftId))
        {
            DocumentSnapshot shiftDoc = await Db.Collection("shifts").Document(alert.ShiftId).GetSnapshotAsync();
            shift = shiftDoc.Exists ? shiftDoc.ConvertTo<ShiftLog>() : null;
        }

        // The driver whose number is checked is the one the shift is assigned to, not whoever
        // the alert claims to be.
        string driverId = shift?.DriverId ?? alert.DriverId;
        UserProfile? driver = null;
        if (!string.IsNullOrWhiteSpace(driverId))
        {
            DocumentSnapshot userDoc = await Db.Collection("users").Document(driverId).GetSnapshotAsync();
            driver = userDoc.Exists ? userDoc.ConvertTo<UserProfile>() : null;
        }

        return SosCallerValidator.Validate(alert, shift, driver);
    }

    public async Task<SosCallerValidator.Result> VerifyAndStoreAsync(DocumentReference alertRef, EmergencyAlert alert)
    {
        SosCallerValidator.Result result = await VerifyAsync(alert);
        await alertRef.UpdateAsync(new System.Collections.Generic.Dictionary<string, object>
        {
            ["callerCheck"] = result.Status,
            ["callerVerified"] = result.Verified,
            ["callerVerificationReason"] = result.Reason,
        });
        return result;
    }
}

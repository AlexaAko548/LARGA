using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Writes one entry to the `audit_logs` collection from the mobile app, using the same field
/// names as the server-side AuditLog entity. Not batched with the action it records: the
/// mobile Firestore client has no batch writes in this app yet, so a failed audit write is
/// logged but doesn't undo or block the action.
/// </summary>
public static class AuditLogWriter
{
    /// <summary>
    /// Starts an audit write and returns straight away. Use this from any flow the user is waiting
    /// on (SOS, alert resolve, defect submit) so a slow or offline Firestore never holds it up.
    /// WriteAsync already catches its own failures, so nothing is left unobserved.
    /// </summary>
    public static void Record(string actionType, string details) => _ = WriteAsync(actionType, details);

    public static async Task WriteAsync(string actionType, string details)
    {
        string uid = CrossFirebaseAuth.Current.CurrentUser?.Uid ?? string.Empty;
        try
        {
            // Server time, not the phone's: firestore.rules accept only an entry stamped by the server
            // (audit A-05), so a phone clock can't backdate one. A dictionary, because FieldValue
            // doesn't fit a typed proxy - the same shape QuickLedgerService.WriteAuditAsync writes.
            await CrossFirebaseFirestore.Current
                .GetCollection("audit_logs")
                .AddDocumentAsync(new Dictionary<object, object>
                {
                    ["userId"] = uid,
                    ["actionType"] = actionType,
                    ["auditLogDetails"] = details,
                    ["timestamp"] = FieldValue.ServerTimestamp(),
                });
        }
        catch (Exception ex)
        {
            // Debug.WriteLine is stripped from Release builds, so write to the Android log instead
            // to make failures visible in logcat (tag LARGA_AUDIT).
#if ANDROID
            // No uid: logcat survives Release builds, and the user isn't needed to diagnose a failed write (audit F-06).
            Android.Util.Log.Error("LARGA_AUDIT", $"Write failed ({actionType}): {ex}");
#else
            System.Diagnostics.Debug.WriteLine($"Audit Log Error ({actionType}): {ex}");
#endif
        }
    }
}

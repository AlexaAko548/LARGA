using System;
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
            await CrossFirebaseFirestore.Current
                .GetCollection("audit_logs")
                .AddDocumentAsync(new AuditLogProxy
                {
                    UserId = uid,
                    ActionType = actionType,
                    AuditLogDetails = details,
                    Timestamp = DateTime.UtcNow,
                });
        }
        catch (Exception ex)
        {
            // Debug.WriteLine is stripped from Release builds, so write to the Android log instead
            // to make failures visible in logcat (tag LARGA_AUDIT).
#if ANDROID
            Android.Util.Log.Error("LARGA_AUDIT", $"Write failed ({actionType}) for uid '{uid}': {ex}");
#else
            System.Diagnostics.Debug.WriteLine($"Audit Log Error ({actionType}): {ex}");
#endif
        }
    }

    // Class-based, like the other mobile Firestore writes: the plugin serializes these
    // [FirestoreProperty] fields reliably, where a Dictionary payload may not reach Firestore.
    private class AuditLogProxy
    {
        [FirestoreProperty("userId")]
        public string UserId { get; set; } = string.Empty;

        [FirestoreProperty("actionType")]
        public string ActionType { get; set; } = string.Empty;

        [FirestoreProperty("auditLogDetails")]
        public string AuditLogDetails { get; set; } = string.Empty;

        [FirestoreProperty("timestamp")]
        public DateTime Timestamp { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
using LARGA.SharedCore;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Keeps system_configs/global.managerPhoneNumbers - the numbers a driver's phone auto-answers
/// after an automated SOS (LAR-86/87) - in step with the managers' own profile numbers. Drivers
/// can't read managers' users documents (firestore.rules), so a manager's app adds its own number.
/// ManagerWeb also rebuilds the whole list on startup (ManagerPhoneAllowlistSyncService).
/// Best-effort throughout: a failure here never blocks the manager's screen or update.
/// </summary>
public static class ManagerAllowlistSync
{
    /// <summary>Adds the signed-in manager's profile number to the allowlist.</summary>
    public static async Task SyncCurrentManagerAsync()
    {
        try
        {
            IFirebaseUser? user = CrossFirebaseAuth.Current.CurrentUser;
            if (user == null) return;

            var profile = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(user.Uid)
                .GetDocumentSnapshotAsync<ManagerProfileProxy>();

            if (!string.Equals(profile?.Data?.Role, "Manager", StringComparison.OrdinalIgnoreCase)) return;

            string? number = InputValidator.NormalizePhilippineMobile(profile?.Data?.PhoneNumber);
            if (number == null) return;

            await UpdateAsync(FieldValue.ArrayUnion(number));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Manager allowlist sync failed: {ex.Message}");
        }
    }

    /// <summary>Swaps a manager's old number for their new one after a contact-number change.</summary>
    public static async Task ReplaceAsync(string? oldNumber, string? newNumber)
    {
        try
        {
            string? normalizedOld = InputValidator.NormalizePhilippineMobile(oldNumber);
            string? normalizedNew = InputValidator.NormalizePhilippineMobile(newNumber);

            if (normalizedOld != null && normalizedOld != normalizedNew)
            {
                await UpdateAsync(FieldValue.ArrayRemove(normalizedOld));
            }
            if (normalizedNew != null)
            {
                await UpdateAsync(FieldValue.ArrayUnion(normalizedNew));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Manager allowlist update failed: {ex.Message}");
        }
    }

    private static Task UpdateAsync(FieldValue change) =>
        CrossFirebaseFirestore.Current
            .GetCollection("system_configs")
            .GetDocument("global")
            .UpdateDataAsync(new Dictionary<object, object> { ["managerPhoneNumbers"] = change });

    private class ManagerProfileProxy
    {
        [FirestoreProperty("role")]
        public string Role { get; set; } = string.Empty;

        [FirestoreProperty("phoneNumber")]
        public string PhoneNumber { get; set; } = string.Empty;
    }
}

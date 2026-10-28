using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LARGA.SharedCore;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.Services;

/// <summary>users/{uid}.mustChangePassword (ForcedPasswordChangeRules): cleared after any password change
/// made in the app - the forced screen and the regular Change Password pages alike.</summary>
public static class MustChangePasswordFlag
{
    /// <summary>Best effort: the password is already changed, and if this write fails ManagerWeb clears the
    /// flag on its next start (MustChangePasswordCleanupService).</summary>
    public static async Task ClearAsync()
    {
        try
        {
            string? uid = CrossFirebaseAuth.Current.CurrentUser?.Uid;
            if (string.IsNullOrEmpty(uid)) return;

            await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(uid)
                .UpdateDataAsync(new Dictionary<object, object> { [ForcedPasswordChangeRules.Field] = false });
        }
        catch (Exception ex)
        {
            FirestoreErrors.Log("Clear mustChangePassword", ex);
        }
    }
}

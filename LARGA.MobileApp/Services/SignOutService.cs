using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Storage;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Logout for both apps (audit F-02). Fleet phones are shared, so a logout leaves nothing of this
/// user behind for the next one:
/// 1. live listeners stop (SessionEvents) and writes still queued (audit entries, payments made
///    offline) get a few seconds to reach the server while this user is still signed in;
/// 2. sign out;
/// 3. the Firestore offline cache is wiped - it isn't kept per user, and offline queries are
///    answered from it without the security rules;
/// 4. this user's shift IDs, unsent fuel-report draft and cached photos are deleted.
/// Kept: the theme, the Remember-Me email, the manager numbers the SOS auto-answer needs offline,
/// and the one-time full-screen prompt flag.
/// </summary>
public static class SignOutService
{
    private static readonly TimeSpan PendingWritesWait = TimeSpan.FromSeconds(5);

    private static readonly string[] ShiftKeys =
        { "IsShiftActive", "CurrentShiftId", "ShiftStartTime", ClockInPendingViewModel.PendingRequestKey,
          LastShiftStore.LastShiftIdKey, LastShiftStore.LastShiftEndedAtKey };

    private static readonly string[] PhotoExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".heic" };

    public static async Task SignOutAsync()
    {
        SessionEvents.RaiseSigningOut();

        IFirebaseFirestore firestore = CrossFirebaseFirestore.Current;
        bool writesFlushed = await FlushPendingWritesAsync(firestore);

        await CrossFirebaseAuth.Current.SignOutAsync();
        StaffSession.Role = null;

        // Clearing the cache would drop writes that never reached the server. When the phone is
        // offline and something is still queued, keep the cache (the write goes out on the next
        // connection) and clear the rest.
        if (writesFlushed)
        {
            await ClearFirestoreCacheAsync(firestore);
        }
        else
        {
            FirestoreErrors.Log("Sign-out cache clear", new TimeoutException(
                "Queued writes didn't reach the server before logout (offline?) - Firestore cache kept so they aren't lost."));
        }

        ClearLocalShiftData();
        DeleteCachedPhotos();
    }

    private static async Task<bool> FlushPendingWritesAsync(IFirebaseFirestore firestore)
    {
        try
        {
            Task flush = firestore.WaitForPendingWritesAsync();
            return await Task.WhenAny(flush, Task.Delay(PendingWritesWait)) == flush && !flush.IsFaulted;
        }
        catch (Exception ex)
        {
            FirestoreErrors.Log("Sign-out pending writes", ex);
            return false;
        }
    }

    private static async Task ClearFirestoreCacheAsync(IFirebaseFirestore firestore)
    {
        try
        {
            // The cache can only be cleared on a stopped instance; Restart brings Firestore back
            // for the next login.
            await firestore.TerminateAsync();
            await firestore.ClearPersistenceAsync();
        }
        catch (Exception ex)
        {
            FirestoreErrors.Log("Sign-out cache clear", ex);
        }
        finally
        {
            try
            {
                firestore.Restart();
            }
            catch (Exception ex)
            {
                FirestoreErrors.Log("Firestore restart after sign-out", ex);
            }
        }
    }

    private static void ClearLocalShiftData()
    {
        foreach (string key in ShiftKeys)
        {
            Preferences.Remove(key);
        }

        SecureStorage.Remove("ActiveShiftDocumentId");
        FuelReportViewModel.ClearPendingDraft();
        DriverDebtCalculator.ClearCache();
    }

    // Receipt, odometer, licence and checklist photos are copied to the cache folder before upload.
    private static void DeleteCachedPhotos()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(FileSystem.CacheDirectory, "*", SearchOption.AllDirectories)
                         .Where(f => PhotoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
            {
                try { File.Delete(file); } catch { /* in use - the OS clears the cache folder eventually */ }
            }
        }
        catch (Exception ex)
        {
            FirestoreErrors.Log("Sign-out photo cleanup", ex);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LARGA.MobileApp.ViewModels.Driver;
using LARGA.SharedCore.Push;
using LARGA.SharedCore.Services;
using Microsoft.Maui.Storage;
using Plugin.Firebase.Auth;
using Plugin.Firebase.CloudMessaging;
using Plugin.Firebase.Firestore;
#if ANDROID
using Android.Gms.Extensions;
#endif

namespace LARGA.MobileApp.Services;

/// <summary>
/// Logout for both apps (audit F-02). Fleet phones are shared, so a logout leaves nothing of this
/// user behind for the next one:
/// 1. live listeners stop (SessionEvents), this phone's push token is taken off the user's
///    users/{uid}/fcmTokens and the FCM token itself is deleted (so their chat and SOS pushes stop
///    reaching a phone someone else may use next), and writes still queued (audit entries, payments made offline) get a few seconds to
///    reach the server while this user is still signed in;
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
    private static readonly TimeSpan PushTokenWait = TimeSpan.FromSeconds(4);

    private static readonly string[] ShiftKeys =
        { "IsShiftActive", "CurrentShiftId", "ShiftStartTime", ClockInPendingViewModel.PendingRequestKey,
          LastShiftStore.LastShiftIdKey, LastShiftStore.LastShiftEndedAtKey };

    private static readonly string[] PhotoExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".heic" };

    public static async Task SignOutAsync()
    {
        SessionEvents.RaiseSigningOut();

        IFirebaseFirestore firestore = CrossFirebaseFirestore.Current;
        // Bounded: reading the token or the user doc mustn't hold up a logout with no signal.
        Task clearToken = ClearPushTokenAsync(firestore);
        await Task.WhenAny(clearToken, Task.Delay(PushTokenWait));
        bool writesFlushed = await FlushPendingWritesAsync(firestore);

        Task deleteFcmToken = DeleteFcmTokenAsync();
        await Task.WhenAny(deleteFcmToken, Task.Delay(PushTokenWait));

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

    /// <summary>
    /// Stops this account's pushes on this phone: first the phone forgets who's signed in (so the
    /// presenter drops any push for them from now on), then this phone's own
    /// users/{uid}/fcmTokens doc is deleted. Queued like any write, so it goes out with the flush
    /// below (or on the next connection). If it never lands, the FCM token itself is deleted next
    /// (<see cref="DeleteFcmTokenAsync"/>), so FCM rejects it and ManagerWeb removes the doc.
    /// </summary>
    private static async Task ClearPushTokenAsync(IFirebaseFirestore firestore)
    {
        try
        {
            Preferences.Default.Remove(PushTokenRules.SignedInUidPreference);

            string? uid = CrossFirebaseAuth.Current.CurrentUser?.Uid;
            if (string.IsNullOrEmpty(uid)) return;

            string deviceToken = Preferences.Default.Get(PushTokenRules.LastTokenPreference, string.Empty);
            if (string.IsNullOrEmpty(deviceToken))
            {
                deviceToken = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();
            }
            if (string.IsNullOrWhiteSpace(deviceToken)) return;

            Task clear = NotificationService.TokenDocument(uid, deviceToken).DeleteDocumentAsync();
            // Offline the write waits in the queue; the flush below decides how long to wait for it.
            _ = clear.ContinueWith(t => FirestoreErrors.Log("Sign-out push token clear", t.Exception!), TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            FirestoreErrors.Log("Sign-out push token clear", ex);
        }
    }

    /// <summary>
    /// Deletes this phone's FCM registration token, so nothing sent to it is delivered any more -
    /// whatever any Firestore doc still says. The next sign-in gets a fresh token. Offline it
    /// fails (logged) and the token is cleaned up when FCM next reports it.
    /// </summary>
    private static async Task DeleteFcmTokenAsync()
    {
        try
        {
#if ANDROID
            await Firebase.Messaging.FirebaseMessaging.Instance.DeleteToken().AsAsync();
#else
            await Task.CompletedTask;
#endif
            Preferences.Default.Remove(PushTokenRules.LastTokenPreference);
        }
        catch (Exception ex)
        {
            FirestoreErrors.Log("Sign-out FCM token delete", ex);
        }
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

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LARGA.SharedCore.Push;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using Plugin.Firebase.CloudMessaging;
using Plugin.Firebase.Firestore;

namespace LARGA.SharedCore.Services;

public interface INotificationService
{
    /// <summary>Registers this phone for <paramref name="uid"/>'s pushes (only theirs - see
    /// <see cref="NotificationService"/>). Called when a dashboard opens.</summary>
    Task RegisterPushNotificationsAsync(string uid);
}

/// <summary>
/// The phone's push registration. Fleet phones are shared, so a token is only ever registered for
/// the account signed in now:
///  - this device's token is saved as users/{uid}/fcmTokens/{sha256(token)} = { token, platform,
///    deviceId, lastSeen (server time) } - a doc only that user can read or write (firestore.rules);
///  - the uid is remembered on the phone (PushTokenRules.SignedInUidPreference) so the notification
///    presenter can drop any push whose recipientUid isn't this account, even when the app was killed;
///  - a copy of the same token under the previous account is removed by ManagerWeb
///    (PushTokenOwnershipService) - the rules don't let a phone touch another user's tokens - and
///    the send side already ignores it;
///  - sign-out (MobileApp SignOutService) deletes this doc and the FCM token itself.
/// </summary>
public class NotificationService : INotificationService
{
    private static bool _refreshHooked;

    public async Task RegisterPushNotificationsAsync(string uid)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(uid)) return;

            // Remembered before anything else: from now on only this account's pushes are shown here.
            Preferences.Default.Set(PushTokenRules.SignedInUidPreference, uid);

            // Android 13+: POST_NOTIFICATIONS is a runtime permission.
            var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.PostNotifications>();
            }
            if (status != PermissionStatus.Granted) return;

            await CrossFirebaseCloudMessaging.Current.CheckIfValidAsync();
            HookTokenRefresh();
            string token = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();
            await SaveTokenAsync(uid, token);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FCM Registration Error: {ex.Message}");
        }
    }

    /// <summary>The token doc of <paramref name="uid"/> for <paramref name="token"/>.</summary>
    public static IDocumentReference TokenDocument(string uid, string token) =>
        CrossFirebaseFirestore.Current.GetCollection("users").GetDocument(uid)
            .GetCollection(PushTokenRules.Collection).GetDocument(PushTokenRules.DocId(token));

    /// <summary>A random ID for this install (kept across sign-ins - it names the phone, not a user).</summary>
    public static string DeviceId()
    {
        string id = Preferences.Default.Get(PushTokenRules.DeviceIdPreference, string.Empty);
        if (string.IsNullOrEmpty(id))
        {
            id = Guid.NewGuid().ToString("N");
            Preferences.Default.Set(PushTokenRules.DeviceIdPreference, id);
        }
        return id;
    }

    private static async Task SaveTokenAsync(string uid, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        token = token.Trim();

        // A refreshed token: the old one is dead, so its doc goes (it's this user's own doc).
        string previous = Preferences.Default.Get(PushTokenRules.LastTokenPreference, string.Empty);
        if (!string.IsNullOrEmpty(previous) && previous != token)
        {
            _ = TokenDocument(uid, previous).DeleteDocumentAsync();
        }

        await TokenDocument(uid, token).SetDataAsync(new Dictionary<object, object>
        {
            { PushTokenRules.TokenField, token },
            { PushTokenRules.PlatformField, PushTokenRules.AndroidPlatform },
            { PushTokenRules.DeviceIdField, DeviceId() },
            { PushTokenRules.LastSeenField, FieldValue.ServerTimestamp() },
        });
        Preferences.Default.Set(PushTokenRules.LastTokenPreference, token);
    }

    // FCM can replace the token at any time: register the new one for the account signed in now.
    private static void HookTokenRefresh()
    {
        if (_refreshHooked) return;
        _refreshHooked = true;
        CrossFirebaseCloudMessaging.Current.TokenChanged += async (_, e) =>
        {
            try
            {
                string uid = Preferences.Default.Get(PushTokenRules.SignedInUidPreference, string.Empty);
                if (!string.IsNullOrEmpty(uid))
                {
                    await SaveTokenAsync(uid, e.Token);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FCM token refresh registration failed: {ex.Message}");
            }
        };
    }
}

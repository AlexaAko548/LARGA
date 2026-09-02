using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Plugin.Firebase.CloudMessaging;
using Plugin.Firebase.Firestore;

namespace LARGA.SharedCore.Services;

public interface INotificationService
{
    Task RegisterPushNotificationsAsync(string driverId);
}

public class NotificationService : INotificationService
{
    public async Task RegisterPushNotificationsAsync(string driverId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(driverId)) return;

            // 1. Request POST_NOTIFICATIONS runtime permission (Android 13+)
            var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.PostNotifications>();
            }

            if (status == PermissionStatus.Granted)
            {
                // 2. Fetch the device FCM Token
                await CrossFirebaseCloudMessaging.Current.CheckIfValidAsync();
                var token = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();

                // 3. Save/Merge token into Firestore under the driver's document
                if (!string.IsNullOrEmpty(token))
                {
                    await CrossFirebaseFirestore.Current
                        .GetDocument($"drivers/{driverId}")
                        .SetDataAsync(new Dictionary<object, object>
                        {
                            { "fcmToken", token },
                            { "tokenUpdatedAt", DateTime.UtcNow }
                        }, SetOptions.MergeFields("fcmToken", "tokenUpdatedAt"));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FCM Registration Error: {ex.Message}");
        }
    }
}
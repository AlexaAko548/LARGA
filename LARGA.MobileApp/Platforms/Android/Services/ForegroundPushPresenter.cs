using Android.App;
using Android.Content;
using AndroidX.Core.App;
using LARGA.MobileApp.Services;
using LARGA.SharedCore.Chats;
using Microsoft.Extensions.DependencyInjection;
using Plugin.Firebase.CloudMessaging;
using Plugin.Firebase.CloudMessaging.Platforms.Android.Extensions;

namespace LARGA.MobileApp.Platforms.Android.Services;

/// <summary>
/// Shows a push that arrives while the app is open. With the app in the background Android draws
/// FCM notifications itself; in the foreground Plugin.Firebase hands them to its
/// ShowLocalNotificationAction instead (set in MainActivity), and its own default posted them on a
/// null channel, which Android 8+ drops - so nothing showed. Here each push goes on the channel it
/// would use in the background (chat, SOS, pre-shift), and a chat message for the conversation
/// already on screen shows no banner at all. Tapping it raises NotificationTapped like a background notification.
/// </summary>
internal static class ForegroundPushPresenter
{
    public static void Show(FCMNotification notification)
    {
        try
        {
            ShowCore(notification);
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Foreground push couldn't be shown: {ex.Message}");
        }
    }

    private static void ShowCore(FCMNotification notification)
    {
        Context context = global::Android.App.Application.Context;
        string? type = notification.Data != null && notification.Data.TryGetValue("type", out string? t) ? t : null;
        string? driverId = notification.Data != null && notification.Data.TryGetValue("driverId", out string? d) ? d : null;

        if (type == ChatPushPolicy.PushType
            && IPlatformApplication.Current?.Services.GetService<UnreadBadgeService>()?.IsViewingConversation(driverId) == true)
        {
            return; // they're reading it right now
        }

        Intent? launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        if (launch == null) return;
        launch.PutExtra(FirebaseCloudMessagingImplementation.IntentKeyFCMNotification, notification.ToBundle());
        launch.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);

        // One notification per conversation (a newer message replaces it), like the background
        // ones (ChatPushService sets the same tag); other pushes each get their own.
        string tag = type == ChatPushPolicy.PushType && !string.IsNullOrEmpty(driverId) ? $"chat_{driverId}" : $"push_{System.Guid.NewGuid():N}";
        int requestCode = tag.GetHashCode();
        PendingIntent? tap = PendingIntent.GetActivity(context, requestCode, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new NotificationCompat.Builder(context, ChannelFor(type))
            .SetSmallIcon(Resource.Mipmap.appicon)!
            .SetContentTitle(notification.Title)!
            .SetContentText(notification.Body)!
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(notification.Body))!
            .SetPriority(NotificationCompat.PriorityHigh)!
            .SetAutoCancel(true)!
            .SetContentIntent(tap)!;

        NotificationManagerCompat.From(context)?.Notify(tag, 0, builder.Build()!);
    }

    // The channel the same push lands on in the background: the one ManagerWeb sends it with, else
    // the manifest's default_notification_channel_id (pre_shift_channel). Plugin.Firebase 4.0.1
    // doesn't pass the sender's channel through, so it's picked by the push type.
    private static string ChannelFor(string? type) => type switch
    {
        ChatPushPolicy.PushType => ChatPushPolicy.AndroidChannelId,
        "sos_alert" => "sos_alert_channel", // ManagerWeb SosPushService.PushType / AndroidChannelId
        _ => LARGA.SharedCore.PreShiftReminderRules.AndroidChannelId,
    };
}

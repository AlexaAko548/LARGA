using Android.App;
using Android.Content;
using AndroidX.Core.App;
using LARGA.MobileApp.Services;
using LARGA.SharedCore.Chats;
using LARGA.SharedCore.Push;
using Microsoft.Maui.Storage;
using Microsoft.Extensions.DependencyInjection;
using Plugin.Firebase.CloudMessaging;
using Plugin.Firebase.CloudMessaging.Platforms.Android.Extensions;

namespace LARGA.MobileApp.Platforms.Android.Services;

/// <summary>
/// Draws the pushes Plugin.Firebase hands to its ShowLocalNotificationAction (set in
/// MainApplication, so it's there even when FCM starts a killed app's process): every push while
/// the app is open, and chat pushes in every state - those are data-only (ChatPushService), so
/// Android never draws them itself. Before anything is shown, the push's recipientUid must be the
/// account this phone registered for (PushTokenRules.SignedInUidPreference, cleared at sign-out);
/// a push for anyone else - say a driver who used this phone before - is dropped. Each push goes on
/// the channel it would use in the background (chat, SOS, pre-shift), a chat preview is hidden on
/// the lock screen, and a chat message for the conversation already on screen shows no banner at
/// all. Tapping it raises NotificationTapped like a background notification.
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
        // Not for the account signed in here (or no one is): never shown.
        string signedIn = Preferences.Default.Get(PushTokenRules.SignedInUidPreference, string.Empty);
        if (!PushTokenRules.IsForSignedInUser(notification.Data, signedIn))
        {
            return;
        }

        Context context = global::Android.App.Application.Context;
        string? type = Value(notification, "type");
        string? driverId = Value(notification, ChatPushPolicy.ConversationIdKey) ?? Value(notification, "driverId");
        bool isChat = type == ChatPushPolicy.PushType;
        // A data-only push carries its title and preview in the data.
        string title = notification.Title ?? Value(notification, ChatPushPolicy.TitleKey) ?? "LARGA";
        string body = notification.Body ?? Value(notification, ChatPushPolicy.BodyKey) ?? string.Empty;

        if (isChat
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
        string tag = isChat && !string.IsNullOrEmpty(driverId) ? $"chat_{driverId}" : $"push_{System.Guid.NewGuid():N}";
        int requestCode = tag.GetHashCode();
        PendingIntent? tap = PendingIntent.GetActivity(context, requestCode, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new NotificationCompat.Builder(context, ChannelFor(type))
            .SetSmallIcon(Resource.Mipmap.appicon)!
            .SetContentTitle(title)!
            .SetContentText(body)!
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(body))!
            .SetPriority(NotificationCompat.PriorityHigh)!
            .SetAutoCancel(true)!
            .SetContentIntent(tap)!;

        if (isChat)
        {
            // Lock screen: "New message" only - who wrote and what shows once the phone is unlocked.
            builder.SetVisibility(NotificationCompat.VisibilityPrivate);
            builder.SetPublicVersion(new NotificationCompat.Builder(context, ChannelFor(type))
                .SetSmallIcon(Resource.Mipmap.appicon)!
                .SetContentTitle("LARGA")!
                .SetContentText("New message")!
                .Build());
        }

        NotificationManagerCompat.From(context)?.Notify(tag, 0, builder.Build()!);
    }

    private static string? Value(FCMNotification notification, string key) =>
        notification.Data != null && notification.Data.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

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

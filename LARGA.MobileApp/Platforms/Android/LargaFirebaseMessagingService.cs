using Android.App;
using Android.Content;
using AndroidX.Core.App;
using Firebase.Messaging;

namespace LARGA.MobileApp.Platforms.Android;

[Service(Exported = false)]
[IntentFilter(new[] { "com.google.firebase.MESSAGING_EVENT" })]
public class LargaFirebaseMessagingService : FirebaseMessagingService
{
    const string SosChannelId = "larga_sos_channel";
    const string GeneralChannelId = "larga_general_channel";

    public override void OnMessageReceived(RemoteMessage message)
    {
        base.OnMessageReceived(message);

        var notification = message.GetNotification();
        var data = message.Data;

        string title = notification?.Title ?? (data.TryGetValue("title", out var titleValue) ? titleValue : "LARGA Notification");
        string body = notification?.Body ?? (data.TryGetValue("body", out var bodyValue) ? bodyValue : string.Empty);

        bool isSos = data.ContainsKey("type") && data["type"] == "sos_alert";

        ShowNotification(title, body, isSos);
    }

    public override void OnNewToken(string token)
    {
        base.OnNewToken(token);
        // TODO: send this token to Firestore, linked to the current user's account,
        // so the manager's device can be targeted for push notifications
    }

    private void ShowNotification(string title, string body, bool isSos)
    {
        var context = global::Android.App.Application.Context;
        string channelId = isSos ? SosChannelId : GeneralChannelId;

        CreateNotificationChannels(context);

        var builder = new NotificationCompat.Builder(context, channelId)
            .SetContentTitle(title)
            .SetContentText(body)
            .SetSmallIcon(_Microsoft.Android.Resource.Designer.ResourceConstant.Drawable.notification_icon_background)
            .SetPriority(isSos ? NotificationCompat.PriorityMax : NotificationCompat.PriorityDefault)
            .SetAutoCancel(true);

        if (isSos)
        {
            builder.SetCategory(NotificationCompat.CategoryAlarm);
            builder.SetVibrate(new long[] { 0, 500, 250, 500 });
        }

        var notificationManager = NotificationManagerCompat.From(context);
        notificationManager.Notify(new Random().Next(1000, 9999), builder.Build());
    }

    private void CreateNotificationChannels(Context context)
    {
        if (global::Android.OS.Build.VERSION.SdkInt < global::Android.OS.BuildVersionCodes.O)
            return;

        var notificationManager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;

        // High-priority SOS channel — bypasses Do Not Disturb, uses max importance
        var sosChannel = new NotificationChannel(
            SosChannelId,
            "Emergency SOS Alerts",
            NotificationImportance.Max)
        {
            Description = "Critical alerts for driver SOS emergencies. Requires immediate attention."
        };
        sosChannel.EnableVibration(true);
        sosChannel.SetVibrationPattern(new long[] { 0, 500, 250, 500 });
        sosChannel.SetBypassDnd(true);
        sosChannel.LockscreenVisibility = NotificationVisibility.Public;
        notificationManager.CreateNotificationChannel(sosChannel);

        // General channel — for fuel discrepancy, shift approval, etc.
        var generalChannel = new NotificationChannel(
            GeneralChannelId,
            "General Notifications",
            NotificationImportance.High)
        {
            Description = "Fuel discrepancies, shift approvals, and other operational alerts."
        };
        notificationManager.CreateNotificationChannel(generalChannel);
    }
}
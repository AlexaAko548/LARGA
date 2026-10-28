using System.Globalization;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using FirebaseAdmin.Messaging;
using Google.Cloud.Firestore;
using LARGA.SharedCore;
using LARGA.SharedCore.Services;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Tells a driver about something the manager did to their unit, two ways:
///  - a message in their Messages chat with the manager (always - the record they can reread);
///  - a phone notification to the FCM token the driver app saves on users/{id}.fcmToken
///    (best effort: skipped when the driver has no token, and a failed push is only logged).
/// Sent with the Admin SDK app ManagerWeb already uses for Auth.
/// </summary>
public class DriverNotifier
{
    // Must match the FirebaseApp name created with the FirebaseAuth registration in Program.cs.
    private const string FirebaseAppName = "LargaManagerWeb";

    private readonly Lazy<FirestoreDb> _db;
    private readonly Lazy<FirebaseAuth> _auth;
    private readonly ManagerChatService _chat;
    private readonly ILogger<DriverNotifier> _logger;

    public DriverNotifier(Lazy<FirestoreDb> db, Lazy<FirebaseAuth> auth, ManagerChatService chat, ILogger<DriverNotifier> logger)
    {
        _db = db;
        _auth = auth;
        _chat = chat;
        _logger = logger;
    }

    /// <summary>
    /// A Garage work order was booked for <paramref name="taxiId"/>: tells the driver assigned to
    /// it which day it goes into the shop, and that the manager will give them a temporary unit.
    /// Dates are calendar dates (Philippine days). Never throws - the booking itself already saved.
    /// </summary>
    public async Task NotifyUnitBookedForShopAsync(string taxiId, DateTime shopDay, DateTime? expectedFinish, string issueTitle)
    {
        try
        {
            QuerySnapshot drivers = await _db.Value.Collection("users").WhereEqualTo("assignedTaxiId", taxiId).GetSnapshotAsync();
            if (drivers.Count == 0)
            {
                return;
            }

            bool today = shopDay.Date <= PhilippineTimeToday();
            string day = shopDay.ToString("ddd, MMM d", CultureInfo.InvariantCulture);
            string until = expectedFinish is DateTime finish && finish.Date > shopDay.Date
                ? $" until {finish.ToString("MMM d", CultureInfo.InvariantCulture)}"
                : string.Empty;
            string issue = string.IsNullOrWhiteSpace(issueTitle) ? "maintenance" : $"\"{issueTitle.Trim()}\"";

            string title = today ? $"{taxiId} is in the Garage today" : $"{taxiId} goes to the Garage on {day}";
            string body = today
                ? $"Heads-up: your unit {taxiId} went into the Garage today{until} for {issue}. You can't drive it while it's in the shop - I'll assign you a temporary unit for that time."
                : $"Heads-up: your unit {taxiId} is booked into the Garage on {day}{until} for {issue}. You can't drive it on {(until.Length > 0 ? "those days" : "that day")} - I'll assign you a temporary unit for that time.";

            foreach (DocumentSnapshot driver in drivers.Documents)
            {
                string name = driver.TryGetValue("fullName", out string? fullName) && !string.IsNullOrWhiteSpace(fullName) ? fullName : "Driver";
                await _chat.SendAsync(driver.Id, name, body);

                if (driver.TryGetValue("fcmToken", out string? token) && !string.IsNullOrWhiteSpace(token))
                {
                    await SendPushAsync(token, title, body, taxiId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't notify the driver of {TaxiId} about its Garage booking", taxiId);
        }
    }

    /// <summary>
    /// Proposal §4.2.4.7 (MaintenanceSchedulerService): reminds the driver assigned to
    /// <paramref name="taxiId"/> a week ahead that the unit is in the Garage on
    /// <paramref name="shopDay"/>, or - <paramref name="dateChanged"/> - that its shop day moved.
    /// Chat message + push, like NotifyUnitBookedForShopAsync. False when it couldn't be sent
    /// (logged; the next hourly run tries again). Never throws.
    /// </summary>
    public async Task<bool> NotifyUnitDayOffAsync(string taxiId, DateTime shopDay, DateTime? expectedFinish, string issueTitle, bool dateChanged)
    {
        try
        {
            QuerySnapshot drivers = await _db.Value.Collection("users").WhereEqualTo("assignedTaxiId", taxiId).GetSnapshotAsync();

            string day = shopDay.ToString("ddd, MMM d", CultureInfo.InvariantCulture);
            string until = expectedFinish is DateTime finish && finish.Date > shopDay.Date
                ? $" until {finish.ToString("MMM d", CultureInfo.InvariantCulture)}"
                : string.Empty;
            string issue = string.IsNullOrWhiteSpace(issueTitle) ? "maintenance" : $"\"{issueTitle.Trim()}\"";
            string days = until.Length > 0 ? "those days" : "that day";

            string title = dateChanged ? $"New Garage date for {taxiId}: {day}" : $"Reminder: {taxiId} is in the Garage on {day}";
            string body = dateChanged
                ? $"Change of schedule: your unit {taxiId} now goes into the Garage on {day}{until} for {issue}. You can't drive it on {days} - I'll assign you a temporary unit for that time."
                : $"Reminder: your unit {taxiId} goes into the Garage in a week, on {day}{until}, for {issue}. You can't drive it on {days} - I'll assign you a temporary unit for that time.";

            foreach (DocumentSnapshot driver in drivers.Documents)
            {
                string name = driver.TryGetValue("fullName", out string? fullName) && !string.IsNullOrWhiteSpace(fullName) ? fullName : "Driver";
                await _chat.SendAsync(driver.Id, name, body);

                if (driver.TryGetValue("fcmToken", out string? token) && !string.IsNullOrWhiteSpace(token))
                {
                    await SendPushAsync(token, title, body, taxiId);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't send the Garage day-off notice for {TaxiId}", taxiId);
            return false;
        }
    }

    /// <summary>
    /// SRS REQ-4.1-9: the 5:30 AM pre-shift reminder (PreShiftReminderService) - a push only, on the
    /// driver app's pre_shift_channel; tapping it opens the pre-shift inspection. False when the driver
    /// has no FCM token or the push failed (logged). Never throws.
    /// </summary>
    public async Task<bool> SendPreShiftReminderAsync(string driverId, string taxiId)
    {
        try
        {
            DocumentSnapshot driver = await _db.Value.Collection("users").Document(driverId).GetSnapshotAsync();
            if (!driver.Exists || !driver.TryGetValue("fcmToken", out string? token) || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            _ = _auth.Value; // creates the shared FirebaseApp on first use
            FirebaseMessaging messaging = FirebaseMessaging.GetMessaging(FirebaseApp.GetInstance(FirebaseAppName));
            await messaging.SendAsync(new Message
            {
#pragma warning disable CS0618 // a registration token - see SendPushAsync
                Token = token,
#pragma warning restore CS0618
                Notification = new Notification { Title = PreShiftReminderRules.Title, Body = PreShiftReminderRules.Body(taxiId) },
                Data = new Dictionary<string, string> { ["type"] = PreShiftReminderRules.PushType, ["taxiId"] = taxiId },
                Android = new AndroidConfig
                {
                    Notification = new AndroidNotification { ChannelId = PreShiftReminderRules.AndroidChannelId },
                },
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pre-shift reminder to driver {DriverId} failed", driverId);
            return false;
        }
    }

    private async Task SendPushAsync(string token, string title, string body, string taxiId)
    {
        try
        {
            _ = _auth.Value; // creates the shared FirebaseApp on first use
            FirebaseMessaging messaging = FirebaseMessaging.GetMessaging(FirebaseApp.GetInstance(FirebaseAppName));
            await messaging.SendAsync(new Message
            {
                // users/{id}.fcmToken is a registration token (NotificationService), not an
                // installation ID, so Token - not its suggested replacement Fid - is right here.
#pragma warning disable CS0618
                Token = token,
#pragma warning restore CS0618
                Notification = new Notification { Title = title, Body = body },
                Data = new Dictionary<string, string> { ["type"] = "unit_maintenance", ["taxiId"] = taxiId },
            });
        }
        catch (Exception ex)
        {
            // An old/uninstalled token or FCM being unavailable - the chat message still reached them.
            _logger.LogWarning(ex, "Push to the driver of {TaxiId} failed", taxiId);
        }
    }

    private static DateTime PhilippineTimeToday() => LARGA.SharedCore.PhilippineTime.Now.Date;
}

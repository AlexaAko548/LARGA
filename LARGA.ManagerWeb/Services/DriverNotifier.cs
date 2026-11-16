using System.Globalization;
using Google.Cloud.Firestore;
using LARGA.SharedCore;
using LARGA.SharedCore.Push;
using LARGA.SharedCore.Services;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Tells a driver about something the manager did to their unit, two ways:
///  - a message in their Messages chat with the manager (always - the record they can reread);
///  - a phone notification to the driver's own registered phones (users/{id}/fcmTokens, via
///    PushTokenStore; best effort: skipped when none is registered, and a failed push is only logged).
/// </summary>
public class DriverNotifier
{
    private readonly Lazy<FirestoreDb> _db;
    private readonly PushTokenStore _tokens;
    private readonly ManagerChatService _chat;
    private readonly ILogger<DriverNotifier> _logger;

    public DriverNotifier(Lazy<FirestoreDb> db, PushTokenStore tokens, ManagerChatService chat, ILogger<DriverNotifier> logger)
    {
        _db = db;
        _tokens = tokens;
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
                await _chat.SendAsync(driver.Id, name, body, ChatSenderInfo.System);

                await SendPushAsync(driver.Id, title, body, taxiId);
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
                await _chat.SendAsync(driver.Id, name, body, ChatSenderInfo.System);

                await SendPushAsync(driver.Id, title, body, taxiId);
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
    /// has no registered phone or the push failed (logged). Never throws.
    /// </summary>
    /// <param name="unitLabel">What the message calls the unit (PreShiftReminderRules.UnitLabel: the plate).</param>
    public async Task<bool> SendPreShiftReminderAsync(string driverId, string taxiId, string unitLabel, DateTime philippineNow)
    {
        try
        {
            var data = new Dictionary<string, string> { ["type"] = PreShiftReminderRules.PushType, ["taxiId"] = taxiId };
            (int sent, _) = await _tokens.SendToUsersAsync(new[] { driverId },
                device => PushTokenStore.NotificationMessage(device, PreShiftReminderRules.Title, PreShiftReminderRules.Body(unitLabel, philippineNow),
                    data, PreShiftReminderRules.AndroidChannelId),
                platforms: p => p == PushTokenRules.AndroidPlatform);
            if (sent == 0)
            {
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pre-shift reminder to driver {DriverId} failed", driverId);
            return false;
        }
    }

    private async Task SendPushAsync(string driverId, string title, string body, string taxiId)
    {
        try
        {
            var data = new Dictionary<string, string> { ["type"] = "unit_maintenance", ["taxiId"] = taxiId };
            await _tokens.SendToUsersAsync(new[] { driverId },
                device => PushTokenStore.NotificationMessage(device, title, body, data),
                platforms: p => p == PushTokenRules.AndroidPlatform);
        }
        catch (Exception ex)
        {
            // FCM being unavailable - the chat message still reached them.
            _logger.LogWarning(ex, "Push to the driver of {TaxiId} failed", taxiId);
        }
    }

    private static DateTime PhilippineTimeToday() => LARGA.SharedCore.PhilippineTime.Now.Date;
}

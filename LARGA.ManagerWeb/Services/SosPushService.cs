using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using FirebaseAdmin.Messaging;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Emergency;
using LARGA.SharedCore.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Pushes every new SOS (emergency_alerts, written by the driver app for the manual button and
/// the LAR-86/87 Hostile/Crash protocols) to the phones of all Managers and Assistant Managers,
/// so an emergency reaches them even with the manager app closed and nobody on SOS Dispatch.
///
/// A live Firestore listener, not a poll: unlike IdleAlertMonitorService's checks, an SOS has
/// to arrive within seconds. Each alert is claimed in a transaction (pushSentAt) before it's
/// sent, so it's pushed exactly once - across restarts, and even if two ManagerWeb instances
/// run. Alerts older than <see cref="MaxAlertAge"/> when first seen are claimed without a push,
/// so deploying this doesn't buzz managers about old unresolved alerts.
///
/// Sent with the Admin SDK app ManagerWeb already uses for Auth (same as DriverNotifier).
/// Recipients' tokens are users/{uid}.fcmToken, saved by the manager app's dashboard
/// (NotificationService). Never throws out of the host - failures are logged and the listener
/// is restarted.
///
/// Before pushing, each alert's caller is validated (SosCallerVerificationService) and its
/// position reverse-geocoded (MapTiler, stored on the alert). Only a Rejected alert - positively
/// from the wrong sender - is held back; it still shows, flagged, on SOS Dispatch and in the
/// manager app's Alert Center. An Unverified one (details missing, e.g. weak signal) is pushed
/// with a note. The address is waited on for at most AddressWaitForPush, so a slow MapTiler
/// never delays an SOS; it still lands on the alert afterwards.
/// </summary>
public class SosPushService : BackgroundService
{
    // Must match the FirebaseApp name created with the FirebaseAuth registration in Program.cs.
    private const string FirebaseAppName = "LargaManagerWeb";

    // Must match the channel the manager app creates in MainActivity.CreateNotificationChannels.
    public const string AndroidChannelId = "sos_alert_channel";

    // FCM data "type" the manager app routes to its Alert Center on tap (App.xaml.cs).
    public const string PushType = "sos_alert";

    private static readonly TimeSpan MaxAlertAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AddressWaitForPush = TimeSpan.FromSeconds(2);

    private static readonly string[] StaffRoles = [ManagerSignInService.ManagerRole, ManagerSignInService.AssistantManagerRole];

    private readonly Lazy<FirestoreDb> _db;
    private readonly Lazy<FirebaseAuth> _auth;
    private readonly MapTilerGeocoder _geocoder;
    private readonly SosCallerVerificationService _callerVerification;
    private readonly ILogger<SosPushService> _logger;

    public SosPushService(Lazy<FirestoreDb> db, Lazy<FirebaseAuth> auth, MapTilerGeocoder geocoder,
        SosCallerVerificationService callerVerification, ILogger<SosPushService> logger)
    {
        _db = db;
        _auth = auth;
        _geocoder = geocoder;
        _callerVerification = callerVerification;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            FirestoreChangeListener? listener = null;
            try
            {
                listener = _db.Value.Collection("emergency_alerts")
                    .WhereEqualTo("isResolved", false)
                    .Listen(OnSnapshotAsync, stoppingToken);

                // Completes only when the listener stops (cancellation) or faults.
                await listener.ListenerTask;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SOS push listener stopped; restarting in {Delay}.", RestartDelay);
            }
            finally
            {
                if (listener is not null)
                {
                    try { await listener.StopAsync(); } catch { /* already stopped */ }
                }
            }

            try
            {
                await Task.Delay(RestartDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task OnSnapshotAsync(QuerySnapshot snapshot, CancellationToken token)
    {
        foreach (DocumentChange change in snapshot.Changes)
        {
            if (change.ChangeType != DocumentChange.Type.Added)
            {
                continue;
            }

            try
            {
                await HandleNewAlertAsync(change.Document, token);
            }
            catch (Exception ex)
            {
                // One alert failing must not stop the others (or the listener).
                _logger.LogWarning(ex, "SOS push for emergency_alerts/{AlertId} failed", change.Document.Id);
            }
        }
    }

    private async Task HandleNewAlertAsync(DocumentSnapshot alert, CancellationToken token)
    {
        if (alert.ContainsField("pushSentAt"))
        {
            return;
        }

        DateTime? raisedAt = alert.TryGetValue("timestamp", out Timestamp ts) ? ts.ToDateTime() : null;
        bool tooOld = raisedAt is DateTime at && DateTime.UtcNow - at > MaxAlertAge;

        if (!await ClaimAsync(alert.Reference, token))
        {
            return; // another listener pass (or instance) already handled it
        }

        // Already claimed, so nothing past this point may throw before the push: an alert that
        // can't be read is still pushed, just without the address and caller check.
        EmergencyAlert? parsed = null;
        try
        {
            parsed = alert.ConvertTo<EmergencyAlert>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "emergency_alerts/{AlertId} couldn't be read; pushing without caller check or address", alert.Id);
        }

        Task<string?> addressLookup = parsed is null ? Task.FromResult<string?>(null) : StoreAddressAsync(alert.Reference, parsed, token);
        SosCallerValidator.Result caller = parsed is null
            ? new SosCallerValidator.Result(SosCallerCheck.Unverified, "The alert couldn't be read to check the caller.")
            : await VerifyCallerAsync(alert.Reference, parsed);

        if (tooOld)
        {
            _logger.LogInformation("emergency_alerts/{AlertId} is older than {MaxAge}; marked without a push.", alert.Id, MaxAlertAge);
            return;
        }

        if (caller.Rejected)
        {
            _logger.LogWarning("SOS emergency_alerts/{AlertId} not pushed - rejected caller: {Reason}", alert.Id, caller.Reason);
            return;
        }

        string? address = await Task.WhenAny(addressLookup, Task.Delay(AddressWaitForPush, token)) == addressLookup
            ? await addressLookup
            : null;

        List<string> tokens = await GetStaffTokensAsync(token);
        if (tokens.Count == 0)
        {
            _logger.LogWarning("SOS emergency_alerts/{AlertId}: no manager has a push token yet - open the manager app once to register one.", alert.Id);
            return;
        }

        string triggerType = alert.TryGetValue("triggerType", out string? t) ? t ?? string.Empty : string.Empty;
        string driverName = alert.TryGetValue("driverName", out string? d) && !string.IsNullOrWhiteSpace(d) ? d : "A driver";
        string taxiUnit = alert.TryGetValue("taxiUnit", out string? u) && !string.IsNullOrWhiteSpace(u) ? u : "unknown unit";
        bool hasLocation = (alert.TryGetValue("latitude", out double lat) && lat != 0)
                           | (alert.TryGetValue("longitude", out double lng) && lng != 0);

        string label = EmergencyTypes.Label(triggerType);
        string title = $"SOS - {label}";
        string body = $"{driverName} ({taxiUnit}) needs help."
                      + (!string.IsNullOrWhiteSpace(address) ? $" Near {address}."
                         : hasLocation ? " Open LARGA for their location." : " Location unavailable - call the driver.")
                      + (caller.Verified ? string.Empty : " (Caller not verified - confirm with the driver.)");

        _ = _auth.Value; // creates the shared FirebaseApp on first use
        FirebaseMessaging messaging = FirebaseMessaging.GetMessaging(FirebaseApp.GetInstance(FirebaseAppName));

        // FCM accepts at most 500 tokens per multicast; far more than this fleet's managers.
        BatchResponse response = await messaging.SendEachForMulticastAsync(new MulticastMessage
        {
            // users/{id}.fcmToken is a registration token (NotificationService), not an
            // installation ID, so Tokens - not its suggested replacement Fids - is right here.
#pragma warning disable CS0618
            Tokens = tokens.Take(500).ToList(),
#pragma warning restore CS0618
            Notification = new Notification { Title = title, Body = body },
            Data = new Dictionary<string, string>
            {
                ["type"] = PushType,
                ["alertId"] = alert.Id,
                ["triggerType"] = EmergencyTypes.Normalize(triggerType),
                ["emergencyLabel"] = label,
                ["address"] = address ?? string.Empty,
                ["callerCheck"] = caller.Status,
            },
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                Notification = new AndroidNotification { ChannelId = AndroidChannelId },
            },
        }, token);

        _logger.LogInformation("SOS emergency_alerts/{AlertId} pushed to {Sent}/{Total} manager device(s).",
            alert.Id, response.SuccessCount, tokens.Count);
        if (response.FailureCount > 0)
        {
            foreach (SendResponse failed in response.Responses.Where(r => !r.IsSuccess))
            {
                _logger.LogWarning(failed.Exception, "SOS push to one manager device failed (stale token?)");
            }
        }
    }

    /// <summary>Reverse-geocodes the alert's position and stores it as emergency_alerts.address
    /// (only ManagerWeb's Admin SDK may update alerts besides a Manager). Returns the address, or
    /// null when there's no location or MapTiler couldn't be reached.</summary>
    private async Task<string?> StoreAddressAsync(DocumentReference alertRef, EmergencyAlert alert, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(alert.Address) || (alert.Latitude == 0 && alert.Longitude == 0))
        {
            return string.IsNullOrWhiteSpace(alert.Address) ? null : alert.Address;
        }

        try
        {
            string? address = await _geocoder.ReverseGeocodeAsync(alert.Latitude, alert.Longitude, token);
            await alertRef.UpdateAsync(new Dictionary<string, object>
            {
                ["address"] = address ?? MapTilerGeocoder.FormatCoordinates(alert.Latitude, alert.Longitude),
                ["addressSource"] = address is null ? MapTilerGeocoder.SourceCoordinates : MapTilerGeocoder.SourceMapTiler,
            }, cancellationToken: token);
            return address;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Reverse geocoding emergency_alerts/{AlertId} failed", alertRef.Id);
            return null;
        }
    }

    /// <summary>Runs the SOS caller validation and stores the outcome on the alert. A lookup
    /// failure (Firestore unreachable) leaves it Unverified - pushed - so a genuine SOS is never
    /// held back by an outage; only a positive mismatch (Rejected) suppresses the push.</summary>
    private async Task<SosCallerValidator.Result> VerifyCallerAsync(DocumentReference alertRef, EmergencyAlert alert)
    {
        try
        {
            return await _callerVerification.VerifyAndStoreAsync(alertRef, alert);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Caller validation for emergency_alerts/{AlertId} failed; pushing anyway", alertRef.Id);
            return new SosCallerValidator.Result(SosCallerCheck.Unverified, "Caller not checked (lookup failed).");
        }
    }

    /// <summary>Marks the alert as pushed - true only for the caller that set it first.</summary>
    private Task<bool> ClaimAsync(DocumentReference alertRef, CancellationToken token) =>
        _db.Value.RunTransactionAsync(async transaction =>
        {
            DocumentSnapshot current = await transaction.GetSnapshotAsync(alertRef);
            if (!current.Exists || current.ContainsField("pushSentAt"))
            {
                return false;
            }

            transaction.Update(alertRef, "pushSentAt", FieldValue.ServerTimestamp);
            return true;
        }, cancellationToken: token);

    /// <summary>Push tokens of every Manager / Assistant Manager. The role is matched
    /// case-insensitively, the same way the apps' logins treat it.</summary>
    private async Task<List<string>> GetStaffTokensAsync(CancellationToken token)
    {
        QuerySnapshot users = await _db.Value.Collection("users").GetSnapshotAsync(token);
        return users.Documents
            .Where(doc => doc.TryGetValue("role", out object? role)
                          && StaffRoles.Any(r => string.Equals(r, role?.ToString()?.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Select(doc => doc.TryGetValue("fcmToken", out string? fcm) ? fcm : null)
            .Where(fcm => !string.IsNullOrWhiteSpace(fcm))
            .Select(fcm => fcm!)
            .Distinct()
            .ToList();
    }
}

using FirebaseAdmin;
using FirebaseAdmin.Auth;
using FirebaseAdmin.Messaging;
using Google.Cloud.Firestore;
using LARGA.SharedCore.Push;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// The one place ManagerWeb reads, writes and sends to push tokens - users/{uid}/fcmTokens
/// (<see cref="PushTokenRules"/>). Every push service (chat, SOS, defect reports, Garage notices,
/// pre-shift reminders) sends through <see cref="SendToUsersAsync"/>, which:
///  - sends only to the devices each recipient owns (a token registered later by another account
///    is that account's now), one message per device, each naming its recipientUid;
///  - deletes a token everywhere once FCM says it's no longer registered (app uninstalled, token
///    deleted at sign-out, or replaced), so it's never tried again.
/// The browser's tokens are written here too (<see cref="RegisterAsync"/>): ManagerWeb has no
/// Firestore client SDK, and the uid always comes from the signed-in cookie, never from the browser.
/// </summary>
public class PushTokenStore
{
    // Must match the FirebaseApp name created with the FirebaseAuth registration in Program.cs.
    private const string FirebaseAppName = "LargaManagerWeb";

    // FCM takes at most 500 messages per SendEach call.
    private const int MaxBatch = 500;

    private readonly Lazy<FirestoreDb> _db;
    private readonly Lazy<FirebaseAuth> _auth;
    private readonly ILogger<PushTokenStore> _logger;

    public PushTokenStore(Lazy<FirestoreDb> db, Lazy<FirebaseAuth> auth, ILogger<PushTokenStore> logger)
    {
        _db = db;
        _auth = auth;
        _logger = logger;
    }

    /// <summary>Every users/{uid}/fcmTokens doc. Read fresh for each push (the fleet is small): a
    /// token removed at sign-out must stop receiving straight away.</summary>
    public async Task<List<PushTokenRules.DeviceToken>> GetAllAsync(CancellationToken token = default)
    {
        QuerySnapshot snapshot = await _db.Value.CollectionGroup(PushTokenRules.Collection).GetSnapshotAsync(token);
        return Parse(snapshot.Documents);
    }

    /// <summary>The token docs among <paramref name="docs"/> that sit at users/{uid}/fcmTokens/{id}
    /// (an "fcmTokens" collection anywhere else isn't one).</summary>
    public static List<PushTokenRules.DeviceToken> Parse(IEnumerable<DocumentSnapshot> docs)
    {
        var tokens = new List<PushTokenRules.DeviceToken>();
        foreach (DocumentSnapshot doc in docs)
        {
            DocumentReference? user = doc.Reference.Parent.Parent;
            if (user is null || user.Parent.Id != "users" || user.Parent.Parent is not null
                || !doc.TryGetValue(PushTokenRules.TokenField, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            string platform = doc.TryGetValue(PushTokenRules.PlatformField, out string? p) ? p ?? string.Empty : string.Empty;
            DateTime? lastSeen = doc.TryGetValue(PushTokenRules.LastSeenField, out Timestamp seen) ? seen.ToDateTime() : null;
            tokens.Add(new PushTokenRules.DeviceToken(user.Id, value.Trim(), platform, lastSeen));
        }
        return tokens;
    }

    /// <summary>
    /// Saves this browser's token for <paramref name="uid"/> (the signed-in staff member) and takes
    /// the same token off every other account straight away - a shared office PC that changed hands
    /// must stop getting the previous person's messages.
    /// </summary>
    public async Task RegisterAsync(string uid, string fcmToken, string platform, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(fcmToken)) return;
        string clean = fcmToken.Trim();
        string docId = PushTokenRules.DocId(clean);

        await TokenRef(uid, docId).SetAsync(new Dictionary<string, object>
        {
            [PushTokenRules.TokenField] = clean,
            [PushTokenRules.PlatformField] = platform,
            [PushTokenRules.DeviceIdField] = deviceId.Length > 64 ? deviceId[..64] : deviceId,
            [PushTokenRules.LastSeenField] = FieldValue.ServerTimestamp,
        });

        foreach (PushTokenRules.DeviceToken other in (await GetAllAsync()).Where(t => t.Token == clean && t.Uid != uid))
        {
            await TokenRef(other.Uid, docId).DeleteAsync();
            _logger.LogInformation("Push token moved to {Uid}; removed from {OldUid}.", uid, other.Uid);
        }
    }

    /// <summary>Removes <paramref name="fcmToken"/> from <paramref name="uid"/> (sign-out on the web).</summary>
    public Task RemoveAsync(string uid, string fcmToken) =>
        string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(fcmToken)
            ? Task.CompletedTask
            : TokenRef(uid, PushTokenRules.DocId(fcmToken)).DeleteAsync();

    /// <summary>Deletes the token docs the ownership rules say are someone else's now.</summary>
    public async Task DeleteAsync(IEnumerable<PushTokenRules.DeviceToken> tokens)
    {
        foreach (PushTokenRules.DeviceToken t in tokens)
        {
            await TokenRef(t.Uid, PushTokenRules.DocId(t.Token)).DeleteAsync();
        }
    }

    /// <summary>
    /// Sends one message per device <paramref name="recipientUids"/> own, built by
    /// <paramref name="build"/> (which must put the device's uid in the data as recipientUid - use
    /// <see cref="WithRecipient"/>). <paramref name="excludeUid"/>: the sender, whose tokens are
    /// never used. <paramref name="platforms"/>: which platforms get it (all when null). Returns how
    /// many devices it reached. Never throws for a failed device; a token FCM no longer knows is deleted.
    /// </summary>
    public async Task<(int Sent, int Total)> SendToUsersAsync(
        IEnumerable<string> recipientUids,
        Func<PushTokenRules.DeviceToken, Message> build,
        CancellationToken token = default,
        string? excludeUid = null,
        Func<string, bool>? platforms = null)
    {
        IReadOnlyList<PushTokenRules.DeviceToken> devices =
            PushTokenRules.Deliverable(await GetAllAsync(token), recipientUids, excludeUid, platforms);
        return await SendToDevicesAsync(devices, build, token);
    }

    /// <summary>Like <see cref="SendToUsersAsync"/> for devices already picked (chat: <c>ChatPushPolicy.RecipientDevices</c>).</summary>
    public async Task<(int Sent, int Total)> SendToDevicesAsync(
        IReadOnlyList<PushTokenRules.DeviceToken> devices,
        Func<PushTokenRules.DeviceToken, Message> build,
        CancellationToken token = default)
    {
        if (devices.Count == 0) return (0, 0);

        _ = _auth.Value; // creates the shared FirebaseApp on first use
        FirebaseMessaging messaging = FirebaseMessaging.GetMessaging(FirebaseApp.GetInstance(FirebaseAppName));

        int sent = 0;
        foreach (PushTokenRules.DeviceToken[] batch in devices.Chunk(MaxBatch))
        {
            BatchResponse response = await messaging.SendEachAsync(batch.Select(build).ToList(), token);
            sent += response.SuccessCount;
            for (int i = 0; i < response.Responses.Count; i++)
            {
                SendResponse result = response.Responses[i];
                if (result.IsSuccess) continue;

                if (IsGone(result.Exception))
                {
                    await DeleteEverywhereAsync(batch[i].Token);
                    _logger.LogInformation("Removed a push token of {Uid} that FCM no longer accepts.", batch[i].Uid);
                }
                else
                {
                    _logger.LogWarning(result.Exception, "Push to one device of {Uid} failed.", batch[i].Uid);
                }
            }
        }
        return (sent, devices.Count);
    }

    /// <summary><paramref name="data"/> plus the device's recipientUid.</summary>
    public static Dictionary<string, string> WithRecipient(IDictionary<string, string> data, PushTokenRules.DeviceToken device) =>
        new(data) { [PushTokenRules.RecipientUidKey] = device.Uid };

    /// <summary>
    /// A notification push (title/body drawn by Android itself when the app is in the background) on
    /// <paramref name="androidChannelId"/> - the kind SOS, defect, Garage and pre-shift pushes use.
    /// The data carries recipientUid, which the app checks when it's open or tapped.
    /// </summary>
    public static Message NotificationMessage(
        PushTokenRules.DeviceToken device, string title, string body, IDictionary<string, string> data,
        string? androidChannelId = null, bool highPriority = false) => new()
    {
        // A registration token (users/{uid}/fcmTokens), not an installation ID, so Token - not its
        // suggested replacement Fid - is right here.
#pragma warning disable CS0618
        Token = device.Token,
#pragma warning restore CS0618
        Notification = new Notification { Title = title, Body = body },
        Data = WithRecipient(data, device),
        Android = new AndroidConfig
        {
            Priority = highPriority ? Priority.High : Priority.Normal,
            Notification = androidChannelId is null ? null : new AndroidNotification { ChannelId = androidChannelId },
        },
    };

    /// <summary>
    /// A data-only push: nothing is drawn until the app (ForegroundPushPresenter, in every app state)
    /// or the browser's service worker (firebase-messaging-sw.js) has checked recipientUid against
    /// the account signed in there. High priority so a phone in Doze still wakes for it.
    /// </summary>
    public static Message DataMessage(PushTokenRules.DeviceToken device, IDictionary<string, string> data) => new()
    {
#pragma warning disable CS0618
        Token = device.Token,
#pragma warning restore CS0618
        Data = WithRecipient(data, device),
        Android = new AndroidConfig { Priority = Priority.High, TimeToLive = TimeSpan.FromDays(1) },
        Webpush = new WebpushConfig { Headers = new Dictionary<string, string> { ["Urgency"] = "high", ["TTL"] = "86400" } },
    };

    private async Task DeleteEverywhereAsync(string fcmToken)
    {
        string docId = PushTokenRules.DocId(fcmToken);
        foreach (PushTokenRules.DeviceToken t in (await GetAllAsync()).Where(t => t.Token == fcmToken.Trim()))
        {
            await TokenRef(t.Uid, docId).DeleteAsync();
        }
    }

    // UNREGISTERED: the app was uninstalled or the token deleted/replaced. SENDER_ID_MISMATCH: the
    // token belongs to another Firebase project. Anything else (quota, outage) is worth retrying later.
    private static bool IsGone(FirebaseMessagingException? ex) =>
        ex?.MessagingErrorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.SenderIdMismatch;

    private DocumentReference TokenRef(string uid, string docId) =>
        _db.Value.Collection("users").Document(uid).Collection(PushTokenRules.Collection).Document(docId);
}

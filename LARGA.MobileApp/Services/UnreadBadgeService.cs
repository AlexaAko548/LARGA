using System;
using System.Collections.Generic;
using System.Linq;
using LARGA.SharedCore;
using LARGA.SharedCore.Alerts;
using LARGA.SharedCore.Chats;
using LARGA.SharedCore.Ledger;
using LARGA.SharedCore.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Storage;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.Services;

/// <summary>
/// The unread number badges, live from sign-in until sign-out - not only once the Chats or Alerts
/// page has been opened:
///  - staff: the Chats tab (driver conversations with something unread for this person,
///    ChatReadState) and the Alerts tab (the Alert Center's Unread count, AlertCenterUnread);
///  - driver: the Message Manager button (1 while their own conversation has an unread message).
/// Listeners start when a login sets StaffSession.Role and stop on logout or an account switch.
///
/// It also knows which conversation is on screen (ChatsDetailPage / MessageManagerPage): while the
/// app is in the foreground that conversation is kept marked read for this person as new messages
/// arrive, and a push for it isn't shown as a banner.
/// </summary>
public sealed class UnreadBadgeService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private readonly IChatService _chatService;
    private readonly List<IDisposable> _listeners = new();
    private readonly object _gate = new();

    // Bumped on every start/stop, so a late snapshot or retry from an old session is ignored.
    private int _session;
    private string? _uid;
    private bool _isStaff;

    private IReadOnlyList<ChatReadState.Conversation> _conversations = Array.Empty<ChatReadState.Conversation>();
    private ChatReadState.Conversation? _ownConversation;
    private IReadOnlyList<string> _sosIds = Array.Empty<string>();
    private IReadOnlyList<string> _fuelIds = Array.Empty<string>();
    private IReadOnlyList<string> _clockInIds = Array.Empty<string>();
    private IReadOnlyList<AlertCenterUnread.BellRow> _bellRows = Array.Empty<AlertCenterUnread.BellRow>();

    // Driver: the Ledger tab's red number (LedgerUpdates). A shift this phone ends is counted the moment it ends; a
    // payment or a manager's adjustment comes from the driver's own boundary_payments / debt_adjustments. "Seen up
    // to" and the clock-out count are remembered per driver on this phone, so they survive closing the app.
    private IReadOnlyList<LedgerUpdates.Update> _paymentUpdates = Array.Empty<LedgerUpdates.Update>();
    private IReadOnlyList<LedgerUpdates.Update> _adjustmentUpdates = Array.Empty<LedgerUpdates.Update>();
    private bool _paymentsConfirmed;      // each has been read from the server at least once - a cache-only
    private bool _adjustmentsConfirmed;   // snapshot (a fresh install) mustn't make the whole history look new
    private DateTime? _ledgerSeenUtc;
    private int _shiftsEndedUnseen;

    private string? _activeConversation;
    private bool _isForeground = true;
    private (string DriverId, DateTime? Marker, bool Clear)? _lastMarkRead;

    public UnreadBadgeService(IChatService chatService)
    {
        _chatService = chatService;
        StaffSession.RoleChanged += (_, _) => Restart();
        SessionEvents.SigningOut += (_, _) => Stop();
    }

    /// <summary>Staff: driver conversations with something unread for the signed-in person.</summary>
    public int ChatsCount { get; private set; }

    /// <summary>Staff: the Alert Center's Unread count.</summary>
    public int AlertsCount { get; private set; }

    /// <summary>Driver: 1 while their conversation has an unread staff or system message.</summary>
    public int DriverMessagesCount { get; private set; }

    /// <summary>Driver: balance updates since they last opened the Ledger - a shift they ended, a payment a manager
    /// recorded, or a manager's adjustment. Cleared by opening the Ledger (<see cref="MarkLedgerSeen"/>).</summary>
    public int LedgerCount { get; private set; }

    /// <summary>Raised on the main thread whenever a count may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Staff: whether this driver's conversation has something unread for the person signed in now -
    /// the same rule and data as <see cref="ChatsCount"/>, so the Chats list's highlighted rows and
    /// the tab badge always agree. False until the first snapshot, and for a driver account.
    /// </summary>
    public bool IsConversationUnread(string driverId)
    {
        lock (_gate)
        {
            return _isStaff
                && _conversations.FirstOrDefault(c => c.DriverId == driverId) is { } conversation
                && conversation.IsUnreadFor(_uid, viewerIsStaff: true);
        }
    }

    /// <summary>
    /// The driver just ended a shift on this phone, which adds that day's boundary to their balance: the Ledger tab
    /// shows a red number straight away, without waiting for anything to be read back from the server.
    /// </summary>
    public void NoteShiftEnded()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            lock (_gate)
            {
                if (_uid is null || _isStaff) return;
                _shiftsEndedUnseen++;
                Preferences.Set(EndedKey(_uid), _shiftsEndedUnseen);
            }
            Publish();
        });
    }

    /// <summary>
    /// The driver opened the Ledger: everything up to now counts as seen and the red number goes. Returns how many
    /// updates it was showing, so the Ledger can say what was new. Main thread.
    /// </summary>
    public int MarkLedgerSeen()
    {
        int unseen;
        lock (_gate)
        {
            if (_uid is null || _isStaff) return 0;
            unseen = _shiftsEndedUnseen + LedgerUpdates.CountUnseen(_paymentUpdates.Concat(_adjustmentUpdates), _ledgerSeenUtc);

            _shiftsEndedUnseen = 0;
            Preferences.Remove(EndedKey(_uid));
            // Only once the server has answered: with nothing read yet there's nothing to mark seen (a later
            // baseline covers the history), and an empty list mustn't be recorded as "seen up to the beginning".
            if (_paymentsConfirmed && _adjustmentsConfirmed)
            {
                DateTime? newest = LedgerUpdates.Latest(_paymentUpdates.Concat(_adjustmentUpdates));
                _ledgerSeenUtc = newest is DateTime latest && latest > (_ledgerSeenUtc ?? DateTime.MinValue)
                    ? latest
                    : _ledgerSeenUtc ?? LedgerUpdates.Baseline(_paymentUpdates.Concat(_adjustmentUpdates));
                Preferences.Set(SeenKey(_uid), _ledgerSeenUtc.Value.Ticks);
            }
        }
        Publish();
        return unseen;
    }

    private static string SeenKey(string uid) => $"ledger_seen_ticks_{uid}";
    private static string EndedKey(string uid) => $"ledger_shifts_ended_{uid}";

    /// <summary>True when this conversation is open on screen with the app in the foreground.</summary>
    public bool IsViewingConversation(string? driverId) =>
        _isForeground && !string.IsNullOrEmpty(driverId) && _activeConversation == driverId;

    /// <summary>A chat screen appeared (driverId) or went away (null, with the id it showed).</summary>
    public void SetActiveConversation(string? driverId, string? leaving = null)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (driverId is null && leaving is not null && _activeConversation != leaving)
            {
                return; // the next chat screen already took over
            }
            _activeConversation = driverId;
            KeepActiveConversationRead();
        });
    }

    /// <summary>The app went to the background (false) or came back (true). Nothing is marked
    /// read while nobody can see the screen.</summary>
    public void SetForeground(bool isForeground)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _isForeground = isForeground;
            KeepActiveConversationRead();
        });
    }

    private void Restart()
    {
        Stop();

        string? uid = CrossFirebaseAuth.Current.CurrentUser?.Uid;
        string? role = StaffSession.Role;
        if (string.IsNullOrEmpty(uid) || role is null)
        {
            return;
        }

        int session;
        lock (_gate)
        {
            session = _session;
            _uid = uid;
            _isStaff = StaffRoles.IsStaff(role);
        }

        if (StaffRoles.IsStaff(role))
        {
            StartStaff(session);
        }
        else if (StaffRoles.IsDriver(role))
        {
            StartDriver(session, uid);
        }
    }

    private void Stop()
    {
        lock (_gate)
        {
            _session++;
            foreach (IDisposable listener in _listeners)
            {
                try { listener.Dispose(); } catch { /* already stopped */ }
            }
            _listeners.Clear();
            _uid = null;
            _isStaff = false;
            _conversations = Array.Empty<ChatReadState.Conversation>();
            _ownConversation = null;
            _sosIds = _fuelIds = _clockInIds = Array.Empty<string>();
            _bellRows = Array.Empty<AlertCenterUnread.BellRow>();
            _lastMarkRead = null;
            _paymentUpdates = _adjustmentUpdates = Array.Empty<LedgerUpdates.Update>();
            _paymentsConfirmed = _adjustmentsConfirmed = false;
            _ledgerSeenUtc = null;
            _shiftsEndedUnseen = 0;
        }
        Publish();
    }

    private void StartStaff(int session)
    {
        Listen(session, "Chats badge", () => _chatService.ListenForReadStates(
            conversations => Update(session, () => _conversations = conversations),
            ex => OnListenerFailed(session, "Chats badge", ex)));

        Listen(session, "Alerts badge (SOS)", () => CrossFirebaseFirestore.Current
            .GetCollection("emergency_alerts")
            .WhereEqualsTo("isResolved", false)
            .AddSnapshotListener<IdOnlyProxy>(
                snapshot => Update(session, () => _sosIds = Ids(snapshot)),
                ex => OnListenerFailed(session, "Alerts badge (SOS)", ex)));

        Listen(session, "Alerts badge (fuel)", () => CrossFirebaseFirestore.Current
            .GetCollection("fuel_logs")
            .WhereEqualsTo("verificationStatus", "Flagged")
            .AddSnapshotListener<IdOnlyProxy>(
                snapshot => Update(session, () => _fuelIds = Ids(snapshot)),
                ex => OnListenerFailed(session, "Alerts badge (fuel)", ex)));

        Listen(session, "Alerts badge (clock-in)", () => CrossFirebaseFirestore.Current
            .GetCollection("clockin_requests")
            .WhereEqualsTo("status", "Pending")
            .AddSnapshotListener<IdOnlyProxy>(
                snapshot => Update(session, () => _clockInIds = Ids(snapshot)),
                ex => OnListenerFailed(session, "Alerts badge (clock-in)", ex)));

        // The Alert Center's own system_alerts query (AlertFeed): newest 50, every type.
        Listen(session, "Alerts badge (notices)", () => CrossFirebaseFirestore.Current
            .GetCollection(AlertFeed.Collection)
            .OrderBy(AlertFeed.TimestampField, true)
            .LimitedTo(AlertFeed.Limit)
            .AddSnapshotListener<BellRowProxy>(
                snapshot => Update(session, () => _bellRows = snapshot.Documents
                    .Where(doc => doc.Data != null)
                    .Select(doc => new AlertCenterUnread.BellRow(doc.Reference.Id, doc.Data!.Type, doc.Data.IsRead,
                        FirestoreDateTimeFix.Apply(doc.Data.Timestamp)))
                    .ToList()),
                ex => OnListenerFailed(session, "Alerts badge (notices)", ex)));
    }

    private void StartDriver(int session, string uid)
    {
        // Only the driver's own chats/{uid} - never another driver's conversation.
        Listen(session, "Message Manager badge", () => _chatService.ListenForReadState(uid,
            conversation => Update(session, () => _ownConversation = conversation),
            ex => OnListenerFailed(session, "Message Manager badge", ex)));

        // What was seen of the ledger, remembered from last time on this phone.
        lock (_gate)
        {
            _ledgerSeenUtc = Preferences.ContainsKey(SeenKey(uid))
                ? new DateTime(Preferences.Get(SeenKey(uid), 0L), DateTimeKind.Utc)
                : null;
            _shiftsEndedUnseen = Preferences.Get(EndedKey(uid), 0);
        }

        // The driver's own payments and manager adjustments (firestore.rules: each by driverId). Equality filters
        // only, so no index is needed. The first time they're read from the server the history becomes the
        // baseline, so only what arrives after that shows as new.
        Listen(session, "Ledger badge (payments)", () => CrossFirebaseFirestore.Current
            .GetCollection("boundary_payments")
            .WhereEqualsTo("driverId", uid)
            .AddSnapshotListener<QuickLedgerService.BoundaryPaymentProxy>(
                snapshot => Update(session, () =>
                {
                    _paymentUpdates = QuickLedgerService.PaymentUpdates(snapshot);
                    _paymentsConfirmed |= snapshot.Metadata?.IsFromCache != true;
                    BaselineLedgerIfNeeded(uid);
                }),
                ex => OnListenerFailed(session, "Ledger badge (payments)", ex)));

        Listen(session, "Ledger badge (adjustments)", () => CrossFirebaseFirestore.Current
            .GetCollection("debt_adjustments")
            .WhereEqualsTo("driverId", uid)
            .AddSnapshotListener<QuickLedgerService.DebtAdjustmentProxy>(
                snapshot => Update(session, () =>
                {
                    _adjustmentUpdates = QuickLedgerService.AdjustmentUpdates(snapshot);
                    _adjustmentsConfirmed |= snapshot.Metadata?.IsFromCache != true;
                    BaselineLedgerIfNeeded(uid);
                }),
                ex => OnListenerFailed(session, "Ledger badge (adjustments)", ex)));
    }

    // Inside _gate. The first time both lists are confirmed by the server and nothing is remembered for this driver
    // (a new install, or an update that predates this badge): what's there already is old, not new.
    private void BaselineLedgerIfNeeded(string uid)
    {
        if (_ledgerSeenUtc is not null || !_paymentsConfirmed || !_adjustmentsConfirmed) return;

        _ledgerSeenUtc = LedgerUpdates.Baseline(_paymentUpdates.Concat(_adjustmentUpdates));
        Preferences.Set(SeenKey(uid), _ledgerSeenUtc.Value.Ticks);
    }

    private void Listen(int session, string name, Func<IDisposable> start)
    {
        try
        {
            IDisposable listener = start();
            lock (_gate)
            {
                if (session == _session)
                {
                    _listeners.Add(listener);
                    return;
                }
            }
            listener.Dispose(); // signed out while it was starting
        }
        catch (Exception ex)
        {
            OnListenerFailed(session, name, ex);
        }
    }

    // A failed listener stops for good; start the whole set again a little later (same session only).
    private void OnListenerFailed(int session, string name, Exception ex)
    {
        FirestoreErrors.Log($"Unread badges: {name}", ex);
        // A missing index won't fix itself in 15 seconds either - retrying would only restart every badge in a loop.
        if (FirestoreErrors.Classify(ex) is FirestoreErrorKind.PermissionDenied or FirestoreErrorKind.SignedOut or FirestoreErrorKind.MissingIndex)
        {
            return;
        }

        Application.Current?.Dispatcher.DispatchDelayed(RetryDelay, () =>
        {
            bool current;
            lock (_gate) current = session == _session;
            if (current) Restart();
        });
    }

    private void Update(int session, Action apply)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            lock (_gate)
            {
                if (session != _session) return;
                apply();
            }
            Publish();
            KeepActiveConversationRead();
        });
    }

    private void Publish()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            string? uid;
            bool isStaff;
            lock (_gate)
            {
                uid = _uid;
                isStaff = _isStaff;
                ChatsCount = isStaff ? ChatReadState.UnreadConversationCount(_conversations, uid) : 0;
                AlertsCount = isStaff
                    ? AlertCenterUnread.Count(_sosIds.Concat(_fuelIds).Concat(_clockInIds).ToList(), _bellRows, DateTime.UtcNow)
                    : 0;
                DriverMessagesCount = isStaff ? 0 : ChatReadState.DriverBadgeCount(_ownConversation, uid);
                LedgerCount = isStaff || uid is null ? 0
                    : _shiftsEndedUnseen + LedgerUpdates.CountUnseen(_paymentUpdates.Concat(_adjustmentUpdates), _ledgerSeenUtc);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    // Main thread. The conversation on screen stays read for this person as messages arrive.
    private void KeepActiveConversationRead()
    {
        string? uid;
        bool isStaff;
        ChatReadState.Conversation? conversation;
        lock (_gate)
        {
            uid = _uid;
            isStaff = _isStaff;
            conversation = _activeConversation is null ? null
                : isStaff ? _conversations.FirstOrDefault(c => c.DriverId == _activeConversation)
                : _ownConversation?.DriverId == _activeConversation ? _ownConversation : null;
        }
        if (!_isForeground || uid is null || conversation is null)
        {
            return;
        }

        DateTime? marker = conversation.ReadMarkerToWrite(uid);
        bool clearShared = isStaff && conversation.StaffIsUnread; // the driver never clears "unread by staff"
        if (marker is null && !clearShared)
        {
            return;
        }

        // Once per state: a write that keeps failing mustn't be retried on every snapshot.
        var attempt = (conversation.DriverId, marker, clearShared);
        if (_lastMarkRead == attempt)
        {
            return;
        }
        _lastMarkRead = attempt;
        _ = _chatService.MarkConversationReadAsync(conversation.DriverId, uid, marker, clearShared);
    }

    private static IReadOnlyList<string> Ids<T>(IQuerySnapshot<T> snapshot) =>
        snapshot.Documents.Where(doc => doc.Data != null).Select(doc => doc.Reference.Id).ToList();

    private class IdOnlyProxy
    {
    }

    // The fields of a system_alerts row the Unread count needs - same as AlertCenterViewModel's proxy.
    private class BellRowProxy
    {
        [FirestoreProperty("type")]
        public string Type { get; set; } = string.Empty;

        [FirestoreProperty("timestamp")]
        public DateTime Timestamp { get; set; }

        [FirestoreProperty("isRead")]
        public bool IsRead { get; set; }
    }
}

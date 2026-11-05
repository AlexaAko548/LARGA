using System;
using System.Collections.Generic;
using System.Linq;
using LARGA.SharedCore;
using LARGA.SharedCore.Alerts;
using LARGA.SharedCore.Chats;
using LARGA.SharedCore.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Dispatching;
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
        if (FirestoreErrors.Classify(ex) is FirestoreErrorKind.PermissionDenied or FirestoreErrorKind.SignedOut)
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

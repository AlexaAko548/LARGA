using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
// Change this line to point to your new Models folder
using LARGA.SharedCore.Models.Chats;
using LARGA.SharedCore.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Manager;

public class ChatsViewModel : INotifyPropertyChanged
{
    private readonly IChatService _chatService;
    // Who has what unread (per signed-in person). Not worked out here: Shell keeps this page and view
    // model across a logout and the next login, so a user id remembered here would go stale and the
    // rows would be judged for the previous account. The badge service restarts on every login.
    private readonly LARGA.MobileApp.Services.UnreadBadgeService _badges;
    private string _unreadMessagesText = string.Empty;

    // The chat-session listener can fire (and call Rebuild) before the driver roster has
    // loaded even once, which would otherwise publish a premature "0 unread messages" - true
    // zero and "don't know yet" look identical to a manager unless this is gated.
    private bool _hasLoadedDrivers;

    // Inbox = every registered driver (users) cross-referenced with existing chats/* docs.
    private IReadOnlyList<ChatDriver> _drivers = Array.Empty<ChatDriver>();
    private IReadOnlyList<ChatSession> _sessions = Array.Empty<ChatSession>();

    public ObservableCollection<ChatSession> ChatSessions { get; set; } = new();

    // The conversations (chats/*) come from their own listener; if it's refused (e.g. firestore.rules
    // not yet letting this role read chats) the driver list alone would look like "no conversations".
    private IDisposable? _sessionsListener;
    private bool _sessionsFailed;
    private string _conversationsError = string.Empty;
    public string ConversationsError
    {
        get => _conversationsError;
        private set
        {
            if (_conversationsError == value) return;
            _conversationsError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasConversationsError));
        }
    }
    public bool HasConversationsError => !string.IsNullOrEmpty(ConversationsError);

    // The driver roster is fetched async on appearing (LoadDriversAsync); until that first
    // fetch finishes, CollectionView's EmptyView would otherwise show "No drivers registered
    // yet." for the couple of seconds it takes to load, even when drivers do exist.
    private string _emptyStateText = "Loading drivers…";
    public string EmptyStateText
    {
        get => _emptyStateText;
        private set
        {
            if (_emptyStateText == value) return;
            _emptyStateText = value;
            OnPropertyChanged();
        }
    }

    public string UnreadMessagesText
    {
        get => _unreadMessagesText;
        set
        {
            if (_unreadMessagesText != value)
            {
                _unreadMessagesText = value;
                OnPropertyChanged();
            }
        }
    }

    public ICommand OpenChatCommand { get; }
    public ICommand RefreshCommand { get; }

    private bool _isRefreshing;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (_isRefreshing == value) return;
            _isRefreshing = value;
            OnPropertyChanged();
        }
    }

    public ChatsViewModel(IChatService chatService, LARGA.MobileApp.Services.UnreadBadgeService badges)
    {
        _chatService = chatService;
        _badges = badges;
        OpenChatCommand = new Command<ChatSession>(OnOpenChat);
        RefreshCommand = new Command(async () => await RefreshAsync());
        LoadChatSessions();

        // Raised on the main thread: a message arrived or was read, for this person.
        _badges.Changed += (_, _) => RefreshUnread();

        // The page outlives a logout: stop listening for the old account, start again for the next one.
        LARGA.MobileApp.Services.SessionEvents.SigningOut += (_, _) =>
        {
            _sessionsListener?.Dispose();
            _sessionsListener = null;
        };
        LARGA.MobileApp.Services.StaffSession.RoleChanged += (_, _) =>
        {
            if (LARGA.SharedCore.StaffRoles.IsStaff(LARGA.MobileApp.Services.StaffSession.Role))
            {
                MainThread.BeginInvokeOnMainThread(LoadChatSessions);
            }
        };
    }

    /// <summary>Reloads the driver list. Called by ManagerChatsPage each time it appears.</summary>
    public async Task LoadDriversAsync()
    {
        using var _loading = LARGA.MobileApp.Services.LoadingState.Begin(); // LAR-117: app-wide loading indicator
        _drivers = await _chatService.GetDriversAsync();
        _hasLoadedDrivers = true;
        if (_sessionsFailed)
        {
            LoadChatSessions(); // retry the conversations too
        }
        MainThread.BeginInvokeOnMainThread(() =>
        {
            EmptyStateText = "No drivers registered yet.";
            Rebuild();
        });
    }

    // Chat previews themselves stay live via ListenForChatSessions; a pull only re-reads the driver roster
    // (new/removed drivers), same as LoadDriversAsync on appearing.
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            await LoadDriversAsync();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void LoadChatSessions()
    {
        _sessionsFailed = false;
        _sessionsListener?.Dispose();
        _sessionsListener = _chatService.ListenForChatSessions(sessions =>
        {
            _sessions = sessions.ToList();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ConversationsError = string.Empty;
                Rebuild();
            });
        }, error =>
        {
            _sessionsFailed = true;
            LARGA.MobileApp.Services.FirestoreErrors.Log("Chats: conversations listener", error);
            MainThread.BeginInvokeOnMainThread(() =>
                ConversationsError = $"Couldn't load conversations: {LARGA.MobileApp.Services.FirestoreErrors.FriendlyMessage(error, "load the conversations")} Pull down to retry.");
        });
    }

    private void Rebuild()
    {
        var sessionsByDriver = _sessions
            .GroupBy(s => s.DriverId)
            .ToDictionary(g => g.Key, g => g.First());

        // One row per driver; chats whose ID matches no current driver are hidden.
        var rows = _drivers.Select(driver =>
        {
            var row = new ChatSession { DriverId = driver.Id, DriverName = driver.FullName };
            if (sessionsByDriver.TryGetValue(driver.Id, out var session))
            {
                row.HasConversation = true;
                row.LastMessage = session.LastMessage;
                row.Timestamp = session.Timestamp;
                row.IsUnread = _badges.IsConversationUnread(driver.Id);
            }
            return row;
        })
        .OrderByDescending(r => r.HasConversation)
        .ThenByDescending(r => r.Timestamp)
        .ThenBy(r => r.DriverName)
        .ToList();

        ChatSessions.Clear();
        foreach (var row in rows)
        {
            ChatSessions.Add(row);
        }

        UpdateUnreadText();
    }

    // Re-reads each row's unread state for the signed-in person without rebuilding the list.
    private void RefreshUnread()
    {
        foreach (var row in ChatSessions)
        {
            bool unread = row.HasConversation && _badges.IsConversationUnread(row.DriverId);
            if (row.IsUnread != unread) row.IsUnread = unread;
        }
        UpdateUnreadText();
    }

    private void UpdateUnreadText()
    {
        if (!_hasLoadedDrivers) return;

        int unreadCount = ChatSessions.Count(r => r.IsUnread);
        UnreadMessagesText = unreadCount == 1 ? "1 unread message" : $"{unreadCount} unread messages";
    }

    private async void OnOpenChat(ChatSession session)
    {
        if (session == null) return;

        // No chats/{driverId} doc exists until the first message, so there is nothing to mark read.
        // A failed read-receipt mustn't stop the chat from opening.
        if (session.HasConversation && session.IsUnread)
        {
            session.IsUnread = false;
            try
            {
                await _chatService.MarkMessagesAsReadAsync(session.DriverId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Mark chat read failed: {ex.Message}");
            }
        }

        try
        {
            // Dictionary parameters keep names with spaces or '&' intact (unlike a query string).
            await LARGA.MobileApp.Services.NavigationGate.GoToAsync("ChatsDetailPage", new Dictionary<string, object>
            {
                { "DriverId", session.DriverId },
                { "DriverName", session.DriverName }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Open chat failed: {ex.Message}");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string propertyName = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

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
    private string _unreadMessagesText = "0 unread messages";

    // Inbox = every registered driver (users) cross-referenced with existing chats/* docs.
    private IReadOnlyList<ChatDriver> _drivers = Array.Empty<ChatDriver>();
    private IReadOnlyList<ChatSession> _sessions = Array.Empty<ChatSession>();

    public ObservableCollection<ChatSession> ChatSessions { get; set; } = new();

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

    public ChatsViewModel(IChatService chatService)
    {
        _chatService = chatService;
        OpenChatCommand = new Command<ChatSession>(OnOpenChat);
        LoadChatSessions();
    }

    /// <summary>Reloads the driver list. Called by ManagerChatsPage each time it appears.</summary>
    public async Task LoadDriversAsync()
    {
        _drivers = await _chatService.GetDriversAsync();
        MainThread.BeginInvokeOnMainThread(Rebuild);
    }

    private void LoadChatSessions()
    {
        _chatService.ListenForChatSessions(sessions =>
        {
            _sessions = sessions.ToList();
            MainThread.BeginInvokeOnMainThread(Rebuild);
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
                row.IsUnread = session.IsUnread;
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

        int unreadCount = rows.Count(r => r.IsUnread);
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
            await Shell.Current.GoToAsync("ChatsDetailPage", new Dictionary<string, object>
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

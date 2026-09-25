using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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

    private void LoadChatSessions()
    {
        _chatService.ListenForChatSessions(sessions =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ChatSessions.Clear();
                int unreadCount = 0;

                foreach (var session in sessions.OrderByDescending(s => s.Timestamp))
                {
                    ChatSessions.Add(session);
                    if (session.IsUnread) unreadCount++;
                }

                UnreadMessagesText = unreadCount == 1 ? "1 unread message" : $"{unreadCount} unread messages";
            });
        });
    }

    private async void OnOpenChat(ChatSession session)
    {
        if (session == null) return;

        session.IsUnread = false;
        await _chatService.MarkMessagesAsReadAsync(session.DriverId);

        await Shell.Current.GoToAsync($"ChatsDetailPage?DriverId={session.DriverId}&DriverName={session.DriverName}");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string propertyName = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
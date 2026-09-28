using System;
using System.ComponentModel;
using System.Linq;

namespace LARGA.SharedCore.Models.Chats;

/// <summary>A chat session shaped for display - ManagerWeb and Mobile both consume this 
/// to display the active list of driver conversations.</summary>
public class ChatSession : INotifyPropertyChanged
{
    public string DriverId { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string Initials => string.IsNullOrWhiteSpace(DriverName) ? "?" : string.Join("", DriverName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(n => n[0])).ToUpper();
    public string LastMessage { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string DisplayTime => Timestamp.Date == DateTime.Today ? Timestamp.ToString("h:mm tt").ToLower() : Timestamp.ToString("ddd").ToLower();

    private bool _isUnread;
    public bool IsUnread
    {
        get => _isUnread;
        set { _isUnread = value; OnPropertyChanged(nameof(IsUnread)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
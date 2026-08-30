using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LARGA.MobileApp.Models;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Driver;

public class MessageManagerViewModel : INotifyPropertyChanged
{
    private string _newMessage = string.Empty;
    public ObservableCollection<ChatMessage> Messages { get; set; } = new();

    public string NewMessage
    {
        get => _newMessage;
        set
        {
            if (_newMessage != value)
            {
                _newMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SubmitIcon)); // Triggers the icon swap
            }
        }
    }

    // Swaps the icon dynamically based on whether the input box is empty
    public string SubmitIcon => string.IsNullOrWhiteSpace(NewMessage) ? "like_icon.png" : "send_icon.png";

    public ICommand SendMessageCommand { get; }

    public MessageManagerViewModel()
    {
        Messages.Add(new ChatMessage { Text = "Hey! How is the shift going?", IsDriver = false });
        SendMessageCommand = new Command(OnSendMessage);
    }

    private void OnSendMessage()
    {
        if (string.IsNullOrWhiteSpace(NewMessage))
        {
            // Optional: Handle the "Like" button logic here if NewMessage is empty
            return;
        }

        Messages.Add(new ChatMessage { Text = NewMessage, IsDriver = true });
        NewMessage = string.Empty; // This will automatically revert the icon back to "like_icon.png"
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string propertyName = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
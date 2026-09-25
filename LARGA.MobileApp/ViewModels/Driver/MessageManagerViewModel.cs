using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Driver;

public class MessageManagerViewModel : INotifyPropertyChanged
{
    private readonly IChatService _chatService;
    private string _newMessage = string.Empty;

    // Updated property name
    private string DriverId => Plugin.Firebase.Auth.CrossFirebaseAuth.Current.CurrentUser?.Uid ?? "unknown_driver";

    private IDisposable? _chatListener;

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
                OnPropertyChanged(nameof(SubmitIcon));
            }
        }
    }

    public string SubmitIcon => string.IsNullOrWhiteSpace(NewMessage) ? "like_icon.png" : "send_icon.png";
    public ICommand SendMessageCommand { get; }
    public ICommand CallManagerCommand { get; }

    public MessageManagerViewModel(IChatService chatService)
    {
        _chatService = chatService;
        SendMessageCommand = new Command(OnSendMessage);
        StartListening();

        CallManagerCommand = new Command(OnCallManager);
    }

    private void StartListening()
    {
        // FIX 1: Replaced _driverId with DriverId
        _chatListener = _chatService.ListenForMessages(DriverId, messages =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Messages.Clear();
                foreach (var msg in messages)
                {
                    Messages.Add(msg);
                }
            });
        });
    }

    private async void OnSendMessage()
    {
        if (string.IsNullOrWhiteSpace(NewMessage))
        {
            var likeMessage = new ChatMessage
            {
                Text = "👍",
                IsDriver = true
            };

            // FIX 2: Replaced _driverId with DriverId
            await _chatService.SendMessageAsync(DriverId, likeMessage);
            return;
        }

        var message = new ChatMessage
        {
            Text = NewMessage,
            IsDriver = true
        };

        NewMessage = string.Empty;

        // FIX 3: Replaced _driverId with DriverId
        await _chatService.SendMessageAsync(DriverId, message);
    }

    private async void OnCallManager()
    {
        try
        {
            string managerPhoneNumber = "09123456789";

            var status = await Permissions.CheckStatusAsync<Permissions.Phone>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.Phone>();
            }

            if (status == PermissionStatus.Granted)
            {
#if ANDROID
                var uri = Android.Net.Uri.Parse($"tel:{managerPhoneNumber}");
                var intent = new Android.Content.Intent(Android.Content.Intent.ActionCall, uri);
                intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                Android.App.Application.Context.StartActivity(intent);
#else
            if (Microsoft.Maui.ApplicationModel.Communication.PhoneDialer.Default.IsSupported)
                Microsoft.Maui.ApplicationModel.Communication.PhoneDialer.Default.Open(managerPhoneNumber);
#endif
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Call Error: {ex.Message}");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string propertyName = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
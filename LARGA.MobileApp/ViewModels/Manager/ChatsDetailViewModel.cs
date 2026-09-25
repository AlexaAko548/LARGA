using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Manager;

[QueryProperty(nameof(DriverId), "DriverId")]
[QueryProperty(nameof(DriverName), "DriverName")]
public class ChatsDetailViewModel : INotifyPropertyChanged
{
    private readonly IChatService _chatService;
    private string _newMessage = string.Empty;
    private string _driverId = string.Empty;
    private string _driverName = string.Empty;
    private IDisposable? _chatListener;

    public ObservableCollection<ChatMessage> Messages { get; set; } = new();

    public string DriverId
    {
        get => _driverId;
        set
        {
            if (_driverId != value)
            {
                _driverId = value;
                OnPropertyChanged();

                _chatListener?.Dispose(); // Destroy the old listener first
                StartListening();
            }
        }
    }

    public string DriverName
    {
        get => _driverName;
        set
        {
            _driverName = value;
            OnPropertyChanged();
        }
    }

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
    public ICommand CallDriverCommand { get; }
    public ICommand GoBackCommand { get; }

    public ChatsDetailViewModel(IChatService chatService)
    {
        _chatService = chatService;
        SendMessageCommand = new Command(OnSendMessage);
        CallDriverCommand = new Command(OnCallDriver);
        GoBackCommand = new Command(async () => await Shell.Current.GoToAsync(".."));
    }

    private void StartListening()
    {
        if (string.IsNullOrEmpty(DriverId)) return;

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
                IsDriver = false // Sent by manager
            };
            await _chatService.SendMessageAsync(DriverId, likeMessage);
            return;
        }

        var message = new ChatMessage
        {
            Text = NewMessage,
            IsDriver = false // Sent by manager
        };

        NewMessage = string.Empty;
        await _chatService.SendMessageAsync(DriverId, message);
    }

    private async void OnCallDriver()
    {
        try
        {
            // Fetch driver phone number from service based on DriverId
            string driverPhoneNumber = await _chatService.GetDriverPhoneNumberAsync(DriverId) ?? "00000000000";

            var status = await Permissions.CheckStatusAsync<Permissions.Phone>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.Phone>();
            }

            if (status == PermissionStatus.Granted)
            {
#if ANDROID
                var uri = Android.Net.Uri.Parse($"tel:{driverPhoneNumber}");
                var intent = new Android.Content.Intent(Android.Content.Intent.ActionCall, uri);
                intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                Android.App.Application.Context.StartActivity(intent);
#else
            if (Microsoft.Maui.ApplicationModel.Communication.PhoneDialer.Default.IsSupported)
                Microsoft.Maui.ApplicationModel.Communication.PhoneDialer.Default.Open(driverPhoneNumber);
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
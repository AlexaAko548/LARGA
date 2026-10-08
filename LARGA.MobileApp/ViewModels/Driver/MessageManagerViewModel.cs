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

    // False until the first snapshot arrives, so the empty-state text doesn't flash before messages load.
    private bool _hasLoadedMessages;
    public bool HasLoadedMessages
    {
        get => _hasLoadedMessages;
        private set
        {
            if (_hasLoadedMessages != value)
            {
                _hasLoadedMessages = value;
                OnPropertyChanged();
            }
        }
    }
    public ICommand SendMessageCommand { get; }
    public ICommand CallManagerCommand { get; }
    public Action? ScrollToBottom { get; set; }

    private readonly LARGA.MobileApp.Services.IEmergencyAlertService _emergencyAlertService;

    public MessageManagerViewModel(IChatService chatService, LARGA.MobileApp.Services.IEmergencyAlertService emergencyAlertService)
    {
        _chatService = chatService;
        _emergencyAlertService = emergencyAlertService;
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

                HasLoadedMessages = true;
                ScrollToBottom?.Invoke();
            });
        });
    }

    private async void OnSendMessage()
    {
        // An empty box sends a thumbs-up, like the manager's chat.
        string text = string.IsNullOrWhiteSpace(NewMessage) ? "👍" : NewMessage;
        bool typed = !string.IsNullOrWhiteSpace(NewMessage);
        NewMessage = string.Empty;

        bool sent;
        try
        {
            sent = await _chatService.SendMessageAsync(DriverId, new ChatMessage { Text = text, IsDriver = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Send message failed: {ex.Message}");
            sent = false;
        }

        if (!sent)
        {
            // Put the text back so nothing typed is lost, unless something new was typed meanwhile.
            if (typed && string.IsNullOrEmpty(NewMessage)) NewMessage = text;
            await ShowSendFailedAsync();
        }
    }

    private static async Task ShowSendFailedAsync()
    {
        try
        {
            await Shell.Current.DisplayAlert("Message not sent", "Check your connection and try again.", "OK");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Send-failed alert error: {ex.Message}");
        }
    }

    private async void OnCallManager()
    {
        try
        {
            // Drivers can't read managers' users documents (firestore.rules), so the managers'
            // numbers come from the same system_configs allowlist the SOS auto-answer uses.
            string? managerPhoneNumber = (await _emergencyAlertService.GetManagerPhoneNumbersAsync()).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(managerPhoneNumber))
            {
                await Shell.Current.DisplayAlert("Call Manager",
                    "No manager phone number is on file yet. Send a message instead, or ask your manager to add their number.", "OK");
                return;
            }

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
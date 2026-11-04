using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LARGA.SharedCore;
using Microsoft.Maui.Controls;
using Microsoft.Extensions.DependencyInjection;
using Plugin.Firebase.Auth;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Opens the conversation a tapped chat push (ChatPushPolicy.PushType) is about: a driver goes to
/// Message Manager, staff to the Chats tab and then that driver's ChatsDetailPage - the same route
/// and parameters ChatsViewModel.OnOpenChat uses. Works from a cold start too: it waits for the
/// Shell and for the login (or LandingPage's auto-login) to reach a dashboard first. Not signed
/// in, or signed in as someone the message isn't for: stays put.
/// </summary>
public static class ChatNotificationRouter
{
    private static readonly TimeSpan SignInWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    public static async Task OpenAsync(string driverId, string? driverName)
    {
        try
        {
            if (!await WaitForDashboardAsync())
            {
                return;
            }

            string? uid = CrossFirebaseAuth.Current.CurrentUser?.Uid;
            string? role = StaffSession.Role;
            var badges = IPlatformApplication.Current?.Services.GetService<UnreadBadgeService>();
            if (uid is null || badges?.IsViewingConversation(driverId) == true)
            {
                return; // already on it
            }

            // A tap navigation in progress (NavigationGate) finishes first, so this one isn't dropped.
            for (int i = 0; i < 50 && NavigationGate.IsBusy; i++)
            {
                await Task.Delay(Poll);
            }

            if (StaffRoles.IsDriver(role))
            {
                // A driver only ever has their own conversation.
                if (driverId != uid) return;
                await NavigationGate.RunAsync(async () =>
                {
                    await Shell.Current.GoToAsync("//driver-dashboard/home");
                    await Shell.Current.GoToAsync("message-manager");
                });
            }
            else if (StaffRoles.IsStaff(role))
            {
                await NavigationGate.RunAsync(async () =>
                {
                    await Shell.Current.GoToAsync("//manager-dashboard/chats");
                    await Shell.Current.GoToAsync("ChatsDetailPage", new Dictionary<string, object>
                    {
                        { "DriverId", driverId },
                        { "DriverName", string.IsNullOrWhiteSpace(driverName) ? "Driver" : driverName },
                    });
                });
            }
        }
        catch (Exception ex)
        {
            // The message is still waiting in the chat.
            System.Diagnostics.Debug.WriteLine($"Chat notification routing failed: {ex.Message}");
        }
    }

    // True once a signed-in user is on their dashboard (the Shell exists, the role is known and the
    // landing/login pages are behind them); false if that doesn't happen within SignInWait.
    private static async Task<bool> WaitForDashboardAsync()
    {
        DateTime giveUpAt = DateTime.UtcNow + SignInWait;
        while (DateTime.UtcNow < giveUpAt)
        {
            string location = Shell.Current?.CurrentState?.Location?.OriginalString ?? string.Empty;
            if (StaffSession.Role is string role && CrossFirebaseAuth.Current.CurrentUser != null
                && location.Contains(StaffRoles.IsDriver(role) ? "driver-dashboard" : "manager-dashboard", StringComparison.Ordinal))
            {
                return true;
            }
            await Task.Delay(Poll);
        }
        return false;
    }
}

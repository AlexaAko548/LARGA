using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using LARGA.MobileApp.Services;
using AndroidApp = Android.App.Application;
using AndroidUri = Android.Net.Uri;

namespace LARGA.MobileApp.Platforms.Android.Emergency;

/// <summary>
/// Android implementation of <see cref="IEmergencyMonitor"/>: requests the runtime permissions
/// the protocols need, then runs <see cref="EmergencyDetectionService"/> as a foreground service
/// for the shift.
/// </summary>
public sealed class AndroidEmergencyMonitor : IEmergencyMonitor
{
    private const string FullScreenPromptShownKey = "EmergencyFullScreenPromptShown";

    private string? _runningShiftId;

    public async Task StartAsync(string shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId) || _runningShiftId == shiftId)
        {
            return;
        }

        // MAUI only allows permission requests on the main thread; callers may be on any thread.
        PermissionStatus locationStatus = await MainThread.InvokeOnMainThreadAsync(RequestPermissionsAsync);

        // The service runs as a location-typed foreground service; on Android 14+ starting one
        // without location permission throws and takes the service down. Without location there's
        // also no crash-stop confirmation and no alert coordinates, so skip rather than crash.
        if (locationStatus != PermissionStatus.Granted)
        {
            EmergencyLog.Warn("Monitor not started: location permission denied.");
            return;
        }

        try
        {
            Context context = AndroidApp.Context;
            var intent = new Intent(context, typeof(EmergencyDetectionService)).SetAction(EmergencyDetectionService.ActionStart);

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StartService(intent);
            }

            _runningShiftId = shiftId;
            EmergencyLog.Info($"Monitor start requested for shift {shiftId}.");
        }
        catch (Exception ex)
        {
            // e.g. ForegroundServiceStartNotAllowedException if the app was already in the
            // background; the dashboard's open-shift sync retries the next time it's shown.
            EmergencyLog.Error("Monitor start failed", ex);
        }

        await MainThread.InvokeOnMainThreadAsync(PromptForFullScreenPopupOnceAsync);
    }

    public void Stop()
    {
        _runningShiftId = null;

        try
        {
            // stopService works from the background too (a STOP intent via startService doesn't).
            Context context = AndroidApp.Context;
            context.StopService(new Intent(context, typeof(EmergencyDetectionService)));
            EmergencyLog.Info("Monitor stop requested.");
        }
        catch (Exception ex)
        {
            EmergencyLog.Error("Monitor stop failed", ex);
        }
    }

    private static async Task<PermissionStatus> RequestPermissionsAsync()
    {
        // Location drives the crash-confirmation stop and the alert payload; the phone
        // permissions drive auto-answer. Detection still starts if phone permissions are
        // refused - only the manager callback is lost, and that's logged where it's used.
        try
        {
            PermissionStatus location = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            PermissionStatus phone = await Permissions.RequestAsync<EmergencyPermissions>();
            if (phone != PermissionStatus.Granted)
            {
                EmergencyLog.Warn("Phone/notification permissions not all granted - auto-answer may not work.");
            }
            return location;
        }
        catch (Exception ex)
        {
            EmergencyLog.Error("Permission request failed", ex);
            return PermissionStatus.Unknown;
        }
    }

    /// <summary>
    /// The countdown popup appears over the lock screen through a full-screen notification.
    /// Android 14+ only grants that to calling/alarm apps by default, so the driver has to switch
    /// it on once in Settings. Asked a single time per install.
    /// </summary>
    private static async Task PromptForFullScreenPopupOnceAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(34) || Preferences.Get(FullScreenPromptShownKey, false))
        {
            return;
        }

        try
        {
            Context context = AndroidApp.Context;
            var notifications = (NotificationManager?)context.GetSystemService(Context.NotificationService);
            if (notifications == null || notifications.CanUseFullScreenIntent())
            {
                return;
            }

            Preferences.Set(FullScreenPromptShownKey, true);

            Page? page = Shell.Current;
            if (page == null) return;

            bool open = await page.DisplayAlert(
                "Allow emergency pop-ups",
                "If an emergency is detected while your screen is off, LARGA shows a 5-second cancel " +
                "pop-up over the lock screen. Turn on \"Full screen notifications\" for LARGA on the next screen.",
                "Open settings",
                "Not now");

            if (open)
            {
                var intent = new Intent(global::Android.Provider.Settings.ActionManageAppUseFullScreenIntent)
                    .SetData(AndroidUri.Parse($"package:{context.PackageName}"))
                    .AddFlags(ActivityFlags.NewTask);
                context.StartActivity(intent);
            }
        }
        catch (Exception ex)
        {
            EmergencyLog.Error("Full-screen pop-up prompt failed", ex);
        }
    }
}

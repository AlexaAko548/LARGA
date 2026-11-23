using System.Collections.Generic;
using Android;
using Android.OS;
using Microsoft.Maui.ApplicationModel;

namespace LARGA.MobileApp.Platforms.Android.Emergency;

/// <summary>
/// Runtime permissions the emergency protocols need on top of location: answering the manager's
/// call (ANSWER_PHONE_CALLS), seeing who's calling (READ_CALL_LOG + READ_PHONE_STATE), the SIM's
/// own number for SOS caller validation (READ_PHONE_NUMBERS), and
/// showing the cancel countdown (POST_NOTIFICATIONS). MODIFY_PHONE_STATE isn't here - Android
/// only grants it to system apps.
/// </summary>
public class EmergencyPermissions : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions
    {
        get
        {
            var permissions = new List<(string, bool)>
            {
                (Manifest.Permission.ReadPhoneState, true),
                (Manifest.Permission.ReadCallLog, true),
            };

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                permissions.Add((Manifest.Permission.AnswerPhoneCalls, true));
                // The SIM's own number, sent with each SOS for caller validation (DevicePhoneNumber).
                permissions.Add((Manifest.Permission.ReadPhoneNumbers, true));
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                permissions.Add((Manifest.Permission.PostNotifications, true));
            }

            return permissions.ToArray();
        }
    }
}

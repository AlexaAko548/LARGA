using System;
using Android;
using Android.Content;
using Android.Content.PM;
using Android.Telephony;
using AndroidX.Core.Content;
using LARGA.SharedCore;

namespace LARGA.MobileApp.Platforms.Android.Emergency;

/// <summary>
/// The phone number of this device's SIM, sent with each SOS (emergency_alerts.devicePhone) so
/// ManagerWeb can check it against the assigned driver's registered number. Best effort: many
/// carriers don't write the number onto the SIM, and without READ_PHONE_NUMBERS there's nothing
/// to read - null then, and validation relies on the shift assignment and registered number.
/// </summary>
public static class DevicePhoneNumber
{
    public static string? TryGet()
    {
        try
        {
            Context context = global::Android.App.Application.Context;
            if (ContextCompat.CheckSelfPermission(context, Manifest.Permission.ReadPhoneNumbers) != Permission.Granted
                && ContextCompat.CheckSelfPermission(context, Manifest.Permission.ReadPhoneState) != Permission.Granted)
            {
                return null;
            }

            string? number = null;
            if (OperatingSystem.IsAndroidVersionAtLeast(33)
                && context.GetSystemService(Context.TelephonySubscriptionService) is SubscriptionManager subscriptions)
            {
                int subscriptionId = SubscriptionManager.DefaultSubscriptionId;
                if (subscriptionId != SubscriptionManager.InvalidSubscriptionId)
                {
                    number = subscriptions.GetPhoneNumber(subscriptionId);
                }
            }

            if (string.IsNullOrWhiteSpace(number) && context.GetSystemService(Context.TelephonyService) is TelephonyManager telephony)
            {
#pragma warning disable CA1422 // Line1Number is deprecated on API 33+, still the only option below it
                number = telephony.Line1Number;
#pragma warning restore CA1422
            }

            return InputValidator.NormalizePhilippineMobile(number);
        }
        catch (Exception ex)
        {
            EmergencyLog.Warn($"Device phone number unavailable: {ex.Message}");
            return null;
        }
    }
}

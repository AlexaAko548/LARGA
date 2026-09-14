using System;
using Microsoft.Maui.Graphics;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Client-side mirror of DriverManagementService.ComputeLicenseStatus (ManagerWeb, admin-SDK
/// only) so the mobile Manager UI can show the same license status without a server call.
/// </summary>
public static class LicenseStatusHelper
{
    // WORKAROUND: Plugin.Firebase.Firestore hands back a DateTimeOffset for a *nullable*
    // DateTime? property (unlike a non-nullable DateTime field, which comes back as DateTime -
    // see FirestoreDateTimeFix). Assigning that DateTimeOffset via reflection into a
    // Nullable<DateTime> property throws InvalidCastException and kills the ENTIRE document
    // read, not just this field - so proxies must declare license expiry as DateTimeOffset?,
    // and this takes that instead of DateTime? for that reason.
    public static (string Text, Color Color, Color BgColor) Describe(DateTimeOffset? licenseExpiryUtc)
    {
        if (licenseExpiryUtc == null)
        {
            return ("none", Color.FromArgb("#6B808A"), Color.FromArgb("#EEF2F4"));
        }

        var expiry = FirestoreDateTimeFix.Apply(licenseExpiryUtc.Value.UtcDateTime);
        var now = DateTime.UtcNow;

        if (expiry < now) return ("expired", Color.FromArgb("#D33F3F"), Color.FromArgb("#FBEAEA"));

        // 30 days is a judgment call - long enough for a manager to notice and remind the
        // driver to renew before it actually lapses. Matches DriverManagementService.
        if (expiry <= now.AddDays(30)) return ("expiring soon", Color.FromArgb("#C97A1B"), Color.FromArgb("#FBF0E0"));

        return ("active", Color.FromArgb("#1E8E5A"), Color.FromArgb("#E3F5EC"));
    }
}

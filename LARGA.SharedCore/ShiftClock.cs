using System;
using System.Globalization;

namespace LARGA.SharedCore;

/// <summary>
/// "Now" for the driver app's shift rules (clock-in gate, late fee, return countdown).
/// Normally the real time. For testing outside the 6:00 AM–10:00 PM window, a debug build can
/// pretend it's another Philippine time - set by system_configs/global "testClockPh"
/// (see ShiftManagementService.RefreshTestClockAsync); release builds never set it.
/// The pretend clock keeps ticking from the set time.
/// </summary>
public static class ShiftClock
{
    public static TimeSpan Offset { get; private set; }

    public static bool IsPretending => Offset != TimeSpan.Zero;

    public static DateTime UtcNow => DateTime.UtcNow + Offset;

    /// <summary>The phone's local time, shifted the same way (for existing DateTime.Now-based UI).</summary>
    public static DateTime LocalNow => UtcNow.ToLocalTime();

    /// <summary>
    /// "HH:mm" (today, Philippine date) or "yyyy-MM-dd HH:mm", Philippine time. Blank or
    /// unreadable goes back to the real clock.
    /// </summary>
    public static void SetPretendPhilippineTime(string? value)
    {
        Offset = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value)) return;

        value = value.Trim();
        DateTime phTime;
        // One- or two-digit hours: "8:00" and "08:00" both work.
        if (DateTime.TryParseExact(value, new[] { "yyyy-MM-dd H:mm", "yyyy-MM-dd HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime full))
        {
            phTime = full;
        }
        else if (TimeSpan.TryParseExact(value, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out TimeSpan timeOfDay))
        {
            phTime = PhilippineTime.Now.Date + timeOfDay;
        }
        else
        {
            return;
        }

        DateTime targetUtc = DateTime.SpecifyKind(phTime - PhilippineTime.Offset, DateTimeKind.Utc);
        Offset = targetUtc - DateTime.UtcNow;
    }
}

using System;
using System.Globalization;
using LARGA.SharedCore;
using Microsoft.Maui.Storage;

namespace LARGA.MobileApp.Services;

/// <summary>
/// REQ-4.3-3: the shift the driver most recently finished (clock-out, the 6:00 AM auto-close, or
/// one closed elsewhere), so a fuel report filed after clock-out is still linked to it
/// (FuelShiftRules.PostShiftWindow). Only a hint for the phone: ShiftManagementService
/// .GetFuelReportShiftIdAsync checks it against Firestore, and finds the shift there when the
/// phone has forgotten it (reinstall, sign-out).
/// </summary>
public static class LastShiftStore
{
    public const string LastShiftIdKey = "LastShiftId";
    public const string LastShiftEndedAtKey = "LastShiftEndedAt";

    /// <summary>Remembers <paramref name="shiftId"/> as just ended. Called wherever CurrentShiftId is cleared.</summary>
    public static void RememberEnded(string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId)) return;
        Preferences.Set(LastShiftIdKey, shiftId);
        Preferences.Set(LastShiftEndedAtKey, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
    }

    /// <summary>The remembered shift while it's still within the window by the phone's clock, else null.</summary>
    public static string? RecentShiftId()
    {
        string id = Preferences.Get(LastShiftIdKey, string.Empty);
        if (string.IsNullOrWhiteSpace(id)) return null;

        bool parsed = DateTime.TryParse(Preferences.Get(LastShiftEndedAtKey, string.Empty), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime endedAt);
        return parsed && FuelShiftRules.EndedRecently(endedAt, DateTime.UtcNow) ? id : null;
    }

    /// <summary>The shift a fuel report would go to without asking Firestore: the current one, else the recent one.</summary>
    public static string? LocalFuelReportShiftId()
    {
        string current = Preferences.Get("CurrentShiftId", string.Empty);
        return string.IsNullOrWhiteSpace(current) ? RecentShiftId() : current;
    }

    public static void Clear()
    {
        Preferences.Remove(LastShiftIdKey);
        Preferences.Remove(LastShiftEndedAtKey);
    }
}

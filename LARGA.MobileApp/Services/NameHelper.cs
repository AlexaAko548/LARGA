using System;

namespace LARGA.MobileApp.Services;

public static class NameHelper
{
    /// <summary>"Regina Cruz" -> "RC", "Cher" -> "C", blank -> "?". Matches ManagerWeb's own
    /// avatar-initials convention (DashboardSidebar's "RC", DriverShifts' table-avatar).</summary>
    public static string Initials(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "?";

        var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..1].ToUpperInvariant();

        return (parts[0][..1] + parts[^1][..1]).ToUpperInvariant();
    }
}

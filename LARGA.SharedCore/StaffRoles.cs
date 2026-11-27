using System;

namespace LARGA.SharedCore;

/// <summary>
/// users/{uid}.role as the apps read it - trimmed and case-insensitive, the same way
/// ManagerSignInService (web) and firestore.rules (getUserRole().trim().lower()) do. An Assistant
/// Manager uses the manager side of the mobile app (Map, Chats, Ledger, Alerts) but not the
/// Manager-only settings and edits.
/// </summary>
public static class StaffRoles
{
    public const string Driver = "Driver";
    public const string Manager = "Manager";
    public const string AssistantManager = "Assistant Manager";

    /// <summary>A deleted driver. The profile is kept so past shifts and payments still show their name,
    /// but no app, page or security rule treats this as a role that can do anything.</summary>
    public const string FormerDriver = "Former Driver";

    public const string DriverDashboardRoute = "//driver-dashboard";
    public const string ManagerDashboardRoute = "//manager-dashboard";

    public static bool IsDriver(string? role) => Is(role, Driver);
    public static bool IsFormerDriver(string? role) => Is(role, FormerDriver);

    /// <summary>For looking up names on past records: current and deleted drivers.</summary>
    public static bool IsDriverOrFormer(string? role) => IsDriver(role) || IsFormerDriver(role);

    public static bool IsManager(string? role) => Is(role, Manager);
    public static bool IsAssistantManager(string? role) => Is(role, AssistantManager);
    public static bool IsStaff(string? role) => IsManager(role) || IsAssistantManager(role);

    /// <summary>The dashboard a role signs in to, or null for a role the app doesn't know.</summary>
    public static string? HomeRoute(string? role) =>
        IsDriver(role) ? DriverDashboardRoute : IsStaff(role) ? ManagerDashboardRoute : null;

    /// <summary>DSM-006: whether an account may sign in from the Driver or Manager tile. An
    /// Assistant Manager uses the Manager tile.</summary>
    public static bool MatchesLoginTile(string? role, string? tile) =>
        IsManager(tile) ? IsStaff(role) : IsDriver(tile) && IsDriver(role);

    private static bool Is(string? role, string expected) =>
        string.Equals(role?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}

using System;

namespace LARGA.SharedCore.Emergency;

/// <summary>
/// Who may resolve (or reopen) an SOS: a Manager only - an Assistant Manager can view SOS
/// Dispatch but not close an emergency. firestore.rules say the same for the apps
/// (emergency_alerts update: isManager()), but ManagerWeb writes through the Admin SDK, which
/// skips the rules, so SosDispatchService checks this itself.
/// </summary>
public static class SosResolvePolicy
{
    public const string DeniedMessage = "Only a Manager can resolve or reopen an SOS alert.";

    /// <summary>users/{uid}.role, compared trimmed and case-insensitively like the rules do.</summary>
    public static bool CanResolve(string? role) =>
        string.Equals(role?.Trim(), "Manager", StringComparison.OrdinalIgnoreCase);
}

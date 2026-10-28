using LARGA.SharedCore;

namespace LARGA.MobileApp.Services;

/// <summary>The signed-in user's role, set once it's read at login or auto-login (LoginViewModel,
/// LandingPage). The manager screens use it to leave out what only a Manager may change for an
/// Assistant Manager; firestore.rules enforce the same split.</summary>
public static class StaffSession
{
    public static string? Role { get; set; }

    /// <summary>True for an Assistant Manager - false for a Manager, and while the role isn't known.</summary>
    public static bool IsAssistantManager => StaffRoles.IsAssistantManager(Role);

    public static bool IsManager => StaffRoles.IsManager(Role);
}

using System;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Raised just before a manual logout signs the user out of Firebase, so screens that keep live Firestore listeners
/// (the manager's Alert Center) can remove them. Without this they keep running for the signed-out user, fail with
/// permission errors, and a second set starts on the next login.
/// </summary>
public static class SessionEvents
{
    public static event EventHandler? SigningOut;

    public static void RaiseSigningOut() => SigningOut?.Invoke(null, EventArgs.Empty);
}

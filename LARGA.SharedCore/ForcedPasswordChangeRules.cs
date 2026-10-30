using System;

namespace LARGA.SharedCore;

/// <summary>
/// users/{uid}.mustChangePassword: set by ManagerWeb when a manager creates an account or sets a
/// temporary password (DriverManagementService, which also stamps temporaryPasswordSetAt). After
/// signing in, the mobile app keeps the user on the change-password screen until they choose a new
/// password. Any password change made in the app clears the flag (firestore.rules let a user set only
/// their own flag, and only to false). A change made elsewhere - the forgot-password email - is
/// picked up by ManagerWeb on startup and then hourly (<see cref="ChangedSinceTemporary"/>).
/// </summary>
public static class ForcedPasswordChangeRules
{
    public const string Field = "mustChangePassword";
    public const string SetAtField = "temporaryPasswordSetAt";

    /// <summary>Firebase moves the account's "tokens valid after" time a moment after the manager's own
    /// password write, so only a later change than this counts as the user's.</summary>
    public static readonly TimeSpan ChangeMargin = TimeSpan.FromMinutes(2);

    /// <summary>Whether the password was changed after the temporary one was set. Firebase Auth moves
    /// tokensValidAfter on every password change (in the app, by email reset or by a manager), so a time
    /// clearly after <paramref name="temporarySetUtc"/> means the user has replaced it. Unknown on either
    /// side: false (keep the flag).</summary>
    public static bool ChangedSinceTemporary(DateTime? passwordChangedUtc, DateTime? temporarySetUtc) =>
        passwordChangedUtc is DateTime changed && temporarySetUtc is DateTime set && changed > set + ChangeMargin;

    /// <summary>The first problem with the form, or null when it can be saved. The new password follows
    /// the usual rule (InputValidator.ValidatePassword) and can't be the current one.</summary>
    public static string? Validate(string? currentPassword, string? newPassword, string? confirmPassword)
    {
        if (string.IsNullOrWhiteSpace(currentPassword) || string.IsNullOrWhiteSpace(newPassword) || string.IsNullOrWhiteSpace(confirmPassword))
        {
            return "Please fill in all fields.";
        }

        if (newPassword != confirmPassword)
        {
            return "New password and confirmation must match.";
        }

        if (newPassword == currentPassword)
        {
            return InputValidator.SamePasswordMessage;
        }

        return InputValidator.ValidatePassword(newPassword);
    }
}

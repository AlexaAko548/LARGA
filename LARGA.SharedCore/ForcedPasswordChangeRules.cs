namespace LARGA.SharedCore;

/// <summary>
/// users/{uid}.mustChangePassword: set by ManagerWeb when a manager creates an account or sets a
/// temporary password (DriverManagementService). After signing in, the mobile app keeps the user on
/// the change-password screen until they choose a new password, then clears the flag (firestore.rules
/// let a user set only their own flag, and only to false).
/// </summary>
public static class ForcedPasswordChangeRules
{
    public const string Field = "mustChangePassword";

    /// <summary>The first problem with the form, or null when it can be saved. The new password follows
    /// the usual rule (InputValidator.ValidatePassword) and can't be the temporary one.</summary>
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
            return "Choose a new password - it can't be the temporary one your manager gave you.";
        }

        return InputValidator.ValidatePassword(newPassword);
    }
}

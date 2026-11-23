using System.ComponentModel.DataAnnotations;
using LARGA.ManagerWeb.Validation;

namespace LARGA.ManagerWeb.Models;

// The profile menu's Settings forms (ManagerProfileMenu). Same rules as the mobile app's
// Update Contact Number / Change Password screens, via InputValidator.

public class UpdateContactModel
{
    [Required(ErrorMessage = "Enter the number currently on file.")]
    public string CurrentContactNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the new number.")]
    [PhilippineMobile]
    public string NewContactNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "Confirm the new number.")]
    public string ConfirmContactNumber { get; set; } = string.Empty;
}

public class UpdatePasswordModel
{
    [Required(ErrorMessage = "Enter your current password.")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter a new password.")]
    [Password]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Confirm your new password.")]
    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

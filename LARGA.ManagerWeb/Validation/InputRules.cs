using System.ComponentModel.DataAnnotations;
using LARGA.SharedCore;

namespace LARGA.ManagerWeb.Validation;

// DataAnnotations versions of InputValidator's rules, for the EditForm pages (Login,
// Register) - so those forms accept exactly what the rest of the app accepts.

/// <summary>A Philippine mobile number (09XX XXX XXXX / +63 9XX XXX XXXX).</summary>
public sealed class PhilippineMobileAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context) =>
        InputValidator.ValidatePhilippineMobile(value as string) is string error
            ? new ValidationResult(error, new[] { context.MemberName! })
            : ValidationResult.Success;
}

/// <summary>name@domain.tld.</summary>
public sealed class StrictEmailAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context) =>
        InputValidator.ValidateEmail(value as string) is string error
            ? new ValidationResult(error, new[] { context.MemberName! })
            : ValidationResult.Success;
}

/// <summary>First and last name, letters only.</summary>
public sealed class FullNameAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context) =>
        InputValidator.ValidateFullName(value as string) is string error
            ? new ValidationResult(error, new[] { context.MemberName! })
            : ValidationResult.Success;
}

/// <summary>A password someone chooses for themselves (Request Access): 8+ characters with upper and
/// lower case and a number (InputValidator.ValidateStrongPassword).</summary>
public sealed class StrongPasswordAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context) =>
        InputValidator.ValidateStrongPassword(value as string) is string error
            ? new ValidationResult(error, new[] { context.MemberName! })
            : ValidationResult.Success;
}

/// <summary>The shared password policy (InputValidator.PasswordRules): 6-64 characters, at least one
/// letter and one number, no spaces - same rule as account creation and the mobile app.</summary>
public sealed class PasswordAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context) =>
        InputValidator.ValidatePassword(value as string) is string error
            ? new ValidationResult(error, new[] { context.MemberName! })
            : ValidationResult.Success;
}

/// <summary>A sign-in password: length only (InputValidator.ValidateLoginPassword), so an older password
/// that predates the letter/number rule still signs in.</summary>
public sealed class LoginPasswordAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context) =>
        InputValidator.ValidateLoginPassword(value as string) is string error
            ? new ValidationResult(error, new[] { context.MemberName! })
            : ValidationResult.Success;
}

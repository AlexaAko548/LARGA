using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace LARGA.SharedCore;

/// <summary>
/// Input rules shared by ManagerWeb and the mobile app, so a value accepted on one is accepted
/// on the other. Each Validate* method returns null when the value is fine, otherwise the
/// message to show the user; Normalize* methods return the form to store.
/// </summary>
public static class InputValidator
{
    // ---------------------------------------------------------------------
    // Phone numbers - Philippine mobile numbers only (drivers and managers are reached on
    // their cell phones). Typed any common way - 0917 123 4567, 917-123-4567, +63 917 123 4567,
    // 639171234567 - and stored as +639171234567 (international format: works for tel: links,
    // SMS and messaging apps, and the same number always compares equal).
    // ---------------------------------------------------------------------

    public const string PhoneExample = "0917 123 4567 or +63 917 123 4567";

    /// <summary>The number as +639XXXXXXXXX, or null when it isn't a Philippine mobile number.</summary>
    public static string? NormalizePhilippineMobile(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        string trimmed = input.Trim();
        // Only digits, spaces, dashes, dots, parentheses and a leading + are allowed.
        if (!Regex.IsMatch(trimmed, @"^\+?[\d\s\-().]+$"))
        {
            return null;
        }

        string digits = new string(trimmed.Where(char.IsDigit).ToArray());
        string? local = digits switch
        {
            { Length: 11 } when digits.StartsWith("09") => digits[1..],       // 09XXXXXXXXX
            { Length: 10 } when digits.StartsWith('9') => digits,             // 9XXXXXXXXX
            { Length: 12 } when digits.StartsWith("639") => digits[2..],      // 639XXXXXXXXX
            _ => null,
        };

        return local is null ? null : "+63" + local;
    }

    public static string? ValidatePhilippineMobile(string? input, bool required = true)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return required ? $"Enter a mobile number, e.g. {PhoneExample}." : null;
        }

        return NormalizePhilippineMobile(input) is null
            ? $"Enter a valid Philippine mobile number (11 digits starting with 09), e.g. {PhoneExample}."
            : null;
    }

    /// <summary>+639171234567 → "+63 917 123 4567" for display; anything else is returned as is.</summary>
    public static string FormatPhilippineMobile(string? stored)
    {
        string? normalized = NormalizePhilippineMobile(stored);
        return normalized is null
            ? stored ?? string.Empty
            : $"+63 {normalized[3..6]} {normalized[6..9]} {normalized[9..]}";
    }

    /// <summary>Whether two phone numbers are the same, however each was typed.</summary>
    public static bool SamePhone(string? a, string? b)
    {
        string? na = NormalizePhilippineMobile(a);
        string? nb = NormalizePhilippineMobile(b);
        return na is not null ? na == nb : string.Equals(a?.Trim(), b?.Trim(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Email
    // ---------------------------------------------------------------------

    // name@domain.tld - letters/digits and . _ % + - before the @, a dotted domain after it,
    // and a 2+ letter ending. Stricter than "anything@anything.anything" so typos like
    // "juan@gmail" or "juan@@gmail.com" are caught.
    private static readonly Regex EmailPattern = new(
        @"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?)*\.[A-Za-z]{2,}$",
        RegexOptions.Compiled);

    public static bool IsValidEmail(string? email)
    {
        string value = (email ?? string.Empty).Trim();
        return value.Length <= 254 && !value.Contains("..") && EmailPattern.IsMatch(value);
    }

    public static string? ValidateEmail(string? email, bool required = true)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return required ? "Enter an email address." : null;
        }

        return IsValidEmail(email) ? null : "Enter a valid email address, e.g. juan.delacruz@gmail.com.";
    }

    // ---------------------------------------------------------------------
    // Names
    // ---------------------------------------------------------------------

    /// <summary>A person's full name: first and last name at least (the license scan matches
    /// both against the card), letters with spaces, periods, hyphens and apostrophes.</summary>
    public static string? ValidateFullName(string? name)
    {
        string value = NormalizeSpaces(name);
        if (value.Length == 0)
        {
            return "Enter the full name.";
        }

        if (value.Length > 80)
        {
            return "Name is too long (80 characters max).";
        }

        if (!Regex.IsMatch(value, @"^[\p{L}][\p{L}\s.'\-]*$"))
        {
            return "Name can only contain letters, spaces, periods, hyphens and apostrophes.";
        }

        if (value.Split(' ').Count(w => w.Trim('.', '-', '\'').Length >= 2) < 2)
        {
            return "Enter both the first and last name, e.g. Juan Dela Cruz.";
        }

        return null;
    }

    /// <summary>Trims and collapses repeated spaces.</summary>
    public static string NormalizeSpaces(string? value) =>
        Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");

    // ---------------------------------------------------------------------
    // Passwords (Firebase Auth's minimum is 6)
    // ---------------------------------------------------------------------

    public const int MinPasswordLength = 6;

    public static string? ValidatePassword(string? password, bool required = true)
    {
        if (string.IsNullOrEmpty(password))
        {
            return required ? "Enter a password." : null;
        }

        if (password.Length < MinPasswordLength)
        {
            return $"Password must be at least {MinPasswordLength} characters.";
        }

        if (password.Length > 64)
        {
            return "Password is too long (64 characters max).";
        }

        return password.Any(char.IsWhiteSpace) ? "Password can't contain spaces." : null;
    }

    // ---------------------------------------------------------------------
    // LTO driver's license fields
    // ---------------------------------------------------------------------

    // Letter + 2 digits - 2 digits - 5/6 digits, e.g. A01-23-456789 (same pattern the license
    // scan reads; some cards print a 5-digit tail).
    private static readonly Regex LicenseNumberPattern = new(@"^([A-Z])(\d{2})-(\d{2})-(\d{5,6})$", RegexOptions.Compiled);

    /// <summary>"a0123456789", "A01 23 456789" etc. → "A01-23-456789"; null when it isn't one.</summary>
    public static string? NormalizeLicenseNumber(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        string compact = Regex.Replace(input.Trim().ToUpperInvariant(), @"[\s\-–—]", string.Empty);
        Match m = Regex.Match(compact, @"^([A-Z])(\d{2})(\d{2})(\d{5,6})$");
        return m.Success ? $"{m.Groups[1].Value}{m.Groups[2].Value}-{m.Groups[3].Value}-{m.Groups[4].Value}" : null;
    }

    public static string? ValidateLicenseNumber(string? input, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return required ? "Enter the license number." : null;
        }

        return NormalizeLicenseNumber(input) is string n && LicenseNumberPattern.IsMatch(n)
            ? null
            : "License number must look like A01-23-456789 (a letter, then 2-2-6 digits).";
    }

    // LTO DL codes (new card) and the older restriction codes 1-8.
    private static readonly string[] DlCodes = { "A", "A1", "B", "B1", "B2", "C", "D", "BE", "CE" };
    private static readonly string[] ClassificationWords = { "PROFESSIONAL", "NON-PROFESSIONAL", "NON PROFESSIONAL", "STUDENT", "STUDENT PERMIT" };

    /// <summary>License classification: DL codes ("A, B, B1, B2") or Professional /
    /// Non-Professional / Student.</summary>
    public static string? ValidateLicenseClassification(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        string value = NormalizeSpaces(input).ToUpperInvariant();
        if (ClassificationWords.Contains(value))
        {
            return null;
        }

        string[] codes = value.Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return codes.Length > 0 && codes.All(c => DlCodes.Contains(c))
            ? null
            : "Use LTO DL codes separated by commas (A, A1, B, B1, B2, C, D, BE, CE), or Professional / Non-Professional.";
    }

    /// <summary>"a,b1 b2" → "A, B1, B2"; other valid values are just trimmed.</summary>
    public static string NormalizeLicenseClassification(string? input)
    {
        string value = NormalizeSpaces(input).ToUpperInvariant();
        if (value.Length == 0 || ClassificationWords.Contains(value))
        {
            return NormalizeSpaces(input);
        }

        return string.Join(", ", value.Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries).Distinct());
    }

    /// <summary>Restriction codes: digits 1-8 separated by commas, or "None".</summary>
    public static string? ValidateRestrictionCode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || NormalizeSpaces(input).Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] codes = input.Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return codes.All(c => c.Length == 1 && c[0] is >= '1' and <= '8')
            ? null
            : "Restriction codes are numbers 1-8 separated by commas (e.g. 1, 2), or None.";
    }

    /// <summary>An expiry date typed by hand: after 2000 and no more than 10 years ahead (the
    /// longest LTO validity). An expired date is allowed - the status will show Expired.</summary>
    public static string? ValidateLicenseExpiry(DateTime? expiry, DateTime today)
    {
        if (expiry is not DateTime date)
        {
            return null;
        }

        if (date.Year < 2000)
        {
            return "Expiry date looks wrong - check the year.";
        }

        return date.Date > today.Date.AddYears(10)
            ? "Expiry date can't be more than 10 years from today."
            : null;
    }

    // ---------------------------------------------------------------------
    // Free text, money and counts
    // ---------------------------------------------------------------------

    public static string? ValidateText(string? value, string fieldName, int maxLength, bool required = false, int minLength = 0)
    {
        string text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return required ? $"Enter {fieldName}." : null;
        }

        if (text.Length < minLength)
        {
            return $"{Capitalize(fieldName)} is too short - at least {minLength} characters.";
        }

        return text.Length > maxLength ? $"{Capitalize(fieldName)} is too long ({maxLength} characters max)." : null;
    }

    /// <summary>A peso amount: more than zero (or zero when allowed), at most 2 decimals, at most <paramref name="max"/>.</summary>
    public static string? ValidateAmount(decimal? amount, string fieldName, decimal max = 1_000_000m, bool allowZero = false)
    {
        if (amount is not decimal value)
        {
            return $"Enter {fieldName}.";
        }

        if (value < 0 || (!allowZero && value == 0))
        {
            return $"{Capitalize(fieldName)} must be more than ₱0.";
        }

        if (decimal.Round(value, 2) != value)
        {
            return $"{Capitalize(fieldName)} can have at most 2 decimal places.";
        }

        return value > max ? $"{Capitalize(fieldName)} can't be more than ₱{max:#,##0}." : null;
    }

    public static string? ValidateWholeNumber(int? value, string fieldName, int min, int max)
    {
        if (value is not int n)
        {
            return $"Enter {fieldName}.";
        }

        if (n < min)
        {
            return $"{Capitalize(fieldName)} can't be less than {min}.";
        }

        return n > max ? $"{Capitalize(fieldName)} can't be more than {max:#,##0}." : null;
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

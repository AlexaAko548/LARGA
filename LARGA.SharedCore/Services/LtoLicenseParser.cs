using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Reads a Philippine LTO driver's license from OCR text (ManagerWeb's Driver &amp; Shifts
/// profile - the OCR runs in the manager's browser, see wwwroot/ocr.js) and validates it:
/// it must be a Philippine license, the holder must be 18+, and it must not be expired or
/// expiring. Separate from the mobile app's DriverLicenseTextParser, which only extracts
/// expiry (as "the latest date on the card") and has no date of birth to validate age with.
///
/// Card layout (Non-Professional and Professional alike) - each row of labels is followed by
/// a row of values, which OCR keeps as separate lines in reading order:
///   REPUBLIC OF THE PHILIPPINES / DEPARTMENT OF TRANSPORTATION / LAND TRANSPORTATION OFFICE
///   NON-PROFESSIONAL DRIVER'S LICENSE
///   Last Name. First Name. Middle Name      -> DELA CRUZ, JOSIE
///   Nationality  Sex  Date of Birth  ...     -> PHL  F  2002/07/06  58  1.87
///   License No.  Expiration Date  ...        -> B12-19-00375  2025/07/06  B12
/// </summary>
public static class LtoLicenseParser
{
    public class Result
    {
        public bool IsPhilippineLicense { get; set; }
        public string? FullName { get; set; }
        public string? LicenseNumber { get; set; }

        /// <summary>"Professional" or "Non-Professional", from the card's title.</summary>
        public string? Classification { get; set; }

        public DateTime? DateOfBirth { get; set; }

        /// <summary>The date field was printed as a "YYYY/MM/DD" template instead of a date
        /// (sample/specimen cards) - reported as its own error rather than "unreadable".</summary>
        public bool DateOfBirthIsPlaceholder { get; set; }

        public DateTime? ExpiryDate { get; set; }
        public bool ExpiryIsPlaceholder { get; set; }
    }

    /// <summary>Per-field problems, null when that field is fine. Shown as red text under
    /// the matching field; the scan can only be applied to a profile when all are null.</summary>
    public class Validation
    {
        public string? LicenseError { get; set; }
        public string? NameError { get; set; }
        public string? DateOfBirthError { get; set; }
        public string? ExpiryError { get; set; }
        public int? Age { get; set; }

        public bool IsValid => LicenseError is null && NameError is null && DateOfBirthError is null && ExpiryError is null;
    }

    // Text only a PH LTO license carries. OCR garbles some of it (e.g. "Repusuc OF THE
    // PHILIPPINES"), so each marker is a fragment, and two of them are enough.
    private static readonly string[] PhilippineMarkers =
    {
        "OF THE PHILIPPINES",
        "LAND TRANSPORTATION",
        "DEPARTMENT OF TRANSPORTATION",
        "DRIVER'S LICENSE",
        "DRIVERS LICENSE",
    };

    // LTO format is letter + 2 digits (agency code) - 2 digits - 6 digits, e.g. A01-23-456789.
    // Some cards/specimens print a 5-digit tail (B12-19-00375), so 5-6 is accepted.
    private static readonly Regex LicenseNumberPattern = new(
        @"(?<![A-Z0-9])([A-Z])\s*(\d{2})\s*[-–—−]\s*(\d{2})\s*[-–—−]\s*(\d{5,6})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // YYYY/MM/DD - OCR sometimes spaces out the separators ("2025/07 / 06").
    private static readonly Regex DatePattern = new(@"(?<!\d)((?:19|20)\d{2})\s*[/\-.]\s*(\d{1,2})\s*[/\-.]\s*(\d{1,2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex PlaceholderDatePattern = new(@"Y{2,4}\s*/\s*M{1,2}\s*/\s*D{1,2}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BirthLabel = new(@"B[il]r?t?h|Bith|Birt", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExpiryLabel = new(@"Expir|Expiration|Valid\s+Until", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NameLabel = new(@"Last\s*Name", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static Result Parse(string? ocrText) => ParseBest(new[] { ocrText });

    /// <summary>Combines OCR passes over the same photo (ocr.js reads it twice - enlarged +
    /// grayscale, then as uploaded) and takes each field from the first pass that yields
    /// it. On the sample cards, one pass read "AD01-23-456789" where the other got
    /// "A01-23-456789" right.</summary>
    public static Result ParseBest(IEnumerable<string?> ocrTexts)
    {
        var passes = ocrTexts.Select(t => ParseOne(t ?? string.Empty)).ToList();
        if (passes.Count == 0)
        {
            return new Result();
        }

        Result? withDob = passes.FirstOrDefault(p => p.DateOfBirth is not null);
        Result? withExpiry = passes.FirstOrDefault(p => p.ExpiryDate is not null);

        return new Result
        {
            IsPhilippineLicense = passes.Any(p => p.IsPhilippineLicense),
            FullName = passes.Select(p => p.FullName).FirstOrDefault(n => n is not null),
            // Prefer a number whose agency code is a proper letter + 2 digits on its own.
            LicenseNumber = passes.Select(p => p.LicenseNumber).FirstOrDefault(n => n is not null),
            Classification = passes.Select(p => p.Classification).FirstOrDefault(c => c is not null),
            DateOfBirth = withDob?.DateOfBirth,
            DateOfBirthIsPlaceholder = withDob is null && passes.Any(p => p.DateOfBirthIsPlaceholder),
            ExpiryDate = withExpiry?.ExpiryDate,
            ExpiryIsPlaceholder = withExpiry is null && passes.Any(p => p.ExpiryIsPlaceholder),
        };
    }

    private static Result ParseOne(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        string upper = text.ToUpperInvariant().Replace('`', '\'').Replace('’', '\'');

        var result = new Result
        {
            IsPhilippineLicense = PhilippineMarkers.Count(m => upper.Contains(m)) >= 2,
            Classification = upper.Contains("NON-PROFESSIONAL") || upper.Contains("NON PROFESSIONAL") || upper.Contains("NONPROFESSIONAL")
                ? "Non-Professional"
                : upper.Contains("PROFESSIONAL") ? "Professional" : null,
        };

        int licenseLine = -1;
        for (int i = 0; i < lines.Count && result.LicenseNumber is null; i++)
        {
            Match m = LicenseNumberPattern.Match(lines[i]);
            if (m.Success)
            {
                result.LicenseNumber = $"{m.Groups[1].Value.ToUpperInvariant()}{m.Groups[2].Value}-{m.Groups[3].Value}-{m.Groups[4].Value}";
                licenseLine = i;
            }
        }

        (result.DateOfBirth, result.DateOfBirthIsPlaceholder) = ValueUnderLabel(lines, BirthLabel);
        (result.ExpiryDate, result.ExpiryIsPlaceholder) = ValueUnderLabel(lines, ExpiryLabel);

        // Label row unreadable: the expiry is printed on the license number's row.
        if (result.ExpiryDate is null && !result.ExpiryIsPlaceholder && licenseLine >= 0)
        {
            result.ExpiryDate = FirstDate(lines[licenseLine]);
            result.ExpiryIsPlaceholder = result.ExpiryDate is null && PlaceholderDatePattern.IsMatch(lines[licenseLine]);
        }

        // Still missing one: the card has exactly two dates - birth is the earlier, expiry the later.
        var allDates = lines.Select(FirstDate).OfType<DateTime>().Distinct().OrderBy(d => d).ToList();
        if (allDates.Count >= 2)
        {
            if (result.DateOfBirth is null && !result.DateOfBirthIsPlaceholder && allDates[0] != result.ExpiryDate)
            {
                result.DateOfBirth = allDates[0];
            }
            if (result.ExpiryDate is null && !result.ExpiryIsPlaceholder && allDates[^1] != result.DateOfBirth)
            {
                result.ExpiryDate = allDates[^1];
            }
        }

        result.FullName = FindName(lines);
        return result;
    }

    /// <summary>Looks at the value row under a label row. Returns the date found there, or
    /// (null, true) when that row holds a YYYY/MM/DD template instead.</summary>
    private static (DateTime? Date, bool IsPlaceholder) ValueUnderLabel(List<string> lines, Regex label)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (!label.IsMatch(lines[i]))
            {
                continue;
            }

            // Value row is normally the next line; also check the label line itself in case
            // OCR merged the two.
            foreach (string candidate in new[] { i + 1 < lines.Count ? lines[i + 1] : string.Empty, lines[i] })
            {
                DateTime? date = FirstDate(candidate);
                if (date is not null)
                {
                    return (date, false);
                }
                if (PlaceholderDatePattern.IsMatch(candidate))
                {
                    return (null, true);
                }
            }
        }

        return (null, false);
    }

    private static DateTime? FirstDate(string line)
    {
        Match m = DatePattern.Match(line);
        if (m.Success
            && int.TryParse(m.Groups[1].Value, out int y)
            && int.TryParse(m.Groups[2].Value, out int mo)
            && int.TryParse(m.Groups[3].Value, out int d)
            && mo is >= 1 and <= 12
            && d >= 1 && d <= DateTime.DaysInMonth(y, mo))
        {
            return new DateTime(y, mo, d, 0, 0, 0, DateTimeKind.Utc);
        }

        return null;
    }

    private static string? FindName(List<string> lines)
    {
        int label = lines.FindIndex(l => NameLabel.IsMatch(l));
        if (label < 0 || label + 1 >= lines.Count)
        {
            return null;
        }

        // Keep only the letters/commas/periods of the value row - OCR adds stray symbols from
        // the card's background pattern at the line edges.
        string raw = Regex.Replace(lines[label + 1], @"[^A-Za-zÑñ,.\s]", " ");
        raw = Regex.Replace(raw, @"\s+", " ").Trim();
        Match name = Regex.Match(raw, @"[A-ZÑ][A-ZÑ.]*(?:\s+[A-ZÑ][A-ZÑ.]*)*(?:,\s*[A-ZÑ][A-ZÑ.]*(?:\s+[A-ZÑ][A-ZÑ.]*)*)?");
        return name.Success && name.Value.Length >= 4 ? name.Value.Trim() : null;
    }

    /// <summary>
    /// Whether the name on a license belongs to the registered driver: their first name and
    /// last name (first and last word of the registered name) must each appear on the card.
    /// Middle names/initials are ignored on both sides, as is word order (the card prints
    /// "LAST, FIRST MIDDLE"). Not exact: a word counts when it's within a small edit distance,
    /// since OCR misreads single letters (e.g. "BAUTlSTA").
    /// </summary>
    public static bool NameMatches(string? registeredName, string? licenseName)
    {
        List<string> registered = NameWords(registeredName);
        List<string> onCard = NameWords(licenseName);
        if (registered.Count == 0 || onCard.Count == 0)
        {
            return false;
        }

        string first = registered[0];
        string last = registered[^1];
        return onCard.Any(w => IsCloseMatch(first, w)) && onCard.Any(w => IsCloseMatch(last, w));
    }

    // Upper-case words without accents/punctuation; single letters (initials) dropped.
    private static List<string> NameWords(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new List<string>();
        }

        string plain = new string(name.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray()).ToUpperInvariant();
        return Regex.Split(plain, @"[^A-Z]+").Where(w => w.Length >= 2).ToList();
    }

    private static bool IsCloseMatch(string a, string b)
    {
        if (a == b)
        {
            return true;
        }

        int allowed = Math.Max(1, Math.Min(a.Length, b.Length) / 4);
        return Math.Abs(a.Length - b.Length) <= allowed && EditDistance(a, b) <= allowed;
    }

    private static int EditDistance(string a, string b)
    {
        int[] previous = Enumerable.Range(0, b.Length + 1).ToArray();
        int[] current = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>The app's rules, in one place: Philippine license, the driver's own (first and
    /// last name match <paramref name="registeredName"/>, when given), holder 18+, and not
    /// expired or expiring. "Expiring" uses the same 3-month window as the roster's
    /// License Status (DriverManagementService.ComputeLicenseStatus).</summary>
    public static Validation Validate(Result r, DateTime today, string? registeredName = null)
    {
        today = today.Date;
        var v = new Validation();

        if (!r.IsPhilippineLicense)
        {
            // Nothing else on a non-LTO document is worth reporting field by field.
            v.LicenseError = "This doesn't look like a Philippine LTO driver's license - upload a clear photo of the front of the card.";
            return v;
        }

        if (r.LicenseNumber is null)
        {
            v.LicenseError = "Couldn't read the license number - retake the photo, or type it in below.";
        }

        if (!string.IsNullOrWhiteSpace(registeredName))
        {
            if (r.FullName is null)
            {
                v.NameError = "Couldn't read the name on the license - retake the photo.";
            }
            else if (!NameMatches(registeredName, r.FullName))
            {
                v.NameError = $"The name on this license ({r.FullName}) doesn't match this driver ({registeredName}) - their first and last name must be on it.";
            }
        }

        if (r.DateOfBirthIsPlaceholder)
        {
            v.DateOfBirthError = "Date of birth is not a real date (card shows YYYY/MM/DD).";
        }
        else if (r.DateOfBirth is not DateTime dob)
        {
            v.DateOfBirthError = "Couldn't read the date of birth - retake the photo.";
        }
        else if (dob > today)
        {
            v.DateOfBirthError = $"Date of birth {dob:MMM d, yyyy} is in the future.";
        }
        else
        {
            int age = today.Year - dob.Year - (today < dob.AddYears(today.Year - dob.Year) ? 1 : 0);
            v.Age = age;
            if (age < 18)
            {
                v.DateOfBirthError = $"Driver is only {age} (born {dob:MMM d, yyyy}) - must be at least 18.";
            }
        }

        if (r.ExpiryIsPlaceholder)
        {
            v.ExpiryError = "Expiration date is not a real date (card shows YYYY/MM/DD).";
        }
        else if (r.ExpiryDate is not DateTime exp)
        {
            v.ExpiryError = "Couldn't read the expiration date - retake the photo.";
        }
        else if (exp < today)
        {
            v.ExpiryError = $"License expired on {exp:MMM d, yyyy}.";
        }
        else if (exp <= today.AddMonths(3))
        {
            v.ExpiryError = $"License expires on {exp:MMM d, yyyy} - within 3 months. Renew it first.";
        }
        else if (r.DateOfBirth is DateTime birth && exp <= birth)
        {
            v.ExpiryError = "Expiration date is before the date of birth - the card was misread, retake the photo.";
        }

        return v;
    }
}

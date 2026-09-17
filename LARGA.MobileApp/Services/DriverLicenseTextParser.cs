using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Best-effort text parser for a Philippine LTO driver's license, run against the raw text
/// blocks ML Kit's text recognizer returns for a photo of one. This is heuristic, not a real
/// ID-parsing library: it pattern-matches the fields the "Verify License Information" screen
/// needs (name, sex, DL codes, license number, expiry date) and leaves anything it can't find
/// null so the caller shows "--" and the driver retakes the photo. Expect this to need tuning
/// once it's run against real scanned cards on a real device - block ordering/spacing from
/// ML Kit is not guaranteed to match the card's visual layout.
/// </summary>
public static class DriverLicenseTextParser
{
    public class ParsedLicense
    {
        public string? FullName { get; set; }
        public string? Sex { get; set; }
        public string? LicenseNumber { get; set; }
        public string? DlCodes { get; set; }
        public DateTime? ExpiryDate { get; set; }

        // Enough to act on - a scan that found neither isn't worth saving.
        public bool HasMinimumData => !string.IsNullOrWhiteSpace(LicenseNumber) || ExpiryDate != null;
    }

    // Tolerant of common OCR misreads: a dash-like character other than a plain hyphen
    // (en dash, em dash, minus sign) and stray whitespace ML Kit sometimes inserts around
    // punctuation (e.g. "G01 - 25 - 009637"). Case-insensitive since a low-contrast photo can
    // occasionally get read as lowercase.
    private static readonly Regex LicenseNumberPattern = new(@"\b[A-Z]\d{2}\s*[-–—−]\s*\d{2}\s*[-–—−]\s*\d{6}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // PH LTO licenses print dates as YYYY/MM/DD (e.g. "2030/09/27"), not the year-last order
    // this used to assume - that mismatch meant the expiry date (and DOB fallback) never
    // matched on a real license, no matter how clear the photo was.
    private static readonly Regex DatePattern = new(@"\b(\d{4})[/\-](\d{1,2})[/\-](\d{1,2})\b", RegexOptions.Compiled);
    private static readonly Regex DlCodesPattern = new(@"\b[A-Z]\d?(?:\s*,\s*[A-Z]\d?){1,7}\b", RegexOptions.Compiled);

    // A real PH LTO license prints "Last Name, First Name Middle Name" - ONE comma, with the
    // first and middle names space-separated (not a second comma), e.g. "DELA CRUZ, JUAN
    // PEDRO GARCIA". This used to require two commas, which never matches an actual card - it
    // was silently leaving FullName null on every real scan tested.
    private static readonly Regex NamePattern = new(@"\b([A-Z][A-Z.\s]{1,30}),\s*([A-Z][A-Z.\s]{1,40})\b", RegexOptions.Compiled);

    public static ParsedLicense Parse(IEnumerable<string> textBlocks)
    {
        var lines = textBlocks
            .SelectMany(b => b.Split('\n'))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var result = new ParsedLicense();

        // License number and expiry date are printed on the same row on a PH license, so
        // scoping the date search to the line the license number was found on avoids picking
        // up the date of birth (also present on the card) instead.
        string? licenseLine = null;
        foreach (var line in lines)
        {
            var match = LicenseNumberPattern.Match(line);
            if (match.Success)
            {
                // Normalize away the stray whitespace/dash variants/casing the pattern above
                // tolerates, so what's shown and saved is always the clean "G01-25-009637" form.
                var digitsAndLetters = Regex.Replace(match.Value, @"\s+", "").ToUpperInvariant();
                result.LicenseNumber = Regex.Replace(digitsAndLetters, @"[–—−]", "-");
                licenseLine = line;
                break;
            }
        }

        if (licenseLine != null)
        {
            result.ExpiryDate = ExtractDate(licenseLine);
        }
        if (result.ExpiryDate == null)
        {
            // Fallback: take the first date found anywhere. Less reliable (could be the date
            // of birth) but better than nothing for a card whose layout didn't match above.
            foreach (var line in lines)
            {
                var date = ExtractDate(line);
                if (date != null)
                {
                    result.ExpiryDate = date;
                    break;
                }
            }
        }

        foreach (var line in lines)
        {
            var match = DlCodesPattern.Match(line);
            if (match.Success)
            {
                result.DlCodes = Regex.Replace(match.Value, @"\s*,\s*", ", ");
                break;
            }
        }

        var sexToken = lines
            .Select(l => Regex.Match(l, @"\b(Male|Female|M|F)\b"))
            .FirstOrDefault(m => m.Success);
        if (sexToken != null)
        {
            result.Sex = sexToken.Value.ToUpperInvariant() switch
            {
                "M" => "Male",
                "F" => "Female",
                _ => sexToken.Value
            };
        }

        foreach (var line in lines)
        {
            var match = NamePattern.Match(line);
            if (match.Success)
            {
                // Card prints "Last, First Middle" (one comma) - reorder to "First Middle
                // Last" for display. The given-name segment can itself be multiple words
                // (e.g. "JUAN PEDRO GARCIA" or a compound first name like "MARIA CRISTINA") -
                // Filipino naming convention treats the middle name as a single word (the
                // mother's maiden surname), so the LAST word of that segment is taken as the
                // middle name and everything before it as the (possibly multi-word) first name.
                var last = ToTitleCase(match.Groups[1].Value.Trim());
                var givenWords = match.Groups[2].Value.Trim()
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

                string first, middle;
                if (givenWords.Length <= 1)
                {
                    first = givenWords.Length == 1 ? ToTitleCase(givenWords[0]) : string.Empty;
                    middle = string.Empty;
                }
                else
                {
                    first = ToTitleCase(string.Join(" ", givenWords.Take(givenWords.Length - 1)));
                    middle = ToTitleCase(givenWords[^1]);
                }

                result.FullName = $"{first} {middle} {last}".Replace("  ", " ").Trim();
                break;
            }
        }

        return result;
    }

    private static DateTime? ExtractDate(string text)
    {
        var match = DatePattern.Match(text);
        if (!match.Success) return null;

        if (int.TryParse(match.Groups[1].Value, out var year) &&
            int.TryParse(match.Groups[2].Value, out var month) &&
            int.TryParse(match.Groups[3].Value, out var day))
        {
            try
            {
                return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    private static string ToTitleCase(string value) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
}

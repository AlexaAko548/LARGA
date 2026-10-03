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

        // ML Kit doesn't guarantee the license number stays on one OCR "line" - a cramped or
        // small text field can get split across two, e.g. "G01-25-" / "009637". Matching
        // against every line AND every adjacent pair (joined with no separator, since a
        // mid-token break has none) catches that case without losing the single-line case.
        var candidateSpans = lines
            .Concat(lines.Zip(lines.Skip(1), (a, b) => a + b))
            .Concat(lines.Zip(lines.Skip(1), (a, b) => $"{a} {b}"));

        foreach (var span in candidateSpans)
        {
            var match = LicenseNumberPattern.Match(span);
            if (!match.Success) continue;

            // Normalize away the stray whitespace/dash variants/casing the pattern above
            // tolerates, so what's shown and saved is always the clean "G01-25-009637" form.
            var digitsAndLetters = Regex.Replace(match.Value, @"\s+", "").ToUpperInvariant();
            result.LicenseNumber = Regex.Replace(digitsAndLetters, @"[–—−]", "-");
            break;
        }

        // A PH license always prints the date of birth AND the (later) expiry date - rather
        // than trying to scope to "the line near the license number" (fragile: whichever line
        // OCR happened to split things onto), take every date found anywhere on the card and
        // pick the latest one. Physically, expiry can never predate birth, so this is reliable
        // regardless of line layout - and fixes the DOB being picked up as "expiry" whenever
        // the old line-scoping heuristic missed.
        var allDates = lines
            .Select(ExtractDate)
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .ToList();
        result.ExpiryDate = allDates.Count > 0 ? allDates.Max() : null;

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

    /// <summary>
    /// Whether the OCR'd name on a scanned license plausibly belongs to the given registered
    /// driver name - used to reject a scan of someone else's license. Not exact-string
    /// equality: OCR can misread a single character or return names in a different word
    /// order, so this checks that most of the registered name's words show up somewhere in
    /// the OCR'd name, tolerating one mismatched word rather than requiring a perfect match.
    /// </summary>
    public static bool NamesLikelyMatch(string? registeredName, string? ocrName)
    {
        var registeredWords = SignificantWords(registeredName);
        var ocrWords = SignificantWords(ocrName);

        // Nothing to compare against on either side - can't say it's a mismatch, so don't
        // block a scan just because OCR (or the driver record) is missing a name.
        if (registeredWords.Count == 0 || ocrWords.Count == 0) return true;

        // A word "matches" if any OCR word is close enough by edit distance - not exact
        // equality, since a single misread character (e.g. "BAUTISTA" -> "BAUTlSTA") is common
        // enough on real photos that requiring a perfect match would reject genuine scans as
        // often as it catches real mismatches. Tolerance scales with word length.
        var matchingWords = registeredWords.Count(rw => ocrWords.Any(ow => IsCloseMatch(rw, ow)));

        // Roughly two-thirds of the registered name's words must show up - tolerates one
        // additional word being missing entirely (not just misspelled) on a 3+ word name, but
        // a 2-word name needs both (too little room otherwise to tell "close enough" apart
        // from "different person").
        var requiredMatches = (int)Math.Ceiling(registeredWords.Count * 2.0 / 3.0);
        return matchingWords >= requiredMatches;
    }

    private static bool IsCloseMatch(string a, string b)
    {
        if (a == b) return true;
        var maxAllowedDistance = Math.Max(1, Math.Min(a.Length, b.Length) / 4);
        return LevenshteinDistance(a, b) <= maxAllowedDistance;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var previousRow = Enumerable.Range(0, b.Length + 1).ToArray();
        var currentRow = new int[b.Length + 1];

        for (var i = 1; i <= a.Length; i++)
        {
            currentRow[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                currentRow[j] = Math.Min(Math.Min(currentRow[j - 1] + 1, previousRow[j] + 1), previousRow[j - 1] + cost);
            }
            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[b.Length];
    }

    private static HashSet<string> SignificantWords(string? name) =>
        (name ?? string.Empty)
            .ToUpperInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1) // skips lone middle initials like "R."
            .Select(w => w.TrimEnd('.'))
            .ToHashSet();
}

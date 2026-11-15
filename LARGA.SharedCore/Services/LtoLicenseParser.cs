using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Reads a Philippine LTO driver's license from OCR text and validates it. Used by both license
/// scans: ManagerWeb's Driver &amp; Shifts profile (Tesseract in the manager's browser, see
/// wwwroot/ocr.js) and the mobile app's Scan Driver License page (ML Kit, several passes over
/// differently filtered copies of the photo - LicenseScanPipeline).
///
/// Card layout (Non-Professional and Professional alike) - each row of labels is followed by
/// a row of values, which OCR keeps as separate lines in reading order:
///   REPUBLIC OF THE PHILIPPINES / DEPARTMENT OF TRANSPORTATION / LAND TRANSPORTATION OFFICE
///   NON-PROFESSIONAL DRIVER'S LICENSE
///   Last Name. First Name. Middle Name      -> DELA CRUZ, JOSIE
///   Nationality  Sex  Date of Birth  ...     -> PHL  F  2002/07/06  58  1.87
///   License No.  Expiration Date  Agency Code -> B12-19-00375  2025/07/06  B12
///   Blood Type  Eyes Color / DL Codes  Conditions -> A,B,B1,B2  1
///
/// The card's shiny security seal (hologram) and glare sit right over the License No. row, so a
/// single read often loses or garbles digits there. Every pass is parsed on its own and the
/// fields are then voted on (ParseBest); a license number that only one pass read, that needed
/// letters turned into digits, or that the passes disagree on is flagged as uncertain so the
/// screen asks the user to check it against the card instead of saving a guess.
/// </summary>
public static class LtoLicenseParser
{
    public class Result
    {
        public bool IsPhilippineLicense { get; set; }
        public string? FullName { get; set; }
        public string? LicenseNumber { get; set; }

        /// <summary>The license number may be misread: passes disagree, it was only read by
        /// repairing letters into digits (e.g. "G09637"), it doesn't start with the card's
        /// Agency Code, or it has the short 5-digit tail. Show it, but ask for a check.</summary>
        public bool LicenseNumberUncertain { get; set; }

        /// <summary>"Professional" or "Non-Professional", from the card's title.</summary>
        public string? Classification { get; set; }

        /// <summary>DL codes as "A, B, B1, B2" (the new card's vehicle codes).</summary>
        public string? DlCodes { get; set; }

        /// <summary>"Male" or "Female".</summary>
        public string? Sex { get; set; }

        /// <summary>The card's Agency Code field (e.g. "G01") - also the license number's prefix.</summary>
        public string? AgencyCode { get; set; }

        public DateTime? DateOfBirth { get; set; }

        /// <summary>The date field was printed as a "YYYY/MM/DD" template instead of a date
        /// (sample/specimen cards) - reported as its own error rather than "unreadable".</summary>
        public bool DateOfBirthIsPlaceholder { get; set; }

        public DateTime? ExpiryDate { get; set; }
        public bool ExpiryIsPlaceholder { get; set; }

        /// <summary>The passes read different expiry dates.</summary>
        public bool ExpiryUncertain { get; set; }

        /// <summary>Number of letters turned into digits to get this pass's license number,
        /// not counting O→0 (OCR reads the zero in "G01" as O on almost every card).</summary>
        public int LicenseNumberRepairs { get; set; }

        /// <summary>The License No. row starts like a number ("G01-25-...") but the rest
        /// didn't read - glare over the seal hid digits.</summary>
        public bool LicenseRowGarbled { get; set; }

        /// <summary>Everything a save needs, read with no doubts - more OCR passes won't help.</summary>
        public bool IsConfident =>
            LicenseNumber is not null && !LicenseNumberUncertain
            && ExpiryDate is not null && !ExpiryUncertain
            && FullName is not null;
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

    // A digit as OCR may print it: under the seal or in poor light ML Kit and Tesseract read
    // 0 as O/Q/D/C/G, 1 as I/l/|, 5 as S, 8 as B and 2 as Z. Only accepted where the card
    // has a digit, and turned back into one (DigitFor).
    private const string DigitLike = "[0-9OoQDCGIil|SsBZ]";

    // LTO format is letter + 2 digits (agency code) - 2 digits - 6 digits, e.g. A01-23-456789.
    // Some cards/specimens print a 5-digit tail (B12-19-00375), so 5-6 is accepted. The first
    // dash is optional because OCR sometimes drops it ("GO125-...").
    private static readonly Regex LicenseNumberPattern = new(
        $@"(?<![A-Za-z0-9])([A-Z])\s*({DigitLike}{{2}})\s*[-–—−]?\s*({DigitLike}{{2}})\s*[-–—−]\s*({DigitLike}{{5,6}})(?![A-Za-z0-9])",
        RegexOptions.Compiled);

    // The start of a license number - enough to tell the row was there but didn't fully read.
    private static readonly Regex NumberStart = new(@"(?<![A-Za-z0-9])[A-Z][0-9OoIl]{2}\s*-?\s*\d{2}\s*[-–—−]", RegexOptions.Compiled);

    // YYYY/MM/DD - OCR sometimes spaces out the separators ("2025/07 / 06").
    private static readonly Regex DatePattern = new(@"(?<!\d)((?:19|20)\d{2})\s*[/\-.]\s*(\d{1,2})\s*[/\-.]\s*(\d{1,2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex PlaceholderDatePattern = new(@"Y{2,4}\s*/\s*M{1,2}\s*/\s*D{1,2}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BirthLabel = new(@"B[il]r?t?h|Bith|Birt", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExpiryLabel = new(@"Expir|Expiration|Valid\s+Until", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Last Name. First Name. Middle Name" - OCR misreads it ("Last Nane. ... Middie Name"),
    // so any one of its words is enough.
    private static readonly Regex NameLabel = new(@"Last\s*N\w{2}e|First\s*N\w{2}e|Midd\w{1,2}\s*N\w{2}e", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AgencyLabel = new(@"Agen\w*\s*Cod", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AgencyValue = new(@"(?<![A-Za-z0-9])([A-Z])([0-9OoIl]{2})(?![A-Za-z0-9])", RegexOptions.Compiled);
    private static readonly Regex DlCodesLabel = new(@"DL\s*Cod|Restrict", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SexToken = new(@"(?<![A-Za-z])([MF])(?![A-Za-z])", RegexOptions.Compiled);

    // The codes printed on the new card (InputValidator accepts the same).
    private static readonly HashSet<string> KnownDlCodes = new() { "A", "A1", "B", "B1", "B2", "C", "D", "BE", "CE" };

    public static Result Parse(string? ocrText) => ParseBest(new[] { ocrText });

    /// <summary>Combines OCR passes over the same card (ocr.js reads it enlarged + grayscale
    /// and as uploaded; the phone also reads glare-filtered copies and a crop of the License
    /// No. row) by voting per field. Passes earlier in the list win ties.</summary>
    public static Result ParseBest(IEnumerable<string?> ocrTexts)
    {
        var passes = ocrTexts.Select(t => ParseOne(t ?? string.Empty)).ToList();
        if (passes.Count == 0)
        {
            return new Result();
        }

        string? agency = Vote(passes.Select(p => p.AgencyCode)).Winner;
        (string? number, bool numberUncertain) = VoteLicenseNumber(passes, agency);

        var expiry = Vote(passes.Select(p => p.ExpiryDate));
        var dob = Vote(passes.Select(p => p.DateOfBirth));

        return new Result
        {
            IsPhilippineLicense = passes.Any(p => p.IsPhilippineLicense),
            FullName = VoteName(passes.Select(p => p.FullName)),
            LicenseNumber = number,
            LicenseNumberUncertain = numberUncertain,
            Classification = Vote(passes.Select(p => p.Classification)).Winner,
            DlCodes = Vote(passes.Select(p => p.DlCodes)).Winner,
            Sex = Vote(passes.Select(p => p.Sex)).Winner,
            AgencyCode = agency,
            DateOfBirth = dob.Winner,
            DateOfBirthIsPlaceholder = dob.Winner is null && passes.Any(p => p.DateOfBirthIsPlaceholder),
            ExpiryDate = expiry.Winner,
            ExpiryIsPlaceholder = expiry.Winner is null && passes.Any(p => p.ExpiryIsPlaceholder),
            ExpiryUncertain = expiry.Contested,
        };
    }

    /// <summary>The most common value (earliest pass on a tie). Contested when the runner-up
    /// has more than half the winner's votes - e.g. 1 vs 1, 2 vs 2, 3 vs 2, but not 3 vs 1.</summary>
    private static (T? Winner, bool Contested) Vote<T>(IEnumerable<T?> values)
    {
        var groups = values
            .Select((v, i) => (v, i))
            .Where(x => x.v is not null)
            .GroupBy(x => x.v!)
            .Select(g => (Value: g.Key, Count: g.Count(), First: g.Min(x => x.i)))
            .OrderByDescending(g => g.Count).ThenBy(g => g.First)
            .ToList();

        if (groups.Count == 0)
        {
            return (default, false);
        }

        bool contested = groups.Count > 1 && groups[1].Count * 2 > groups[0].Count;
        return (groups[0].Value, contested);
    }

    private static (string? Number, bool Uncertain) VoteLicenseNumber(List<Result> passes, string? agency)
    {
        var reads = passes
            .Where(p => p.LicenseNumber is not null)
            .Select((p, i) => (Number: p.LicenseNumber!, p.LicenseNumberRepairs, Index: i))
            .ToList();
        if (reads.Count == 0)
        {
            return (null, false);
        }

        // A 5-digit tail next to a 6-digit one is the 6-digit number with a digit lost under
        // the seal - only the 6-digit reads are counted then.
        static int TailLength(string n) => n.Length - n.LastIndexOf('-') - 1;
        if (reads.Any(r => TailLength(r.Number) == 6))
        {
            reads = reads.Where(r => TailLength(r.Number) == 6).ToList();
        }

        // The Agency Code field is printed again as the number's prefix.
        bool agencyChecked = agency is not null && reads.Any(r => r.Number.StartsWith(agency, StringComparison.Ordinal));
        if (agencyChecked)
        {
            reads = reads.Where(r => r.Number.StartsWith(agency!, StringComparison.Ordinal)).ToList();
        }

        var groups = reads
            .GroupBy(r => r.Number)
            .Select(g => (Number: g.Key, Count: g.Count(), Repairs: g.Min(r => r.LicenseNumberRepairs), First: g.Min(r => r.Index)))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Repairs).ThenBy(g => g.First)
            .ToList();

        var best = groups[0];

        // Real cards have a 6-digit tail. A 5-digit read while another pass saw the row cut short
        // is the seal's glare eating a digit (e.g. "09837" for "009637") - better left blank for
        // a retake or typing than shown as a plausible wrong number.
        if (TailLength(best.Number) != 6 && passes.Any(p => p.LicenseRowGarbled))
        {
            return (null, false);
        }
        bool uncertain = best.Repairs > 0
            || TailLength(best.Number) != 6
            || (agency is not null && !agencyChecked)
            || (groups.Count > 1 && groups[1].Count * 2 > best.Count);
        return (best.Number, uncertain);
    }

    /// <summary>Votes on the name ignoring accents ("MIÑOZA" and "MINOZA" are one vote), and
    /// shows the spelling with the Ñ when any pass read it.</summary>
    private static string? VoteName(IEnumerable<string?> names)
    {
        var list = names.Where(n => n is not null).Select(n => n!).ToList();
        string? key = Vote(list.Select(StripAccents)).Winner;
        if (key is null)
        {
            return null;
        }

        var spellings = list.Where(n => StripAccents(n) == key).ToList();
        return spellings.FirstOrDefault(n => n.Contains('Ñ')) ?? spellings[0];
    }

    private static string StripAccents(string s) =>
        new string(s.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray());

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

        int licenseLine = FindLicenseNumber(lines, result);
        result.LicenseRowGarbled = result.LicenseNumber is null && lines.Any(l => NumberStart.IsMatch(l));

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

        // An expiry that isn't after the birth date is the birth date read twice.
        if (result.ExpiryDate is DateTime e && result.DateOfBirth is DateTime b && e <= b)
        {
            result.ExpiryDate = null;
        }

        result.FullName = FindName(lines);
        result.AgencyCode = FindAgencyCode(lines);
        result.DlCodes = FindDlCodes(lines);
        result.Sex = FindSex(lines, result.DateOfBirth);
        return result;
    }

    /// <summary>Finds the license number (also split over two lines, "G01-25-" / "009637"),
    /// sets it on <paramref name="result"/> and returns its line, or -1.</summary>
    private static int FindLicenseNumber(List<string> lines, Result result)
    {
        (string Number, int Repairs, int Line)? fiveDigit = null;
        for (int i = 0; i < lines.Count; i++)
        {
            var spans = new List<string> { lines[i] };
            if (i + 1 < lines.Count && Regex.IsMatch(lines[i], @"[-–—−]\s*$"))
            {
                spans.Add(lines[i] + lines[i + 1]);
            }

            foreach (string span in spans)
            {
                foreach (Match m in LicenseNumberPattern.Matches(span))
                {
                    string tailRaw = m.Groups[4].Value;
                    // A real number, not a word that happens to fit: most of the tail and part
                    // of the middle must be read as actual digits.
                    if (tailRaw.Count(char.IsDigit) < 3 || !m.Groups[3].Value.Any(char.IsDigit))
                    {
                        continue;
                    }

                    string number = $"{m.Groups[1].Value}{Digits(m.Groups[2].Value)}-{Digits(m.Groups[3].Value)}-{Digits(tailRaw)}";
                    int repairs = Repairs(m.Groups[3].Value) + Repairs(tailRaw);
                    if (tailRaw.Length == 6)
                    {
                        result.LicenseNumber = number;
                        result.LicenseNumberRepairs = repairs;
                        return i;
                    }

                    fiveDigit ??= (number, repairs, i);
                }
            }
        }

        if (fiveDigit is { } f)
        {
            result.LicenseNumber = f.Number;
            result.LicenseNumberRepairs = f.Repairs;
            return f.Line;
        }

        return -1;
    }

    private static string Digits(string s) => new string(s.Select(DigitFor).ToArray());

    private static char DigitFor(char c) => c switch
    {
        'O' or 'o' or 'Q' or 'D' or 'C' or 'G' => '0',
        'I' or 'i' or 'l' or '|' => '1',
        'S' or 's' => '5',
        'B' => '8',
        'Z' => '2',
        _ => c,
    };

    private static int Repairs(string s) => s.Count(c => !char.IsDigit(c) && c is not ('O' or 'o'));

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

    /// <summary>The "LAST, FIRST MIDDLE" row: the first of the three lines under the name label
    /// that looks like one, or - label unreadable - the first such line on the card. A name has
    /// exactly one comma and no digits, which rules out the address ("NASIPIT, TALAMBAN, CEBU
    /// CITY (CAPITAL)," / "CEBU, 6000").</summary>
    private static string? FindName(List<string> lines)
    {
        int label = lines.FindIndex(l => NameLabel.IsMatch(l));
        IEnumerable<string> candidates = label >= 0 ? lines.Skip(label + 1).Take(3) : lines;
        foreach (string line in candidates)
        {
            if (AsName(line) is string name)
            {
                return name;
            }
        }

        return null;
    }

    private static string? AsName(string line)
    {
        if (line.Count(c => c == ',') != 1 || line.Any(char.IsDigit))
        {
            return null;
        }

        // Keep only the letters/commas/periods of the value row - OCR adds stray symbols from
        // the card's background pattern at the line edges.
        string raw = Regex.Replace(line, @"[^A-Za-zÑñ,.\s]", " ");
        raw = Regex.Replace(raw, @"\s+", " ").Trim();
        Match name = Regex.Match(raw, @"^[A-ZÑ][A-ZÑ.]*(?:\s+[A-ZÑ][A-ZÑ.]*)*,\s*[A-ZÑ][A-ZÑ.]*(?:\s+[A-ZÑ][A-ZÑ.]*)*$");
        return name.Success && name.Value.Length >= 4 ? name.Value.Trim() : null;
    }

    private static string? FindAgencyCode(List<string> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (!AgencyLabel.IsMatch(lines[i]))
            {
                continue;
            }

            foreach (string candidate in new[] { i + 1 < lines.Count ? lines[i + 1] : string.Empty, AgencyLabel.Replace(lines[i], " ") })
            {
                Match m = AgencyValue.Match(candidate.Trim());
                if (m.Success && m.Value.Length == candidate.Trim().Length)
                {
                    return m.Groups[1].Value + Digits(m.Groups[2].Value);
                }
            }
        }

        return null;
    }

    /// <summary>"A,B,B 1,B2" → "A, B, B1, B2". Taken from the lines under the DL Codes label,
    /// or any line made only of known codes.</summary>
    private static string? FindDlCodes(List<string> lines)
    {
        int label = lines.FindIndex(l => DlCodesLabel.IsMatch(l));
        IEnumerable<string> candidates = label >= 0
            ? lines.Skip(label + 1).Take(2).Prepend(DlCodesLabel.Replace(lines[label], " ")).Concat(lines)
            : lines;

        foreach (string line in candidates)
        {
            string compact = Regex.Replace(line.ToUpperInvariant(), @"\s+", string.Empty).Replace('.', ',').Trim(',');
            if (compact.Length == 0 || !compact.Contains(','))
            {
                continue;
            }

            // "BI"/"Bl" is B1 read with a letter.
            var codes = compact.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Length == 2 && c[1] is 'I' or 'L' or '|' ? $"{c[0]}1" : c)
                .ToList();
            if (codes.Count >= 1 && codes.All(KnownDlCodes.Contains))
            {
                return string.Join(", ", codes.Distinct());
            }
        }

        return null;
    }

    /// <summary>Sex is printed on the birth date's row ("PHL F 2005/09/27"), before the date.</summary>
    private static string? FindSex(List<string> lines, DateTime? dateOfBirth)
    {
        foreach (string line in lines)
        {
            Match date = DatePattern.Match(line);
            bool onBirthRow = date.Success && FirstDate(line) is DateTime d && (dateOfBirth is null || d == dateOfBirth);
            string before = onBirthRow ? line[..date.Index] : line.Trim() is "M" or "F" ? line : string.Empty;
            Match sex = SexToken.Match(before);
            if (sex.Success)
            {
                return sex.Value == "M" ? "Male" : "Female";
            }
        }

        return null;
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

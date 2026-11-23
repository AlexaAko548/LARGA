using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Turns the OCR text of a dashboard photo into odometer readings for the driver to pick from.
///
/// A taxi odometer shows 5-6 digits ("058700", leading zeros kept); dial markings (10 ... 140) are
/// at most 3. Only tokens that already contain a digit get the usual OCR letter fixes (O/D -> 0,
/// I/L/| -> 1, S -> 5, B -> 8, Z -> 2, G -> 6), so labels like "ODO" or "km" are never turned into
/// digits. A reading split into groups ("58 700", "58,700") is joined; a trip meter with a decimal
/// ("123.4") is skipped.
/// </summary>
public static class OdometerReadingParser
{
    private const int MinDigits = 5;
    private const int MaxDigits = 6;

    private static readonly Regex Separators = new(@"[\s]+", RegexOptions.CultureInvariant);
    // "KM" but not "KM/L" (fuel economy) or "KM/H" (speed).
    private static readonly Regex OdometerLabel = new(@"(?<![A-Z])(?:ODO|ODOMETER|TOTAL|KM|KMS)(?![A-Z/])", RegexOptions.CultureInvariant);
    private static readonly Regex TripLabel = new(@"(?<![A-Z])(?:TRIP|TRP|RANGE|AVG|KM/L|L/100)(?![A-Z])", RegexOptions.CultureInvariant);
    // ML Kit reads a small "ODO" printed before the reading as "000", "O00" or "O0O" ("o00 5377"
    // on a Vios cluster) - a label, not three more digits.
    private static readonly Regex OdoLookalike = new(@"(?<![A-Z0-9])[0OD]{3}(?=\s+[0-9OIL|SBZG]{4,}(?![0-9A-Z]))", RegexOptions.CultureInvariant);

    /// <summary>A reading offered to the driver. Not <paramref name="Confident"/> when ML Kit and
    /// the seven-segment decoder disagreed, or only one of them read it, so the driver checks it.</summary>
    public readonly record struct Reading(string Text, bool Confident);

    // Seven-segment digits one segment apart. On an LCD readout ML Kit reads a 6 as a 5 (the
    // lower-left segment missed); the decoder, which looks at the segments, is right about those.
    private static readonly HashSet<string> OneSegmentApart = new() { "08", "17", "39", "56", "59", "68", "89" };

    /// <summary>Readings found in one OCR line or block, best first.</summary>
    public static List<string> Candidates(string? text) => Candidates(new[] { text });

    /// <summary>Readings across several OCR lines/blocks, best first (next to "ODO"/"km" before
    /// unlabelled, unlabelled before "TRIP"), without duplicates.</summary>
    public static List<string> Candidates(IEnumerable<string?> texts) =>
        texts.SelectMany(Ranked)
            .Where(f => !f.Reading.All(c => c == f.Reading[0]))
            .OrderByDescending(f => f.Rank)
            .ThenByDescending(f => f.Reading.Length)
            .Select(f => f.Reading)
            .Distinct()
            .ToList();

    private static List<(string Reading, int Rank)> Ranked(string? text)
    {
        var found = new List<(string Reading, int Rank)>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return found;
        }

        foreach (string rawLine in text.Split('\n'))
        {
            string line = OdoLookalike.Replace(rawLine.ToUpperInvariant(), "ODO");
            bool trip = TripLabel.IsMatch(line);
            int rank = OdometerLabel.IsMatch(line) ? 2 : trip ? 0 : 1;

            var groups = new List<string>();
            foreach (string token in Separators.Split(line))
            {
                string? digits = DigitsOf(token);
                if (digits is null)
                {
                    Flush(groups, found, rank);
                    continue;
                }
                groups.Add(digits);
            }
            Flush(groups, found, rank);
        }

        return found;
    }

    /// <summary>A token's digits after OCR letter fixes, or null if it isn't a number. "123.4"
    /// (trip meter) is not a number here; "58,700" is 58700.</summary>
    private static string? DigitsOf(string token)
    {
        string t = token.Trim(':', ';', '-', '(', ')', '[', ']', '|');
        if (t.Length == 0 || !t.Any(char.IsDigit))
        {
            return null;
        }

        // Unit stuck to the number: "58700KM".
        t = Regex.Replace(t, @"(?:KMS?|MI)$", string.Empty);

        if (Regex.IsMatch(t, @"\d[.]\d{1,2}$"))
        {
            return null; // trip / fuel economy with decimals
        }

        var sb = new StringBuilder();
        foreach (char c in t.Replace(",", string.Empty).Replace(".", string.Empty))
        {
            char d = c switch
            {
                'O' or 'D' or 'Q' => '0',
                'I' or 'L' or '|' or '!' => '1',
                'S' => '5',
                'B' => '8',
                'Z' => '2',
                'G' => '6',
                _ => c,
            };
            if (!char.IsDigit(d))
            {
                return null;
            }
            sb.Append(d);
        }

        // A token that was mostly letters isn't a reading ("OIL" has no digit at all and is
        // rejected above); at least a third of it must be real digits ("7OO" is fine).
        int realDigits = t.Count(char.IsDigit);
        return realDigits * 3 >= sb.Length ? sb.ToString() : null;
    }

    private static void Flush(List<string> groups, List<(string, int)> found, int rank)
    {
        if (groups.Count == 0)
        {
            return;
        }

        // Speedometer marks ML Kit put on one line ("120 140", "120 l40 0") aren't a reading
        // split into groups; a lone digit next to them is a tick or a bit of the needle.
        if (groups.Count(IsDialMark) >= 2 && groups.All(g => IsDialMark(g) || g.Length == 1))
        {
            groups.Clear();
            return;
        }

        // Each group on its own, and neighbours joined ("58" "700" -> "58700").
        for (int start = 0; start < groups.Count; start++)
        {
            string joined = string.Empty;
            for (int end = start; end < groups.Count; end++)
            {
                if (end > start && groups[end].Length > 3)
                {
                    break; // only short groups are pieces of one reading
                }
                joined += groups[end];
                if (joined.Length > MaxDigits)
                {
                    break;
                }
                if (joined.Length >= MinDigits)
                {
                    found.Add((joined, rank));
                }
            }
        }

        groups.Clear();
    }

    private static bool IsDialMark(string group) =>
        group.Length is 2 or 3 && int.Parse(group) is int value && value >= 10 && value <= 260 && value % 10 == 0;

    /// <summary>
    /// Whether an OCR line may hold a reading ML Kit only partly read - a number of 4+ digits after
    /// the usual letter fixes ("o00 5377", "o0o S859" for 25377 and 58609) - so the seven-segment
    /// decoder should look at it too.
    /// </summary>
    public static bool MayHoldReading(string? text) => PartialReading(text) != null;

    /// <summary>The longest 4+ digit number on a line after the usual letter fixes ("5377" in
    /// "o00 5377"), or null. Part of a reading at best - never offered on its own.</summary>
    public static string? PartialReading(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Split('\n')
            .SelectMany(line => Separators.Split(OdoLookalike.Replace(line.ToUpperInvariant(), "ODO")))
            .Select(DigitsOf)
            .Where(digits => digits?.Length >= 4)
            .OrderByDescending(digits => digits!.Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// The reading in a framed readout's OCR passes (each pass's lines, best pass first). A proper
    /// reading from any pass comes before a short number from any: the enhanced pass can split
    /// "25377" into "2537" / "17" while the plain one reads it whole. A short number (3-7 digits)
    /// is still what the driver framed - a low-mileage unit, or a drum partly behind the needle.
    /// </summary>
    public static string? FramedReading(IEnumerable<IEnumerable<string?>> passes)
    {
        var lines = passes.Select(p => p.ToList()).ToList();
        foreach (var pass in lines)
        {
            if (Candidates(pass).FirstOrDefault() is string reading)
            {
                return reading;
            }
        }

        foreach (var pass in lines)
        {
            string digits = pass
                .Select(l => new string((l ?? string.Empty).Where(char.IsDigit).ToArray()))
                .OrderByDescending(d => d.Length)
                .FirstOrDefault() ?? string.Empty;
            if (digits.Length is >= 3 and <= 7)
            {
                return digits;
            }
        }
        return null;
    }

    /// <summary>
    /// ML Kit's reading checked against the seven-segment decoder's read of the same digits.
    /// Where they differ by one segment and the decoder is sure, its digit is taken (ML Kit's 6
    /// read as 5); any disagreement leaves the reading not Confident. With no ML Kit reading, a
    /// full 5-6 digit decode is offered on its own (not Confident), with the digits it wasn't sure
    /// of taken from the part ML Kit did read (<paramref name="partial"/>: "5377" of 25377, the
    /// first digit lost next to "ODO"). '?' cells at either end of the decode are the "ODO" label
    /// or a bit of the frame. Null when neither read a reading.
    /// </summary>
    public static Reading? Reconcile(string? ocr, SevenSegmentDecoder.DecodeResult decoded, string? partial = null)
    {
        var digits = decoded.Digits
            .SkipWhile(d => d.Digit == '?')
            .Reverse()
            .SkipWhile(d => d.Digit == '?')
            .Reverse()
            .ToList();

        if (ocr == null)
        {
            char[] text = digits.Select(d => d.Digit).ToArray();
            if (partial != null && partial.Length < text.Length)
            {
                // Where the part lines up best; on a tie, at the end (the digit lost is the
                // one next to the label).
                int offset = Enumerable.Range(0, text.Length - partial.Length + 1)
                    .OrderByDescending(o => partial.Where((c, i) => c == text[o + i]).Count())
                    .ThenByDescending(o => o)
                    .First();
                for (int i = 0; i < partial.Length; i++)
                {
                    if (!digits[offset + i].IsConfident)
                    {
                        text[offset + i] = partial[i];
                    }
                }
            }

            bool fullDecode = text.Length is >= MinDigits and <= MaxDigits && !text.Contains('?');
            return fullDecode ? new Reading(new string(text), Confident: false) : null;
        }

        if (digits.Count == 0)
        {
            // Nothing to compare with (a drum odometer, or the decoder found no segments).
            return new Reading(ocr, Confident: true);
        }

        if (digits.Count != ocr.Length)
        {
            return new Reading(ocr, Confident: false);
        }

        char[] merged = ocr.ToCharArray();
        bool agree = true;
        for (int i = 0; i < merged.Length; i++)
        {
            char d = digits[i].Digit;
            if (d == ocr[i])
            {
                continue;
            }

            agree = false;
            string pair = d < ocr[i] ? $"{d}{ocr[i]}" : $"{ocr[i]}{d}";
            if (digits[i].IsConfident && OneSegmentApart.Contains(pair))
            {
                merged[i] = d;
            }
        }
        return new Reading(new string(merged), agree);
    }
}

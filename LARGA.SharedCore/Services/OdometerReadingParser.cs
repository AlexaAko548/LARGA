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
    private static readonly Regex OdometerLabel = new(@"(?<![A-Z])(?:ODO|ODOMETER|TOTAL|KM|KMS)(?![A-Z])", RegexOptions.CultureInvariant);
    private static readonly Regex TripLabel = new(@"(?<![A-Z])(?:TRIP|TRP|RANGE|AVG|KM/L|L/100)(?![A-Z])", RegexOptions.CultureInvariant);

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
            string line = rawLine.ToUpperInvariant();
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
}

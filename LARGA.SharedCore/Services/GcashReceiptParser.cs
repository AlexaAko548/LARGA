using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using LARGA.SharedCore.Ledger;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Pulls Amount, Date and Reference No. out of the OCR'd text of a GCash payment receipt
/// (the screenshot a driver sends the manager after paying their boundary online). The OCR
/// itself runs in the manager's browser (wwwroot/ocr.js) - this only interprets its
/// text, so it's plain C# and testable without an image.
///
/// GCash receipts look roughly like:
///   Sent via GCash
///   Amount                1,000.00
///   Total Amount Sent    ₱1,000.00
///   Ref No. 5012 345 678901    Sep 24, 2026 9:37 AM
/// OCR routinely misreads the peso sign (as P, F, or nothing), so amounts are found by their
/// label and "digits.2 decimals" shape rather than by the currency symbol.
/// </summary>
public static class GcashReceiptParser
{
    public class Result
    {
        public decimal? Amount { get; set; }
        public DateTime? Date { get; set; }

        /// <summary>Digits only, spaces removed. A string, not a number: GCash reference
        /// numbers are 13 digits, which doesn't fit in an int.</summary>
        public string? ReferenceNumber { get; set; }

        /// <summary>The GCash number / bank account the money was sent to (often masked, e.g.
        /// "+63 917 *** 4567"), for checking against the authorized payout accounts.</summary>
        public TargetAccount? TargetAccount { get; set; }
    }

    // Thousands separator is a comma only. Allowing a space too turned "₱ 850.00" - whose peso
    // sign OCR read as "[1" - into 1,850.00.
    private static readonly Regex MoneyPattern = new(@"(\d{1,3}(?:,\d{3})+|\d+)\.(\d{2})(?!\d)", RegexOptions.Compiled);

    // "Ref No. 5012 345 678901", "Ref. No: 5012345678901", "Reference No. 5012 345 678901"
    private static readonly Regex LabelledReferencePattern = new(
        @"Ref(?:erence)?\s*\.?\s*(?:No|Number|#)\s*\.?\s*:?\s*([0-9][0-9 ]{6,22}[0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // GCash's own format when the label is garbled: 4-3-6 digit groups.
    private static readonly Regex GroupedReferencePattern = new(@"(?<!\d)(\d{4}\s\d{3}\s\d{6})(?!\d)", RegexOptions.Compiled);

    private static readonly Regex MonthNameDatePattern = new(
        @"(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)[a-z]*\.?\s+(\d{1,2}),?\s+(\d{4})(?:\s*(?:at\s*)?(\d{1,2}):(\d{2})\s*([AP]\.?M\.?))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumericDatePattern = new(
        @"(?<!\d)(\d{1,2})[/-](\d{1,2})[/-](\d{4})(?:\s*(\d{1,2}):(\d{2})\s*([AP]\.?M\.?)?)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Result Parse(string? ocrText) => ParseBest(new[] { ocrText });

    /// <summary>
    /// Combines several OCR passes over the same receipt (ocr.js reads it twice: once
    /// enlarged + grayscale, once as uploaded). Neither pass is reliably right on every field -
    /// in testing, the enlarged pass read the date and amount correctly but turned a 6 in the
    /// reference into a B, while the original pass got the reference right but read the year
    /// as 7028. So each field is taken from the first pass that gives a *valid-looking* value.
    /// </summary>
    public static Result ParseBest(IEnumerable<string?> ocrTexts)
    {
        var passes = ocrTexts.Select(SplitLines).ToList();

        // A full 13-digit GCash reference from any pass beats a partial one from an earlier pass.
        string? reference = passes.Select(lines => FindReference(string.Join("\n", lines))).FirstOrDefault(r => r?.Length == 13)
            ?? passes.Select(lines => FindReference(string.Join("\n", lines))).FirstOrDefault(r => r is not null);

        return new Result
        {
            Amount = passes.Select(FindAmount).FirstOrDefault(a => a is > 0),
            Date = passes.Select(lines => FindDate(string.Join("\n", lines))).FirstOrDefault(d => d is not null),
            ReferenceNumber = reference,
            // A recipient whose digits all show beats a masked read from an earlier pass.
            TargetAccount = passes.Select(lines => PayoutAccountMatcher.ExtractTarget(lines, reference)).FirstOrDefault(t => t is { IsMasked: false })
                ?? passes.Select(lines => PayoutAccountMatcher.ExtractTarget(lines, reference)).FirstOrDefault(t => t is not null),
        };
    }

    private static List<string> SplitLines(string? text) =>
        (text ?? string.Empty)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

    private static decimal? FindAmount(List<string> lines)
    {
        // The plain "Amount" line first: it's what actually reached the recipient (the
        // boundary), whereas "Total Amount Sent" adds any transfer fee the driver paid on top.
        // It's also the line OCR reads most reliably - "Total Amount Sent" carries the peso
        // sign, which OCR has been seen to read as "[1", turning ₱1,000.00 into 11,000.00.
        for (int i = 0; i < lines.Count; i++)
        {
            if (Regex.IsMatch(lines[i], @"^\W*Amount\b", RegexOptions.IgnoreCase))
            {
                decimal? value = FirstMoney(lines[i]) ?? (i + 1 < lines.Count ? FirstMoney(lines[i + 1]) : null);
                if (value is > 0)
                {
                    return value;
                }
            }
        }

        foreach (string label in new[] { "total amount", "amount sent", "amount" })
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (!lines[i].Contains(label, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Value is usually on the same line; some layouts put it on the next one.
                decimal? value = FirstMoney(lines[i]) ?? (i + 1 < lines.Count ? FirstMoney(lines[i + 1]) : null);
                if (value is > 0)
                {
                    return value;
                }
            }
        }

        // No usable label - take the largest money-shaped value on the receipt.
        return lines.Select(FirstMoney).Where(v => v is > 0).Max();
    }

    private static decimal? FirstMoney(string line)
    {
        Match match = MoneyPattern.Match(line);
        if (!match.Success)
        {
            return null;
        }

        string whole = match.Groups[1].Value.Replace(",", string.Empty);
        return decimal.TryParse($"{whole}.{match.Groups[2].Value}", NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value)
            ? value
            : null;
    }

    private static string? FindReference(string text)
    {
        Match labelled = LabelledReferencePattern.Match(text);
        if (labelled.Success)
        {
            string digits = Regex.Replace(labelled.Groups[1].Value, @"\s", string.Empty);
            if (digits.Length >= 8)
            {
                return digits;
            }
        }

        Match grouped = GroupedReferencePattern.Match(text);
        return grouped.Success ? Regex.Replace(grouped.Groups[1].Value, @"\s", string.Empty) : null;
    }

    // A misread digit in the year (e.g. 2026 -> 7028) still parses as a date, so anything
    // outside a plausible range is treated as not found rather than shown.
    private static bool IsPlausibleYear(int year) => year is >= 2000 and <= 2099;

    private static DateTime? FindDate(string text)
    {
        foreach (Match named in MonthNameDatePattern.Matches(text))
        {
            string month = named.Groups[1].Value.Substring(0, 3);
            if (DateTime.TryParseExact($"{month} {named.Groups[2].Value} {named.Groups[3].Value}", "MMM d yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)
                && IsPlausibleYear(date.Year))
            {
                return WithTime(date, named.Groups[4], named.Groups[5], named.Groups[6]);
            }
        }

        // Philippine receipts write numeric dates month-first (MM/DD/YYYY).
        Match numeric = NumericDatePattern.Match(text);
        if (numeric.Success
            && int.TryParse(numeric.Groups[1].Value, out int m)
            && int.TryParse(numeric.Groups[2].Value, out int d)
            && int.TryParse(numeric.Groups[3].Value, out int y)
            && IsPlausibleYear(y)
            && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m))
        {
            return WithTime(new DateTime(y, m, d), numeric.Groups[4], numeric.Groups[5], numeric.Groups[6]);
        }

        return null;
    }

    private static DateTime WithTime(DateTime date, Group hour, Group minute, Group meridiem)
    {
        if (!hour.Success || !int.TryParse(hour.Value, out int h) || !int.TryParse(minute.Value, out int min) || h > 23 || min > 59)
        {
            return date;
        }

        if (meridiem.Success && h <= 12)
        {
            bool pm = meridiem.Value.StartsWith("P", StringComparison.OrdinalIgnoreCase);
            h = (h % 12) + (pm ? 12 : 0);
        }

        return date.AddHours(h).AddMinutes(min);
    }
}

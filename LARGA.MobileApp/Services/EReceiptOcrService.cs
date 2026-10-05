using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LARGA.MobileApp.Services;

/// <summary>Fields read from a GCash e-receipt. A null field means OCR didn't find it.</summary>
public sealed record EReceiptData(decimal? Amount, DateTime? Date, string? ReferenceNumber)
{
    /// <summary>All three fields are needed before a scan can be used to record an e-wallet payment.</summary>
    public bool IsComplete => Amount is > 0 && Date.HasValue && !string.IsNullOrEmpty(ReferenceNumber);
}

/// <summary>Sent from the scan page to the payment form once the manager confirms a scan.</summary>
public sealed record EReceiptScanResult(decimal Amount, DateTime Date, string ReferenceNumber, string PhotoFilePath);

/// <summary>
/// Reads a GCash "Sent via GCash" e-receipt photo. OCR returns text in block order, and right-aligned values
/// often land in a different block from their label. So the parser looks for values near their keywords, and it
/// accepts a value that appears more than once on the receipt, instead of relying on exact line layout.
/// </summary>
public sealed class EReceiptOcrService
{
    private const int MinReferenceDigits = 10;
    private const int MaxReferenceDigits = 20;

    // 145.00 / 1,145.00 / 1 145.00 — the peso sign is optional because OCR often drops or mangles it.
    private static readonly Regex MoneyToken = new(@"(?<![\d.])(\d{1,3}(?:[,\s]\d{3})*\.\d{2}|\d+\.\d{2})(?!\d)", RegexOptions.CultureInvariant);
    private static readonly Regex AmountKeyword = new(@"AMOUNT|SENT|TOTAL", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ReferenceKeyword = new(@"REF\.?\s*(NO|NUM)?\.?|REFERENCE", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ReferenceDigits = new(@"(?:\d[\s-]?){" + MinReferenceDigits + "," + MaxReferenceDigits + "}", RegexOptions.CultureInvariant);

    // Digits (with optional spaces or dashes between groups) at the start of a line's remainder. Anchored, so it
    // stops at the end of the line or at the first letter or punctuation, such as the "." in "145.00".
    private static readonly Regex LeadingReferenceDigits = new(@"^[\s:#.]*((?:\d[\s-]?)*\d)", RegexOptions.CultureInvariant);
    private static readonly Regex Date = new(@"\b(JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)[A-Z]*\.?\s+(\d{1,2}),?\s+(\d{4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IOcrService _ocr;

    public EReceiptOcrService(IOcrService ocr)
    {
        _ocr = ocr;
    }

    /// <summary>Runs OCR on a captured photo and reads the receipt fields.</summary>
    public async Task<EReceiptData> ReadAsync(string imagePath, DateTime philippineToday)
    {
        List<OcrTextBlock> blocks = await _ocr.ExtractTextBlocksAsync(imagePath);

        // One block can hold several lines, so split on newlines before matching.
        List<string> lines = blocks
            .Where(b => !string.IsNullOrWhiteSpace(b.Text))
            .SelectMany(b => b.Text.Split('\n'))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        return Parse(lines, philippineToday);
    }

    /// <summary>Pure parsing of OCR lines. Kept static so it can be tested without a camera.</summary>
    public static EReceiptData Parse(IReadOnlyList<string> lines, DateTime philippineToday) =>
        new(ParseAmount(lines), ParseDate(lines, philippineToday), ParseReference(lines));

    private static decimal? ParseAmount(IReadOnlyList<string> lines)
    {
        var tokens = new List<(decimal Value, int Line)>();
        for (int i = 0; i < lines.Count; i++)
        {
            foreach (Match match in MoneyToken.Matches(lines[i]))
            {
                string digits = match.Groups[1].Value.Replace(",", string.Empty).Replace(" ", string.Empty);
                if (decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value) && value > 0)
                {
                    tokens.Add((value, i));
                }
            }
        }

        if (tokens.Count == 0) return null;

        // 1) A value on an "Amount"/"Total Amount Sent" line, or on the line just below one.
        var keywordLines = Enumerable.Range(0, lines.Count).Where(i => AmountKeyword.IsMatch(lines[i])).ToHashSet();
        var nearKeyword = tokens
            .Where(t => keywordLines.Contains(t.Line) || keywordLines.Contains(t.Line - 1))
            .Select(t => t.Value)
            .ToList();
        if (nearKeyword.Count > 0)
        {
            return MostCommon(nearKeyword);
        }

        // 2) Otherwise the value that appears most often. A receipt usually prints the amount twice.
        var repeated = tokens.Select(t => t.Value).ToList();
        return repeated.GroupBy(v => v).Where(g => g.Count() >= 2).Select(g => g.Key).Any()
            ? MostCommon(repeated)
            : null;
    }

    private static decimal MostCommon(IEnumerable<decimal> values) =>
        values.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;

    private static string? ParseReference(IReadOnlyList<string> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            Match keyword = ReferenceKeyword.Match(lines[i]);
            if (!keyword.Success) continue;

            // The number is normally on the same line as its label. Only if that line has no digits do we
            // look at the next line, because OCR sometimes splits the label from its value. Matching must
            // not cross lines: the next line can hold the amount, which would be glued onto the reference.
            string sameLine = lines[i].Substring(keyword.Index + keyword.Length);
            string? digits = DigitsOf(LeadingReferenceDigits.Match(sameLine).Groups[1].Value);
            if (digits != null) return digits;

            if (i + 1 < lines.Count)
            {
                digits = DigitsOf(LeadingReferenceDigits.Match(lines[i + 1]).Groups[1].Value);
                if (digits != null) return digits;
            }
        }

        // No label found: take a standalone run of digits that looks like a GCash reference.
        foreach (string line in lines)
        {
            string? digits = DigitsOf(ReferenceDigits.Match(line).Value);
            if (digits != null) return digits;
        }

        return null;
    }

    private static string? DigitsOf(string raw)
    {
        string digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length >= MinReferenceDigits && digits.Length <= MaxReferenceDigits ? digits : null;
    }

    private static DateTime? ParseDate(IReadOnlyList<string> lines, DateTime philippineToday)
    {
        Match match = Date.Match(string.Join("\n", lines));
        if (!match.Success) return null;

        string month = match.Groups[1].Value;
        string normalized = $"{month[..1].ToUpperInvariant()}{month[1..].ToLowerInvariant()} {match.Groups[2].Value} {match.Groups[3].Value}";
        if (!DateTime.TryParseExact(normalized, "MMM d yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        {
            return null;
        }

        // Ignore dates that can't be this receipt: far in the past or in the future.
        bool plausible = parsed.Date <= philippineToday.Date.AddDays(1) && parsed.Date >= philippineToday.Date.AddYears(-3);
        return plausible ? parsed.Date : null;
    }
}

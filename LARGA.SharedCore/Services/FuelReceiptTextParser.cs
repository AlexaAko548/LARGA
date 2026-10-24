using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace LARGA.SharedCore.Services;

/// <summary>
/// The fuel receipt OCR parsing (station, amount, liters, date, warning) moved unchanged out of
/// ScanFuelReceiptPage so it can be unit tested without MAUI or an image. The OR number lives in
/// FuelReceiptParser.
/// </summary>
public static class FuelReceiptTextParser
{
    /// <summary>One OCR text block with its bounding box, normalized to 0-1 of the image
    /// (AndroidOcrService's OcrTextBlock without the MAUI Rect).</summary>
    public readonly record struct OcrBlock(string? Text, double X, double Y, double Width, double Height);

    /// <summary>FUEL-004: only Petron and Shell (FuelStations, shared with the rest of the app). Any
    /// other receipt reads as UNKNOWN - the first OCR line is no longer taken as the station - and
    /// is submitted Flagged unless the driver corrects it to one of the two.</summary>
    public static string? TryParseVendor(string fullText) =>
        FuelStations.Match(fullText) ?? FuelStations.Match(NormalizeVendorText(fullText));

    // "0.987L x 96.700P/L" - a per-liter rate, never a total. Thermal POS receipts print this
    // right next to the actual line amount, and a rate is very often numerically bigger than
    // the total when the purchase is small (buy under 1L and the rate alone exceeds what was
    // paid) - exactly what mis-extracted LAR-105's reference receipt (P96.70 picked over the
    // real P95.44 total). Matched as its own pattern so it can be kept out of the last-resort
    // "biggest number on the receipt" fallback below, regardless of which OCR block it lands in.
    private static readonly Regex UnitPriceToken = new(@"\d+(?:[.,]\d+)?\s*L\s*[xX]\s*(\d+(?:[.,]\d+)?)\s*P?\s*/\s*L", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static decimal? TryParseAmount(string fullText, List<string> lines, IReadOnlyList<OcrBlock>? blocks = null)
    {
        var unitPriceValues = new HashSet<decimal>();
        foreach (Match unitMatch in UnitPriceToken.Matches(fullText))
        {
            if (TryParseDecimal(unitMatch.Groups[1].Value, out var rate))
            {
                unitPriceValues.Add(rate);
            }
        }

        var candidates = new List<(decimal Value, int Score)>();
        // Per-block info kept only when the caller has bounding boxes, for the row-alignment
        // pass below (tier 2) - label and value land in the same visual row but, on a lot of
        // real POS layouts, in two separate OCR blocks (one column of labels, one of
        // right-aligned values), so neither "same line" nor "next line" ever lines them up.
        var blockInfos = new List<(string UpperText, bool HasKeyword, bool IsLikelyNonTotal, OcrBlock Box, List<decimal> Values)>();

        foreach (var line in lines)
        {
            var upperLine = line.ToUpperInvariant();
            var hasAmountKeyword = upperLine.Contains("TOTAL") || upperLine.Contains("AMOUNT") || upperLine.Contains("NET") || upperLine.Contains("SALE") || upperLine.Contains("DUE") || upperLine.Contains("PAYABLE");
            var isLikelyNonTotal = upperLine.Contains("VAT") || upperLine.Contains("CHANGE") || upperLine.Contains("DISCOUNT") || upperLine.Contains("PRICE/L") || upperLine.Contains("UNIT PRICE") || upperLine.Contains("LITER") || upperLine.Contains("LTR") || upperLine.Contains("QTY");
            var score = hasAmountKeyword ? (upperLine.Contains("TOTAL") ? 3 : 2) : 0;

            if (isLikelyNonTotal)
            {
                score--;
            }

            foreach (Match match in Regex.Matches(line, @"(?:PHP|P|₱|\?)?\s*\d{1,3}(?:[\s,]\d{3})*(?:[\.,]\d{2,3})|(?:PHP|P|₱|\?)?\s*\d+[\.,]\d{2,3}"))
            {
                if (TryParseDecimal(match.Value, out var value) && value > 0)
                {
                    candidates.Add((value, score));
                }
            }
        }

        var bestKeywordAmount = candidates
            .Where(c => c.Score >= 2)
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Value)
            .FirstOrDefault();

        if (bestKeywordAmount.Value > 0)
        {
            return bestKeywordAmount.Value;
        }

        var keywordMatch = Regex.Match(fullText.ToUpperInvariant(),
            @"(?:TOTAL\s+AMOUNT|AMOUNT\s+DUE|NET\s+AMOUNT|GRAND\s+TOTAL|TOTAL|SALE)\s*[:=]?\s*(PHP|P|₱|\?)?\s*(\d{1,3}(?:[,\s]\d{3})*(?:[\.,]\d{2,3})|\d+[\.,]\d{2,3})");
        if (keywordMatch.Success && TryParseDecimal(keywordMatch.Groups[2].Value, out var keyedAmount))
        {
            return keyedAmount;
        }

        // Tier 2: the keyword and its value are on separate OCR blocks (a label column and a
        // value column, grouped as two blocks by ML Kit) - find a numeric block sitting on the
        // same visual row as a keyword block, by bounding-box Y, instead of text adjacency.
        if (blocks is { Count: > 0 })
        {
            foreach (OcrBlock block in blocks)
            {
                string text = block.Text ?? string.Empty;
                string upper = text.ToUpperInvariant();
                bool hasKeyword = upper.Contains("TOTAL") || upper.Contains("AMOUNT") || upper.Contains("NET") || upper.Contains("SALE") || upper.Contains("DUE") || upper.Contains("PAYABLE");
                bool isLikelyNonTotal = upper.Contains("VAT") || upper.Contains("CHANGE") || upper.Contains("DISCOUNT") || upper.Contains("PRICE/L") || upper.Contains("UNIT PRICE") || upper.Contains("LITER") || upper.Contains("LTR") || upper.Contains("QTY");
                var values = new List<decimal>();
                foreach (Match m in Regex.Matches(text, @"(?:PHP|P|₱|\?)?\s*\d{1,3}(?:[\s,]\d{3})*(?:[\.,]\d{2,3})|(?:PHP|P|₱|\?)?\s*\d+[\.,]\d{2,3}"))
                {
                    if (TryParseDecimal(m.Value, out var v) && v > 0)
                    {
                        values.Add(v);
                    }
                }
                blockInfos.Add((upper, hasKeyword, isLikelyNonTotal, block, values));
            }

            foreach (var keywordBlock in blockInfos.Where(b => b.HasKeyword && !b.IsLikelyNonTotal))
            {
                double keywordRowY = keywordBlock.Box.Y + keywordBlock.Box.Height / 2;
                double tolerance = Math.Max(keywordBlock.Box.Height, 1) * 0.8;

                var rowMatch = blockInfos
                    .Where(b => b.Values.Count > 0 && !b.IsLikelyNonTotal
                                && Math.Abs((b.Box.Y + b.Box.Height / 2) - keywordRowY) <= tolerance)
                    .SelectMany(b => b.Values.Select(v => (Value: v, Right: b.Box.X + b.Box.Width)))
                    .Where(v => v.Value >= 10m && !unitPriceValues.Contains(v.Value))
                    .OrderByDescending(v => v.Right)
                    .FirstOrDefault();

                if (rowMatch.Value > 0)
                {
                    return rowMatch.Value;
                }
            }
        }

        var fallback = candidates
            .Where(c => c.Value >= 10m && !unitPriceValues.Contains(c.Value))
            .OrderByDescending(c => c.Value)
            .FirstOrDefault();

        return fallback.Value > 0 ? fallback.Value : null;
    }

    public static decimal? TryParseLiters(string fullText, List<string> lines)
    {
        var matches = new List<decimal>();

        foreach (var line in lines)
        {
            foreach (Match match in Regex.Matches(line.ToUpperInvariant(), @"(?:QTY|QUANTITY|VOLUME|VOL|LITERS?|LITRES?|LTRS?)\s*[:=]?\s*(\d{1,3}(?:[\.,]\d{1,3})?)"))
            {
                if (TryParseDecimal(match.Groups[1].Value, out var liters) && liters > 0m && liters <= 200m)
                {
                    matches.Add(liters);
                }
            }

            foreach (Match match in Regex.Matches(line.ToUpperInvariant(), @"(\d{1,3}(?:[\.,]\d{1,3})?)\s*(?:LITERS?|LITRES?|LTRS?|L)\b"))
            {
                if (TryParseDecimal(match.Groups[1].Value, out var liters) && liters > 0m && liters <= 200m)
                {
                    matches.Add(liters);
                }
            }
        }

        if (matches.Count > 0)
        {
            return matches.OrderByDescending(v => v).FirstOrDefault();
        }

        var fullTextMatch = Regex.Match(fullText.ToUpperInvariant(),
            @"(?:QTY|QUANTITY|VOLUME|VOL)\s*[:=]?\s*(\d{1,3}(?:[\.,]\d{1,3})?)|(\d{1,3}(?:[\.,]\d{1,3})?)\s*(?:LITERS?|LITRES?|LTRS?|L)\b");

        if (!fullTextMatch.Success)
        {
            return null;
        }

        var token = !string.IsNullOrWhiteSpace(fullTextMatch.Groups[1].Value)
            ? fullTextMatch.Groups[1].Value
            : fullTextMatch.Groups[2].Value;

        return TryParseDecimal(token, out var parsedLiters) && parsedLiters > 0m ? parsedLiters : null;
    }

    public static DateTime? TryParseReceiptDate(string fullText, List<string> lines)
    {
        var now = DateTime.Now.Date;

        var keywordDates = new List<DateTime>();
        foreach (var line in lines)
        {
            var upperLine = line.ToUpperInvariant();
            if (upperLine.Contains("DATE") || upperLine.Contains("TRANS") || upperLine.Contains("INVOICE"))
            {
                keywordDates.AddRange(ExtractDatesFromText(line));
            }
        }

        var bestKeywordDate = keywordDates
            .Where(IsPlausibleReceiptDate)
            .OrderBy(d => Math.Abs((d.Date - now).TotalDays))
            .FirstOrDefault();

        if (bestKeywordDate != default)
        {
            return bestKeywordDate.Date;
        }

        var patterns = new[]
        {
            @"\b\d{4}[-/]\d{1,2}[-/]\d{1,2}\b",
            @"\b\d{1,2}[-/]\d{1,2}[-/]\d{2,4}\b",
            @"\b(?:JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|SEPT|OCT|NOV|DEC)[A-Z]*\.?\s+\d{1,2},?\s+\d{2,4}\b"
        };

        var fallbackDates = new List<DateTime>();

        foreach (var pattern in patterns)
        {
            var matches = Regex.Matches(fullText.ToUpperInvariant(), pattern);
            foreach (Match match in matches)
            {
                if (TryParseDateFlexible(match.Value, out var dt))
                {
                    fallbackDates.Add(dt.Date);
                }
            }
        }

        return fallbackDates
            .Where(IsPlausibleReceiptDate)
            .OrderBy(d => Math.Abs((d.Date - now).TotalDays))
            .FirstOrDefault();
    }

    public static bool TryParseDecimal(string token, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var cleaned = Regex.Replace(token, @"[^\d,\.\-]", string.Empty);
        if (string.IsNullOrWhiteSpace(cleaned))
            return false;

        var lastDot = cleaned.LastIndexOf('.');
        var lastComma = cleaned.LastIndexOf(',');
        var decimalSeparatorIndex = Math.Max(lastDot, lastComma);

        if (decimalSeparatorIndex >= 0)
        {
            var integerPart = Regex.Replace(cleaned[..decimalSeparatorIndex], @"[^\d\-]", string.Empty);
            var decimalPart = Regex.Replace(cleaned[(decimalSeparatorIndex + 1)..], @"[^\d]", string.Empty);
            cleaned = string.IsNullOrWhiteSpace(decimalPart) ? integerPart : $"{integerPart}.{decimalPart}";
        }
        else
        {
            cleaned = Regex.Replace(cleaned, @"[^\d\-]", string.Empty);
        }

        return decimal.TryParse(cleaned, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseDateFlexible(string token, out DateTime date)
    {
        var formats = new[]
        {
            "yyyy-MM-dd", "yyyy/M/d", "yyyy/MM/dd",
            "M/d/yyyy", "MM/dd/yyyy", "d/M/yyyy", "dd/MM/yyyy",
            "M/d/yy", "MM/dd/yy", "d/M/yy", "dd/MM/yy",
            "MMM d yyyy", "MMM d, yyyy", "MMMM d yyyy", "MMMM d, yyyy",
            "MMM dd yyyy", "MMM dd, yyyy", "MMMM dd yyyy", "MMMM dd, yyyy"
        };

        return DateTime.TryParseExact(token.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date)
               || DateTime.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static IEnumerable<DateTime> ExtractDatesFromText(string text)
    {
        var patterns = new[]
        {
            @"\b\d{4}[-/]\d{1,2}[-/]\d{1,2}\b",
            @"\b\d{1,2}[-/]\d{1,2}[-/]\d{2,4}\b",
            @"\b(?:JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|SEPT|OCT|NOV|DEC)[A-Z]*\.?\s+\d{1,2},?\s+\d{2,4}\b"
        };

        foreach (var pattern in patterns)
        {
            var matches = Regex.Matches(text.ToUpperInvariant(), pattern);
            foreach (Match match in matches)
            {
                if (TryParseDateFlexible(match.Value, out var parsed))
                {
                    yield return parsed.Date;
                }
            }
        }
    }

    private static bool IsPlausibleReceiptDate(DateTime date)
    {
        var today = DateTime.Today;
        return date.Year >= 2020 && date.Date <= today.AddDays(1) && date.Date >= today.AddYears(-3);
    }

    private static string NormalizeVendorText(string text)
    {
        return text
            .ToUpperInvariant()
            .Replace('0', 'O')
            .Replace('1', 'I')
            .Replace('5', 'S');
    }

    public static string BuildParsingWarning(string? vendor, decimal? amount, decimal? liters, DateTime? date)
    {
        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(vendor) || vendor.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
            issues.Add("Station not recognized as Petron or Shell.");

        if (!amount.HasValue || amount.Value <= 0)
            issues.Add("Total amount was not confidently detected.");

        if (!liters.HasValue || liters.Value <= 0)
            issues.Add("Liters/quantity was not confidently detected.");

        if (!date.HasValue)
            issues.Add("Receipt date was not confidently detected.");

        return issues.Count == 0 ? string.Empty : "OCR uncertain: " + string.Join(" ", issues) + " Please review/edit before submit.";
    }
}

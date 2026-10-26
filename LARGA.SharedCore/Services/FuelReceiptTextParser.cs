using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Reads station, total, liters and date out of a fuel receipt's OCR text (the OR number lives in
/// FuelReceiptParser). Plain C#, so it's unit tested with the ML Kit output captured on a device.
///
/// It works on OCR *lines* with their boxes. Thermal POS receipts print labels on the left and
/// right-aligned amounts on the right, and ML Kit usually returns the two columns as separate
/// blocks - so a label is paired with the amount on the same printed row (corrected for a tilted
/// photo), and the total is cross-checked against the VAT breakdown every BIR receipt prints.
/// </summary>
public static class FuelReceiptTextParser
{
    /// <summary>One OCR line (or block) with its bounding box, normalized to 0-1 of the image.
    /// <paramref name="Slope"/> is the line's tilt (dy/dx in the same normalized units, from its
    /// corner points); 0 when unknown.</summary>
    public readonly record struct OcrBlock(string? Text, double X, double Y, double Width, double Height, double Slope = 0)
    {
        public double CenterX => X + Width / 2;
        public double CenterY => Y + Height / 2;
        public bool HasBox => Width > 0 && Height > 0;
    }

    /// <summary>Everything the receipt scan fills in.</summary>
    public sealed record ReceiptFields(
        string? Vendor,
        decimal? Amount,
        bool AmountUncertain,
        decimal? Liters,
        DateTime? Date,
        string? OrNumber,
        bool OrNumberUncertain,
        string Warning);

    /// <summary>Parses the OCR result of one receipt photo. Blocks spanning several lines are
    /// split into lines (with estimated boxes) when the OCR didn't return line boxes itself.</summary>
    public static ReceiptFields Parse(IReadOnlyList<OcrBlock> blocks)
    {
        List<OcrBlock> lines = SplitIntoLines(blocks);
        List<string> texts = lines.Select(l => l.Text!).ToList();
        string fullText = string.Join(" ", texts);

        string? vendor = TryParseVendor(fullText);
        decimal? amount = TryParseAmount(fullText, texts, lines, out bool amountUncertain);
        decimal? liters = TryParseLiters(fullText, texts);
        DateTime? date = TryParseReceiptDate(fullText, texts);
        string? orNumber = FuelReceiptParser.TryParseOrNumber(texts, out bool orUncertain);

        return new ReceiptFields(vendor, amount, amountUncertain, liters, date, orNumber, orUncertain,
            BuildParsingWarning(vendor, amount, liters, date, amountUncertain));
    }

    /// <summary>FUEL-004: only Petron and Shell (FuelStations, shared with the rest of the app). Any
    /// other receipt reads as UNKNOWN and is submitted Flagged.</summary>
    public static string? TryParseVendor(string fullText) =>
        FuelStations.Match(fullText) ?? FuelStations.Match(NormalizeVendorText(fullText));

    // ---------------------------------------------------------------------
    // Numbers
    // ---------------------------------------------------------------------

    // "0.987L x 96.700P/L", "31.94 L @ 56.39/L" - a per-liter rate, never a total.
    private static readonly Regex UnitPriceToken = new(
        @"(?<qty>\d+(?:[.,]\d+)?)\s*(?:L|LTRS?|LITERS?)?\s*[xX@*]\s*(?:PHP|P|₱)?\s*(?<rate>\d+(?:[.,]\d+)?)\s*(?:PHP|P|₱)?\s*/\s*(?:L|LTR|LITER)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A peso amount: exactly two decimals ("1,641.40", "95.44", "P95,44"), optional currency
    // prefix, optional minus on either side of it. Three decimals ("96.700", "0.987") are rates
    // and liters, never money.
    private static readonly Regex MoneyToken = new(
        @"(?<![\d.,])(?<neg1>-\s*)?(?:PHP|P|₱)?\s*(?<neg2>-\s*)?(?<num>\d{1,3}(?:,\d{3})+\.\d{2}|\d+[.,]\d{2})(?![\d])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Bare decimals without a currency prefix, for quantities ("31.94", "8.234").
    private static readonly Regex BareDecimal = new(
        @"(?<![\d.,A-Z₱])(?<num>\d{1,3}[.,]\d{2,3})(?![\d])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SpaceInsideNumber = new(@"(?<=\d[,.])\s+(?=\d)", RegexOptions.CultureInvariant);

    // OCR reads 0 as O: "Php0.o0", "PO.00", "1O0.00".
    private static readonly Regex LetterOInNumber = new(
        @"(?<=\d[.,]?)[Oo](?=[\d.,Oo]|\b|$)|(?<=(?:^|[\s:Pp₱]))[Oo](?=[.,]\d)",
        RegexOptions.CultureInvariant);

    /// <summary>Undoes the usual OCR slips inside numbers, leaving words alone.</summary>
    public static string NormalizeNumbers(string text)
    {
        string fixedText = SpaceInsideNumber.Replace(text, string.Empty);
        string previous;
        do
        {
            previous = fixedText;
            fixedText = LetterOInNumber.Replace(fixedText, "0");
        }
        while (fixedText != previous);
        return fixedText;
    }

    private readonly record struct Money(decimal Value, bool Negative);

    private static List<Money> MoneyIn(string line)
    {
        var values = new List<Money>();
        foreach (Match m in MoneyToken.Matches(NormalizeNumbers(line)))
        {
            if (TryParseDecimal(m.Groups["num"].Value, out decimal value))
            {
                values.Add(new Money(value, m.Groups["neg1"].Success || m.Groups["neg2"].Success));
            }
        }
        return values;
    }

    private static HashSet<decimal> UnitPrices(IEnumerable<string> lines)
    {
        var rates = new HashSet<decimal>();
        foreach (string line in lines)
        {
            foreach (Match m in UnitPriceToken.Matches(NormalizeNumbers(line)))
            {
                if (TryParseDecimal(m.Groups["rate"].Value, out decimal rate))
                {
                    rates.Add(decimal.Round(rate, 2));
                }
            }
        }
        return rates;
    }

    // ---------------------------------------------------------------------
    // Total amount
    // ---------------------------------------------------------------------

    /// <summary>How strongly a line reads as the receipt total's label (0 = not a total label).</summary>
    private static int TotalLabelScore(string upper)
    {
        if (Regex.IsMatch(upper, @"SUB\s*-?\s*TOTAL"))
            return 1;
        if (Regex.IsMatch(upper, @"GRAND\s+TOTAL|TOTAL\s+(?:AMOUNT|INVOICE|DUE|SALES?)|AMOUNT\s+(?:DUE|PAYABLE)|NET\s+(?:AMOUNT|TOTAL|SALES?)|SALES?\s+TOTAL|TOTAL\s*\(?\s*INCL"))
            return 4;
        if (Regex.IsMatch(upper, @"(?<![A-Z])T[O0]TAL(?![A-Z])"))
            return 3;
        if (Regex.IsMatch(upper, @"(?<![A-Z])DUE(?![A-Z])|PAYABLE"))
            return 2;
        return 0;
    }

    /// <summary>VAT/discount/payment/quantity lines: their amounts are never the total. "Total
    /// (incl. VAT)" is the exception.</summary>
    private static bool IsNonTotalLine(string upper)
    {
        bool inclusiveTotal = upper.Contains("TOTAL") && upper.Contains("INCL");
        if (!inclusiveTotal && Regex.IsMatch(upper, @"VAT|V\.A\.T"))
            return true;
        return Regex.IsMatch(upper,
            @"CHANGE|DISCOUNT|(?<![A-Z])DISC(?![A-Z])|CASH|TENDER|PAYMENT|PRICE|/\s*L\b|(?<![A-Z])QTY|QUANTITY|LITER|LITRE|(?<![A-Z])LTRS?(?![A-Z])|POINTS|SAVINGS|EXEMPT|ZERO\s*RATED|REBATE");
    }

    /// <summary>A line that is (almost) only an amount - the right-hand column of a receipt.</summary>
    private static bool IsValueOnlyLine(string line)
    {
        string rest = MoneyToken.Replace(NormalizeNumbers(line), string.Empty);
        return rest.Count(char.IsLetter) <= 4;
    }

    public static decimal? TryParseAmount(string fullText, List<string> lines, IReadOnlyList<OcrBlock>? blocks = null) =>
        TryParseAmount(fullText, lines, blocks, out _);

    /// <summary>
    /// The receipt total. <paramref name="uncertain"/> is true when it was only guessed (no total
    /// label matched and the VAT breakdown didn't confirm it), so the driver is asked to check it.
    /// <paramref name="blocks"/> are the OCR lines with boxes (as <paramref name="lines"/>, same
    /// order); without them a label is paired with an amount on its own line or the line under it.
    /// <paramref name="fullText"/> (the lines joined) is the fallback when no line pairing works.
    /// </summary>
    public static decimal? TryParseAmount(string fullText, List<string> lines, IReadOnlyList<OcrBlock>? blocks, out bool uncertain)
    {
        List<OcrBlock> rows = blocks is { Count: > 0 } ? SplitIntoLines(blocks) : lines.Select(l => new OcrBlock(l, 0, 0, 0, 0)).ToList();
        HashSet<decimal> unitPrices = UnitPrices(rows.Select(r => r.Text!));
        bool IsUsable(Money m) => !m.Negative && m.Value > 0 && !unitPrices.Contains(m.Value);

        // Every amount on the receipt, and how many times each is printed (a total usually shows
        // twice: TOTAL and the payment line).
        var allValues = rows.SelectMany(r => MoneyIn(r.Text!)).Where(IsUsable).Select(m => m.Value).ToList();
        var timesPrinted = allValues.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());

        // 1. Label and amount on the same line, or on the same printed row in another block.
        double slope = EstimateSlope(rows);
        var labelled = new List<(decimal Value, int Score)>();
        for (int i = 0; i < rows.Count; i++)
        {
            string upper = rows[i].Text!.ToUpperInvariant();
            int score = TotalLabelScore(upper);
            if (score == 0 || IsNonTotalLine(upper))
            {
                continue;
            }

            var sameLine = MoneyIn(rows[i].Text!).Where(IsUsable).ToList();
            if (sameLine.Count > 0)
            {
                labelled.Add((sameLine[^1].Value, score));
                continue;
            }

            if (RowValue(rows, i, slope, IsUsable) is decimal rowValue)
            {
                labelled.Add((rowValue, score));
            }
            else if (ValueBelow(rows, i, IsUsable) is decimal below)
            {
                // Label and amount split across OCR lines: the amount printed under its label.
                labelled.Add((below, score - 1));
            }
        }

        // Pairing by line found nothing (ML Kit cut the label and amount apart some other way): an
        // amount right after a total label in the receipt's text, flagged for the driver to check.
        if (labelled.Count == 0 && AmountAfterTotalLabel(fullText, IsUsable) is decimal fromText)
        {
            labelled.Add((fromText, 2));
        }

        // 2. BIR receipts print VATable Sales + VAT Amount (12 %), which add up to the total.
        decimal? vatChecked = VatConsistentTotal(allValues);

        var bestLabelled = labelled
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => timesPrinted.GetValueOrDefault(c.Value))
            .ThenByDescending(c => c.Value)
            .FirstOrDefault();

        if (vatChecked is decimal vatTotal)
        {
            uncertain = false;
            return vatTotal;
        }

        if (bestLabelled.Value > 0)
        {
            uncertain = bestLabelled.Score < 3;
            return bestLabelled.Value;
        }

        // 3. Last resort, flagged for the driver to check: an amount printed more than once,
        // else the largest one - never from a VAT/discount/cash/change line.
        var plain = rows
            .Where(r => !IsNonTotalLine(r.Text!.ToUpperInvariant()))
            .SelectMany(r => MoneyIn(r.Text!))
            .Where(m => IsUsable(m) && m.Value >= 10m)
            .Select(m => m.Value)
            .ToList();
        var guess = plain
            .OrderByDescending(v => timesPrinted.GetValueOrDefault(v))
            .ThenByDescending(v => v)
            .Select(v => (decimal?)v)
            .FirstOrDefault();

        uncertain = true;
        return guess;
    }

    /// <summary>The amount printed on the same row as rows[labelIndex], to its right.</summary>
    private static decimal? RowValue(List<OcrBlock> rows, int labelIndex, double slope, Func<Money, bool> isUsable)
    {
        OcrBlock label = rows[labelIndex];
        if (!label.HasBox)
        {
            return null;
        }

        double labelRow = label.CenterY - slope * label.CenterX;
        var match = rows
            .Select((r, j) => (Row: r, Index: j))
            .Where(c => c.Index != labelIndex && c.Row.HasBox && c.Row.CenterX > label.CenterX)
            .Where(c => !IsNonTotalLine(c.Row.Text!.ToUpperInvariant()) && TotalLabelScore(c.Row.Text!.ToUpperInvariant()) == 0)
            .Where(c => IsValueOnlyLine(c.Row.Text!))
            .Select(c => (c.Row, Distance: Math.Abs((c.Row.CenterY - slope * c.Row.CenterX) - labelRow),
                          Tolerance: 0.6 * Math.Max(label.Height, c.Row.Height)))
            .Where(c => c.Distance <= c.Tolerance)
            .OrderBy(c => c.Distance)
            .Select(c => MoneyIn(c.Row.Text!).Where(isUsable).Select(m => (decimal?)m.Value).LastOrDefault())
            .FirstOrDefault(v => v.HasValue);
        return match;
    }

    /// <summary>The amount on the line after rows[labelIndex] when that line is only an amount -
    /// never one from a VAT/discount/cash line or another total label (FUEL-001: "TOTAL" over
    /// "VAT 175.97" is not a total of 175.97). With boxes, the line must sit just under the label.</summary>
    private static decimal? ValueBelow(List<OcrBlock> rows, int labelIndex, Func<Money, bool> isUsable)
    {
        if (labelIndex + 1 >= rows.Count)
        {
            return null;
        }

        OcrBlock label = rows[labelIndex];
        OcrBlock next = rows[labelIndex + 1];
        string upper = next.Text!.ToUpperInvariant();
        if (IsNonTotalLine(upper) || TotalLabelScore(upper) > 0 || !IsValueOnlyLine(next.Text!))
        {
            return null;
        }

        if (label.HasBox && next.HasBox
            && (next.CenterY <= label.CenterY || next.CenterY - label.CenterY > 2.5 * Math.Max(label.Height, next.Height)))
        {
            return null;
        }

        return MoneyIn(next.Text!).Where(isUsable).Select(m => (decimal?)m.Value).LastOrDefault();
    }

    // "TOTAL AMOUNT DUE: P1,641.40" in the joined text. Not after SUB/VAT/DISCOUNT ("VAT TOTAL",
    // "SUB-TOTAL"), and a negative amount never matches.
    private static readonly Regex TotalLabelThenAmount = new(
        @"(?<![A-Z])(?<!(?:SUB|VAT|DISC[A-Z]*)\s*-?\s*)(?:GRAND\s+TOTAL|TOTAL\s+AMOUNT(?:\s+DUE)?|TOTAL\s+DUE|AMOUNT\s+DUE|NET\s+AMOUNT|T[O0]TAL)(?![A-Z])\s*[:=]?\s*(?<amount>(?:PHP|P|₱)?\s*(?:\d{1,3}(?:,\d{3})+\.\d{2}|\d+[.,]\d{2}))(?!\d)",
        RegexOptions.CultureInvariant);

    private static decimal? AmountAfterTotalLabel(string fullText, Func<Money, bool> isUsable)
    {
        if (string.IsNullOrWhiteSpace(fullText))
        {
            return null;
        }

        return TotalLabelThenAmount.Matches(NormalizeNumbers(fullText).ToUpperInvariant())
            .SelectMany(m => MoneyIn(m.Groups["amount"].Value))
            .Where(isUsable)
            .Select(m => (decimal?)m.Value)
            .Max();
    }

    /// <summary>The photo's tilt: the median slope of the longer OCR lines (0 when unknown).</summary>
    private static double EstimateSlope(List<OcrBlock> rows)
    {
        var slopes = rows.Where(r => r.HasBox && r.Slope != 0 && r.Width >= 0.15).Select(r => r.Slope).OrderBy(s => s).ToList();
        return slopes.Count == 0 ? 0 : slopes[slopes.Count / 2];
    }

    /// <summary>A printed amount that equals two others printed as VATable sales + 12 % VAT.</summary>
    private static decimal? VatConsistentTotal(List<decimal> values)
    {
        var distinct = values.Distinct().OrderByDescending(v => v).ToList();
        foreach (decimal total in distinct.Where(v => v >= 10m))
        {
            foreach (decimal vatable in distinct.Where(v => v < total))
            {
                decimal vat = total - vatable;
                if (vat > 0 && distinct.Contains(vat) && Math.Abs(vat - vatable * 0.12m) <= Math.Max(0.02m, vatable * 0.0005m))
                {
                    return total;
                }
            }
        }
        return null;
    }

    // ---------------------------------------------------------------------
    // Liters
    // ---------------------------------------------------------------------

    private static readonly Regex LitersAfterKeyword = new(
        @"(?:QTY|QUANTITY|VOLUME|VOL|LITERS?|LITRES?|LTRS?)\s*[:=]?\s*(\d{1,3}(?:[\.,]\d{1,3})?)(?![\d])",
        RegexOptions.CultureInvariant);

    private static readonly Regex LitersWithUnit = new(
        @"(?<![\d.,])(\d{1,3}(?:[\.,]\d{1,3})?)\s*(?:LITERS?|LITRES?|LTRS?|L)\b",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Liters bought: "0.987L x 96.700P/L", "QTY 25.5", "8.234 L" - or, when the receipt prints the
    /// quantity in a "Qty." column without a unit (Petron), the number that times the price per
    /// liter gives a printed line amount (31.94 x 56.39 = 1,801.10). Keeps up to 3 decimals.
    /// </summary>
    public static decimal? TryParseLiters(string fullText, List<string> lines)
    {
        var normalized = lines.Select(l => NormalizeNumbers(l).ToUpperInvariant()).ToList();

        foreach (string line in normalized)
        {
            foreach (Match m in UnitPriceToken.Matches(line))
            {
                if (TryParseDecimal(m.Groups["qty"].Value, out decimal q) && q > 0m && q <= 200m)
                {
                    return q;
                }
            }
        }

        var explicitLiters = new List<decimal>();
        foreach (string line in normalized)
        {
            foreach (Regex pattern in new[] { LitersAfterKeyword, LitersWithUnit })
            {
                foreach (Match m in pattern.Matches(line))
                {
                    if (TryParseDecimal(m.Groups[1].Value, out decimal q) && q > 0m && q <= 200m)
                    {
                        explicitLiters.Add(q);
                    }
                }
            }
        }

        if (QuantityTimesPrice(normalized) is decimal fromPrice)
        {
            return fromPrice;
        }

        if (explicitLiters.Count > 0)
        {
            return explicitLiters.Max();
        }

        // A quantity and its unit/label split across OCR lines ("QTY" / "25.5", "0.987L" / "x 96.700P/L")
        // only line up in the joined text.
        return LitersInText(NormalizeNumbers(fullText ?? string.Empty).ToUpperInvariant());
    }

    private static decimal? LitersInText(string text)
    {
        foreach (Match m in UnitPriceToken.Matches(text))
        {
            if (TryParseDecimal(m.Groups["qty"].Value, out decimal q) && q > 0m && q <= 200m)
            {
                return q;
            }
        }

        var found = new[] { LitersAfterKeyword, LitersWithUnit }
            .SelectMany(p => p.Matches(text).Cast<Match>())
            .Select(m => TryParseDecimal(m.Groups[1].Value, out decimal q) ? q : 0m)
            .Where(q => q > 0m && q <= 200m)
            .ToList();
        return found.Count > 0 ? found.Max() : null;
    }

    /// <summary>q x price = amount, with q a bare decimal and price/amount printed amounts.</summary>
    private static decimal? QuantityTimesPrice(List<string> normalizedLines)
    {
        var amounts = normalizedLines.SelectMany(MoneyIn).Where(m => !m.Negative && m.Value > 0).Select(m => m.Value).Distinct().ToList();
        var quantities = normalizedLines
            .SelectMany(l => BareDecimal.Matches(l).Cast<Match>())
            .Select(m => TryParseDecimal(m.Groups["num"].Value, out decimal q) ? q : 0m)
            .Where(q => q >= 0.1m && q <= 200m)
            .Distinct()
            .ToList();

        foreach (decimal q in quantities)
        {
            foreach (decimal price in amounts.Where(p => p >= 20m && p <= 200m && p != q))
            {
                decimal product = q * price;
                if (amounts.Any(a => a >= 10m && a != price && Math.Abs(product - a) <= Math.Max(0.02m, a * 0.0005m)))
                {
                    return q;
                }
            }
        }
        return null;
    }

    // ---------------------------------------------------------------------
    // Date
    // ---------------------------------------------------------------------

    /// <summary>The receipt's date, or null when none is printed or none is plausible (see
    /// <see cref="IsPlausibleReceiptDate"/>).</summary>
    public static DateTime? TryParseReceiptDate(string fullText, List<string> lines)
    {
        var now = DateTime.Now.Date;
        var normalizedLines = lines.Select(NormalizeNumbers).ToList();

        var keywordDates = new List<DateTime>();
        foreach (var line in normalizedLines)
        {
            var upperLine = line.ToUpperInvariant();
            if (upperLine.Contains("DATE") || upperLine.Contains("TRANS") || upperLine.Contains("INVOICE"))
            {
                keywordDates.AddRange(ExtractDatesFromText(line));
            }
        }

        if (ClosestPlausibleDate(keywordDates, now) is DateTime bestKeywordDate)
        {
            return bestKeywordDate;
        }

        // Any date on a line, else one split across OCR lines ("OCT 24," / "2026") in the joined text -
        // as printed too, since NormalizeNumbers joins "24, 2026" into one number.
        string joined = fullText ?? string.Empty;
        return ClosestPlausibleDate(normalizedLines.SelectMany(ExtractDatesFromText), now)
               ?? ClosestPlausibleDate(ExtractDatesFromText(joined).Concat(ExtractDatesFromText(NormalizeNumbers(joined))), now);
    }

    private static DateTime? ClosestPlausibleDate(IEnumerable<DateTime> dates, DateTime now) =>
        dates
            .Where(IsPlausibleReceiptDate)
            .OrderBy(d => Math.Abs((d.Date - now).TotalDays))
            .Select(d => (DateTime?)d.Date)
            .FirstOrDefault();

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
            "M-d-yyyy", "MM-dd-yyyy", "M-d-yy", "MM-dd-yy",
            "MMM d yyyy", "MMM d, yyyy", "MMMM d yyyy", "MMMM d, yyyy",
            "MMM dd yyyy", "MMM dd, yyyy", "MMMM dd yyyy", "MMMM dd, yyyy",
            "d MMM yyyy", "dd MMM yyyy", "d-MMM-yyyy", "dd-MMM-yyyy", "dd-MMM-yy"
        };

        return DateTime.TryParseExact(token.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date)
               || DateTime.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static readonly string[] DatePatterns =
    {
        @"\b\d{4}[-/]\d{1,2}[-/]\d{1,2}\b",
        @"\b\d{1,2}[-/]\d{1,2}[-/]\d{2,4}\b",
        @"\b(?:JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|SEPT|OCT|NOV|DEC)[A-Z]*\.?\s+\d{1,2},?\s+\d{2,4}\b",
        @"\b\d{1,2}[\s-](?:JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)[A-Z]*[\s-]\d{2,4}\b",
    };

    private static IEnumerable<DateTime> ExtractDatesFromText(string text)
    {
        foreach (var pattern in DatePatterns)
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

    /// <summary>FUEL-012: from 2020, not more than a day ahead, at most three years old.</summary>
    public static bool IsPlausibleReceiptDate(DateTime date)
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

    public static string BuildParsingWarning(string? vendor, decimal? amount, decimal? liters, DateTime? date, bool amountUncertain = false)
    {
        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(vendor) || vendor.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
            issues.Add("Station not recognized as Petron or Shell.");

        if (!amount.HasValue || amount.Value <= 0 || amountUncertain)
            issues.Add("Total amount was not confidently detected.");

        if (!liters.HasValue || liters.Value <= 0)
            issues.Add("Liters/quantity was not confidently detected.");

        if (!date.HasValue)
            issues.Add("Receipt date was not confidently detected.");

        return issues.Count == 0 ? string.Empty : "OCR uncertain: " + string.Join(" ", issues) + " Please review/edit before submit.";
    }

    // ---------------------------------------------------------------------
    // Lines
    // ---------------------------------------------------------------------

    /// <summary>One entry per text line. A block with several lines is cut into equal-height
    /// slices of its box (used when the OCR only returned blocks).</summary>
    public static List<OcrBlock> SplitIntoLines(IReadOnlyList<OcrBlock> blocks)
    {
        var lines = new List<OcrBlock>();
        foreach (OcrBlock block in blocks)
        {
            string[] parts = (block.Text ?? string.Empty).Split('\n');
            int count = parts.Length;
            for (int i = 0; i < count; i++)
            {
                string text = parts[i].Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                lines.Add(count == 1
                    ? block with { Text = text }
                    : new OcrBlock(text, block.X, block.Y + block.Height * i / count, block.Width, block.Height / count, block.Slope));
            }
        }
        return lines;
    }
}

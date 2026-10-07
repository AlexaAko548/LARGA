using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Pulls the OR (Official Receipt) number out of the OCR'd text of a fuel receipt. The other
/// fuel receipt fields (station, amount, liters, date) are still parsed in ScanFuelReceiptPage;
/// this lives here so it's plain C# and testable without an image, like GcashReceiptParser.
///
/// BIR-compliant PH receipts print the number under a few labels:
///   OR No.: 0012345        O.R. # 0012345        OFFICIAL RECEIPT No 0012345
///   SI No. 0045678         SALES INVOICE 0045678 Invoice No: 45678    Trans # 1234
/// An OR/Official Receipt label is trusted; the invoice/transaction labels are a fallback and
/// come back flagged uncertain so the driver is asked to check them. The same receipts also
/// print TIN, MIN, Serial No. and PTU/Accreditation numbers - none of those labels match, and
/// a TIN-shaped value (000-123-456-000) is rejected outright in case OCR garbles a label.
/// </summary>
public static class FuelReceiptParser
{
    // "OR", "O.R.", "0R" (OCR reads O as zero) followed by No/Number/#, or "Official Receipt".
    private static readonly Regex OrLabelPattern = new(
        @"(?<![A-Z0-9])(?:[O0]\s?\.?\s?R\s?\.?\s*(?:NO|NUMBER|NUM|#)|OFFICIAL\s+RECEIPT(?:\s*(?:NO|NUMBER|NUM|#))?)\s*\.?\s*[:#]?\s*",
        RegexOptions.Compiled);

    private static readonly Regex FallbackLabelPattern = new(
        @"(?<![A-Z0-9])(?:S\s?\.?\s?I\s?\.?\s*(?:NO|NUMBER|NUM|#)|SALES\s+INVOICE(?:\s*(?:NO|NUMBER|NUM|#))?|INVOICE\s*(?:NO|NUMBER|NUM|#)|RECEIPT\s*(?:NO|NUMBER|NUM|#)|TRANS(?:ACTION)?\s*\.?\s*(?:NO|NUMBER|NUM|#)|TXN\s*(?:NO|#))\s*\.?\s*[:#]?\s*",
        RegexOptions.Compiled);

    // Atomic group so "12345.67" (a money amount) fails outright instead of backtracking to
    // "1234"; the value can't run on into a decimal part.
    private static readonly Regex ValuePattern = new(
        @"^(?>[A-Z0-9][A-Z0-9\-]{2,24})(?![.,]?\d)",
        RegexOptions.Compiled);

    private static readonly Regex TinPattern = new(@"^\d{3}-\d{3}-\d{3}(?:-\d{3,5})?$", RegexOptions.Compiled);

    /// <summary>
    /// Returns the OR number, or null when none was found. <paramref name="uncertain"/> is
    /// false only when it came from an OR/Official Receipt label.
    /// </summary>
    /// <param name="ocrBlocks">OCR text blocks; a block may span several lines.</param>
    public static string? TryParseOrNumber(IEnumerable<string> ocrBlocks, out bool uncertain)
    {
        var lines = ocrBlocks
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .SelectMany(b => b.Split('\n'))
            .Select(l => l.Trim().ToUpperInvariant())
            .Where(l => l.Length > 0)
            .ToList();

        string? value = FindLabelledValue(lines, OrLabelPattern);
        if (value != null)
        {
            uncertain = false;
            return value;
        }

        value = FindLabelledValue(lines, FallbackLabelPattern);
        uncertain = true;
        return value;
    }

    private static string? FindLabelledValue(List<string> lines, Regex labelPattern)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            foreach (Match label in labelPattern.Matches(lines[i]))
            {
                string rest = lines[i][(label.Index + label.Length)..];

                // The number is often printed on the line under its label.
                if (string.IsNullOrWhiteSpace(rest) && i + 1 < lines.Count)
                {
                    rest = lines[i + 1];
                }

                string? value = ExtractValue(rest);
                if (value != null)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? ExtractValue(string text)
    {
        Match match = ValuePattern.Match(text.TrimStart(' ', ':', '#', '.'));
        if (!match.Success)
        {
            return null;
        }

        string value = FixOcrDigits(match.Value.Trim('-'));

        if (value.Count(char.IsDigit) < 3 || TinPattern.IsMatch(value))
        {
            return null;
        }

        return value;
    }

    /// <summary>
    /// OCR confuses O/0 and I/L/1 inside receipt numbers. Only swaps letters sitting next to
    /// digits (or a leading O before digits), so a real letter prefix like "SI-" survives.
    /// </summary>
    private static string FixOcrDigits(string value)
    {
        string previous;
        do
        {
            previous = value;
            value = Regex.Replace(value, @"(?<=\d)[OQ](?=[\dOQ])|(?<=[\dOQ])[OQ](?=\d)|^[OQ](?=[\dOQ]{2})", "0");
            value = Regex.Replace(value, @"(?<=\d)[IL](?=\d)", "1");
        }
        while (value != previous);

        return value;
    }
}

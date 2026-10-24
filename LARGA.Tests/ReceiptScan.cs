using System.Globalization;
using System.Text.RegularExpressions;
using LARGA.SharedCore.Services;
using static LARGA.SharedCore.Services.FuelReceiptTextParser;

namespace LARGA.Tests;

/// <summary>
/// What ScanFuelReceiptPage.CaptureOrConfirmAsync does with the OCR blocks, step for step: block
/// texts become the "lines", the full text is those joined by spaces, then each parser runs. The
/// display strings use the page's own formats ("0.00" for the amount, "0.##" for liters).
/// </summary>
public sealed record ReceiptScan(
    string? Vendor, decimal? Amount, decimal? Liters, DateTime? Date,
    string? OrNumber, bool OrUncertain, string Warning)
{
    public string AmountShown => Amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "--";
    public string LitersShown => Liters?.ToString("0.##", CultureInfo.InvariantCulture) ?? "--";

    public static ReceiptScan Run(IReadOnlyList<OcrBlock> blocks)
    {
        var lines = blocks
            .Select(b => b.Text?.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList();
        string fullText = string.Join(" ", lines);

        var vendor = TryParseVendor(fullText);
        var amount = TryParseAmount(fullText, lines, blocks);
        var liters = TryParseLiters(fullText, lines);
        var date = TryParseReceiptDate(fullText, lines);
        var or = FuelReceiptParser.TryParseOrNumber(lines, out bool orUncertain);
        return new ReceiptScan(vendor, amount, liters, date, or, orUncertain,
            BuildParsingWarning(vendor, amount, liters, date));
    }

    /// <summary>One block per line, no bounding boxes (layout b).</summary>
    public static ReceiptScan FromLines(params string[] lines) =>
        Run(lines.Select(l => new OcrBlock(l, 0, 0, 0, 0)).ToList());

    private static readonly Regex LogLine = new(
        @"LARGA-OCR block \d+ .*norm=\[x(?<x>-?[\d.]+) y(?<y>-?[\d.]+) w(?<w>-?[\d.]+) h(?<h>-?[\d.]+)\] text=""(?<t>.*)""\s*$");

    /// <summary>The exact blocks ML Kit returned on the emulator (layout a), from the LARGA-OCR logcat lines.</summary>
    public static IReadOnlyList<OcrBlock> FromLogcat(string fixture)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        var blocks = new List<OcrBlock>();
        foreach (string line in File.ReadLines(path))
        {
            Match m = LogLine.Match(line);
            if (!m.Success)
            {
                continue;
            }

            double D(string g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
            blocks.Add(new OcrBlock(m.Groups["t"].Value.Replace("\\n", "\n"), D("x"), D("y"), D("w"), D("h")));
        }

        Assert.NotEmpty(blocks);
        return blocks;
    }
}

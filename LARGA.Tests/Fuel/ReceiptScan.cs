using System.Globalization;
using System.Text.RegularExpressions;
using LARGA.SharedCore.Services;
using static LARGA.SharedCore.Services.FuelReceiptTextParser;

namespace LARGA.Tests.Fuel;

/// <summary>
/// What ScanFuelReceiptPage does with one OCR read (FuelReceiptTextParser.Parse). The display
/// strings use the page's own formats ("0.00" for the amount, "0.###" for liters).
/// </summary>
public sealed record ReceiptScan(
    string? Vendor, decimal? Amount, bool AmountUncertain, decimal? Liters, DateTime? Date,
    string? OrNumber, bool OrUncertain, string Warning)
{
    public string AmountShown => Amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "--";
    public string LitersShown => Liters?.ToString("0.###", CultureInfo.InvariantCulture) ?? "--";

    public static ReceiptScan Run(IReadOnlyList<OcrBlock> blocks)
    {
        ReceiptFields f = Parse(blocks);
        return new ReceiptScan(f.Vendor, f.Amount, f.AmountUncertain, f.Liters, f.Date, f.OrNumber, f.OrNumberUncertain, f.Warning);
    }

    /// <summary>One block per line, no bounding boxes (layout b).</summary>
    public static ReceiptScan FromLines(params string[] lines) =>
        Run(lines.Select(l => new OcrBlock(l, 0, 0, 0, 0)).ToList());

    private static readonly Regex LogLine = new(
        @"LARGA-OCR (?<kind>block|line) \d+ .*norm=\[x(?<x>-?[\d.]+) y(?<y>-?[\d.]+) w(?<w>-?[\d.]+) h(?<h>-?[\d.]+)\](?: slope=(?<s>-?[\d.]+))? text=""(?<t>.*)""\s*$");

    /// <summary>What ML Kit returned on the emulator (layout a), from the LARGA-OCR logcat output:
    /// its line boxes when the capture has them ("LARGA-OCR line"), otherwise its blocks.</summary>
    public static IReadOnlyList<OcrBlock> FromLogcat(string fixture)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fuel", fixture);
        string kind = File.ReadLines(path).Any(l => l.Contains("LARGA-OCR line ")) ? "line" : "block";

        var blocks = new List<OcrBlock>();
        foreach (string line in File.ReadLines(path))
        {
            Match m = LogLine.Match(line);
            if (!m.Success || m.Groups["kind"].Value != kind)
            {
                continue;
            }

            double D(string g) => m.Groups[g].Success ? double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
            blocks.Add(new OcrBlock(m.Groups["t"].Value.Replace("\\n", "\n"), D("x"), D("y"), D("w"), D("h"), D("s")));
        }

        Assert.NotEmpty(blocks);
        return blocks;
    }

    /// <summary>The block-level capture only (as before ML Kit lines were logged).</summary>
    public static IReadOnlyList<OcrBlock> BlocksFromLogcat(string fixture)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fuel", fixture);
        var blocks = new List<OcrBlock>();
        foreach (string line in File.ReadLines(path))
        {
            Match m = LogLine.Match(line);
            if (m.Success && m.Groups["kind"].Value == "block")
            {
                double D(string g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
                blocks.Add(new OcrBlock(m.Groups["t"].Value.Replace("\\n", "\n"), D("x"), D("y"), D("w"), D("h")));
            }
        }

        Assert.NotEmpty(blocks);
        return blocks;
    }
}

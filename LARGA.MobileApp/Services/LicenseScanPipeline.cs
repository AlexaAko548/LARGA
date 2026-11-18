using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LARGA.SharedCore.Services;
using Microsoft.Maui.Graphics;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Reads a driver's license photo with ML Kit in as many passes as it takes (Scan Driver
/// License page, and the DEBUG OcrBatchReceiver for testing):
///   1. the photo as taken - a clear photo of a clean card stops here;
///   2. glare-filtered whole card (OcrGlareFilter.ColorMax: the shiny seal's colours removed,
///      glare and shadows evened out);
///   3-4. grayscale, flattened, plain and sharpened (faded or slightly blurry cards);
///   5-8. the License No. / Expiration row only, cropped and enlarged, with each filter and a
///      wider lighting blur - the row the seal sits on. Small images, so these are quick, and
///      each one is another chance at the digits glare left faint;
///   9. the receipt scanner's contrast-stretched copy.
/// Every pass is parsed and the fields voted on (LtoLicenseParser.ParseBest). It stops as soon
/// as everything is read with no doubts, or after <see cref="TimeBudget"/>.
/// </summary>
public static class LicenseScanPipeline
{
    public static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(15);

    /// <param name="Texts">This photo's passes, to carry into a retake (see RunAsync).</param>
    public sealed record Outcome(LtoLicenseParser.Result Result, IReadOnlyList<string> Texts, int Passes);

    // A license number, possibly cut short by glare ("GO1-25-0937"), to find its row.
    private static readonly Regex PartialNumber = new(@"[A-Z][0-9OoIl]{2}\s*-?\s*\d{2}\s*-", RegexOptions.Compiled);
    private static readonly Regex ExpiryLabel = new(@"Expir", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LicenseLabel = new(@"Li?cen[sc]e\s*N", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private enum Pass { ColorMax, Gray, GraySharp, RowColorMax, RowGray, RowGraySharp, RowColorMaxWide, Contrast }

    /// <param name="earlierTexts">Passes over an earlier photo of the same card (a retake after
    /// glare hid the number): voted on together with this photo's, after them. Glare moves when
    /// the card is tilted, so two photos together often read what neither did alone.</param>
    public static async Task<Outcome> RunAsync(IOcrService ocr, string photoPath, IReadOnlyList<string>? earlierTexts = null)
    {
        var watch = Stopwatch.StartNew();
        var texts = new List<string>();
        var allBlocks = new List<List<OcrTextBlock>>();
        IReadOnlyList<string> earlier = earlierTexts ?? Array.Empty<string>();

        LtoLicenseParser.Result Parse() => LtoLicenseParser.ParseBest(texts.Concat(earlier));

        List<OcrTextBlock> first = await ocr.ExtractTextBlocksAsync(photoPath);
        allBlocks.Add(first);
        texts.Add(ToText(first));
        LtoLicenseParser.Result result = Parse();
        Log(photoPath, "original", texts[^1], result);

        foreach (Pass pass in Enum.GetValues<Pass>())
        {
            if (result.IsConfident || watch.Elapsed > TimeBudget)
            {
                break;
            }

            Rect? row = null;
            if (pass is Pass.RowColorMax or Pass.RowGray or Pass.RowGraySharp or Pass.RowColorMaxWide)
            {
                row = allBlocks.Select(FindNumberRow).FirstOrDefault(r => r is not null);
                if (row is null)
                {
                    continue;
                }
            }

            string? copy = await Task.Run(() => pass switch
            {
                Pass.ColorMax => OcrImageEnhancer.CreateLicensePass(photoPath, OcrGlareFilter.Mode.ColorMax),
                Pass.Gray => OcrImageEnhancer.CreateLicensePass(photoPath, OcrGlareFilter.Mode.Luminance),
                Pass.GraySharp => OcrImageEnhancer.CreateLicensePass(photoPath, OcrGlareFilter.Mode.Luminance, sharpen: 0.8),
                Pass.RowColorMax => OcrImageEnhancer.CreateLicensePass(photoPath, OcrGlareFilter.Mode.ColorMax, crop: row),
                Pass.RowGray => OcrImageEnhancer.CreateLicensePass(photoPath, OcrGlareFilter.Mode.Luminance, crop: row),
                Pass.RowGraySharp => OcrImageEnhancer.CreateLicensePass(photoPath, OcrGlareFilter.Mode.Luminance, sharpen: 0.8, crop: row),
                Pass.RowColorMaxWide => OcrImageEnhancer.CreateLicensePass(photoPath, OcrGlareFilter.Mode.ColorMax, crop: row, backgroundFraction: 1.0 / 5),
                _ => OcrImageEnhancer.CreateEnhancedCopy(photoPath),
            });
            if (copy is null)
            {
                continue;
            }

            try
            {
                List<OcrTextBlock> blocks = await ocr.ExtractTextBlocksAsync(copy);
                // Row crops have their own coordinates - only whole-card passes can locate the row.
                if (row is null)
                {
                    allBlocks.Add(blocks);
                }
                texts.Add(ToText(blocks));
                result = Parse();
                Log(photoPath, pass.ToString(), texts[^1], result);
            }
            finally
            {
                TryDelete(copy);
            }
        }

        return new Outcome(result, texts, texts.Count);
    }

    /// <summary>ML Kit's lines in its reading order, one per line - the layout LtoLicenseParser
    /// expects (a label row, then its value row).</summary>
    public static string ToText(IEnumerable<OcrTextBlock> blocks) =>
        string.Join("\n", blocks.SelectMany(b => b.Lines.Count > 0
            ? b.Lines.Select(l => l.Text)
            : b.Text.Split('\n')));

    /// <summary>Where the License No. value is, normalized to the upright photo: around the
    /// (maybe partial) number itself, else left of the Expiration Date label, else under the
    /// License No. label. Null if none of them was read.</summary>
    public static Rect? FindNumberRow(IEnumerable<OcrTextBlock> blocks)
    {
        var lines = blocks
            .SelectMany(b => b.Lines.Count > 0
                ? b.Lines.Select(l => (l.Text, Box: l.BoundingBox))
                : new[] { (b.Text, Box: b.BoundingBox) })
            .Where(l => l.Box.Width > 0 && l.Box.Height > 0)
            .ToList();

        foreach (var (text, box) in lines)
        {
            if (PartialNumber.IsMatch(text))
            {
                return Clamp(box.X - 0.03, box.Y - 0.8 * box.Height, box.X + box.Width * 1.7, box.Bottom + 0.8 * box.Height);
            }
        }

        foreach (var (text, box) in lines)
        {
            if (ExpiryLabel.IsMatch(text))
            {
                return Clamp(box.X - 2.0 * box.Width, box.Y - 0.3 * box.Height, box.Right + 0.2 * box.Width, box.Bottom + 2.6 * box.Height);
            }
        }

        foreach (var (text, box) in lines)
        {
            if (LicenseLabel.IsMatch(text))
            {
                return Clamp(box.X - 0.5 * box.Width, box.Y - 0.3 * box.Height, box.X + 3.5 * box.Width, box.Bottom + 2.6 * box.Height);
            }
        }

        return null;
    }

    private static Rect Clamp(double left, double top, double right, double bottom)
    {
        left = Math.Clamp(left, 0, 1);
        top = Math.Clamp(top, 0, 1);
        right = Math.Clamp(right, 0, 1);
        bottom = Math.Clamp(bottom, 0, 1);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Couldn't delete OCR copy '{path}': {ex.Message}");
        }
    }

    [Conditional("DEBUG")]
    private static void Log(string photoPath, string pass, string text, LtoLicenseParser.Result r) =>
        Debug.WriteLine(
            $"LARGA-LIC file={Path.GetFileName(photoPath)} pass={pass} " +
            $"read=\"{text.Replace("\n", " | ")}\" => number={r.LicenseNumber ?? "-"}{(r.LicenseNumberUncertain ? "?" : "")} " +
            $"expiry={r.ExpiryDate:yyyy-MM-dd}{(r.ExpiryUncertain ? "?" : "")} name={r.FullName ?? "-"} " +
            $"dl={r.DlCodes ?? "-"} sex={r.Sex ?? "-"} agency={r.AgencyCode ?? "-"} confident={r.IsConfident}");
}

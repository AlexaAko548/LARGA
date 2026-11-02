using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SkiaSharp;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Classical (non-ML) seven-segment LCD digit reader - decodes a cropped region containing
/// N digits (e.g. a car odometer readout) by thresholding, splitting into per-digit cells,
/// and sampling each of the 7 standard segment zones per cell.
///
/// Exists because general OCR (e.g. ML Kit) is trained
/// on normal printed/handwritten glyphs and is a known weak case for seven-segment displays -
/// their strokes are disconnected line segments that don't match that training distribution,
/// so detection is unreliable exactly where this app needs it most (odometer readouts).
/// Seven-segment digits are geometrically well-defined (a fixed on/off combination of up to 7
/// strokes per digit), so a deterministic decode - no training, no model, no ongoing cost -
/// is both possible and, once the crop is roughly right, considerably more reliable than
/// asking a general OCR model to recognize this specific font.
///
/// Real readouts framed from a dashboard photo (LARGA.Tests/Fixtures/Odometer, Toyota Vios
/// clusters) are small, italic and have "km" printed under them, so the frame is scaled up,
/// ink touching the frame is dropped, only the readout's own rows are kept and the slant is
/// taken out before the digits are split. ML Kit's typical slip on these displays is reading a
/// 6 as a 5 (one segment apart), which is what this decoder is there to catch
/// (OdometerReadingParser.Reconcile). IsConfident flags a digit whose segment scores landed in
/// the gray zone, so the caller asks the driver to check rather than trusting a guess.
/// </summary>
public static class SevenSegmentDecoder
{
    public class DigitResult
    {
        /// <summary>'?' when no segment pattern matched any known digit at all.</summary>
        public char Digit { get; set; }

        /// <summary>False when this digit's decode is a coin-flip - at least one segment's
        /// on/off score landed within the ambiguous band around the threshold rather than
        /// clearly on one side of it.</summary>
        public bool IsConfident { get; set; }
    }

    public class DecodeResult
    {
        public List<DigitResult> Digits { get; set; } = new();
        public string Text => new string(Digits.Select(d => d.Digit).ToArray());
        public bool AllConfident => Digits.Count > 0 && Digits.All(d => d.IsConfident);
    }

    // Segment score bands: a fraction of "lit" pixels in a segment's sample zone below
    // OffBand reads as confidently off, above OnBand reads as confidently on. Anything
    // between the two is the ambiguous middle ground validated against real photo noise -
    // this is exactly where the two misreads during testing came from, so those digits are
    // marked unconfident rather than guessed.
    private const double OffBand = 0.45;
    private const double OnBand = 0.65;
    private const double DecisionThreshold = 0.50;

    // A lit green LCD scores well above this on green-minus-other-channels; white/grey digits
    // (drum odometers, white or amber backlit LCDs) only reach it through JPEG noise.
    private const int MinColorScore = 80;
    // Below this luminance spread the frame is a flat patch with nothing to read.
    private const int MinLuminanceContrast = 40;

    // A readout framed from an ordinary dashboard photo is often only 15-40 px tall; the segment
    // zones need a few pixels each, so a smaller frame is scaled up first.
    private const int MinWorkingHeight = 160;
    // Italic LCD fonts lean right by up to ~20 degrees (a shear of ~0.35).
    private const double MaxShear = 0.4;

    /// <param name="maxDigits">The most digits the readout can have (a taxi odometer shows 5 or
    /// 6). A frame that splits into far more cells than that holds texture, not a readout.</param>
    public static DecodeResult Decode(SKBitmap crop, int maxDigits)
    {
        using SKBitmap? upscaled = crop.Height < MinWorkingHeight ? Upscale(crop) : null;
        bool[,]? ink = Threshold(upscaled ?? crop);
        if (ink == null)
        {
            return new DecodeResult();
        }

        // Ink touching the frame is the bezel, glare or a label cut by the frame - never a digit
        // the driver framed. It used to come back as an extra '1' at the end of the reading.
        ClearBorder(ink);

        if (DigitBand(ink) is not (int bandTop, int bandBottom))
        {
            return new DecodeResult();
        }

        // Only the readout's own rows ("km" under it is left out), with the italic slant removed.
        bool[,] on = Deskew(ink, bandTop, bandBottom);
        int w = on.GetLength(0);
        int inkTop = 0, inkBottom = on.GetLength(1) - 1;
        int inkHeight = inkBottom - inkTop + 1;

        var colCounts = new int[w];
        for (int x = 0; x < w; x++)
        {
            int count = 0;
            for (int y = inkTop; y <= inkBottom; y++) if (on[x, y]) count++;
            colCounts[x] = count;
        }

        // Tier 1: split on (near-)empty columns (real gaps between digits). A column needs a
        // real share of the ink band to count, so isolated specks don't open runs of their own.
        int minColInk = Math.Max(1, inkHeight / 8);
        var runs = new List<(int start, int end)>();
        int runStart = -1;
        for (int x = 0; x < w; x++)
        {
            bool colOn = colCounts[x] >= minColInk;
            if (colOn && runStart < 0) runStart = x;
            else if (!colOn && runStart >= 0) { runs.Add((runStart, x - 1)); runStart = -1; }
        }
        if (runStart >= 0) runs.Add((runStart, w - 1));

        // A digit (even a '1') is tall - a run whose ink covers little of the band is a speck,
        // glare or a dial mark at the frame's edge.
        runs.RemoveAll(r => RunInkHeight(on, r.start, r.end, inkTop, inkBottom) < inkHeight * 0.5);

        if (runs.Count == 0)
        {
            return new DecodeResult();
        }

        double unitWidth = DigitWidth(runs, inkHeight);

        // Tier 2: a run much wider than one digit is multiple digits touching at the baseline
        // with no fully-empty column between them (common - digit spacing on a real LCD is
        // tight) - split it further at local minima (valleys) in the column density instead of
        // requiring a true gap.
        var finalRuns = new List<(int start, int end)>();
        foreach (var r in runs)
        {
            int runWidth = r.end - r.start + 1;
            int subDigits = (int)Math.Round(runWidth / unitWidth);
            if (subDigits <= 1) { finalRuns.Add(r); continue; }

            var boundaries = new List<int>();
            for (int k = 1; k < subDigits; k++)
            {
                int expected = r.start + (int)Math.Round(k * (double)runWidth / subDigits);
                int searchRadius = Math.Max(1, (int)(unitWidth / 3));
                int bestX = expected, bestCount = int.MaxValue;
                for (int x = Math.Max(r.start, expected - searchRadius); x <= Math.Min(r.end, expected + searchRadius); x++)
                {
                    if (colCounts[x] < bestCount) { bestCount = colCounts[x]; bestX = x; }
                }
                boundaries.Add(bestX);
            }

            int segStart = r.start;
            foreach (int b in boundaries) { finalRuns.Add((segStart, b)); segStart = b + 1; }
            finalRuns.Add((segStart, r.end));
        }

        // Far more cells than an odometer has digits means the frame holds texture (a dial
        // face, printed text, a screen's pixel grid), not a seven-segment readout - report
        // nothing rather than a wall of guessed digits.
        if (finalRuns.Count > maxDigits + 2)
        {
            return new DecodeResult();
        }

        var result = new DecodeResult();
        for (int i = 0; i < finalRuns.Count; i++)
        {
            var (start, end) = finalRuns[i];
            int cellWidth = end - start + 1;

            // '1' is drawn much narrower than every other digit on a real seven-segment
            // display (only the two right-hand vertical strokes are needed) - percentage-
            // based zone sampling, tuned for a full-width digit, breaks down on it (too few
            // pixels per zone to be reliable). Width alone is a reliable enough signal here.
            if (cellWidth < unitWidth * 0.55)
            {
                result.Digits.Add(new DigitResult { Digit = '1', IsConfident = true });
                continue;
            }

            // A cell whose edge came from a tier-2 valley split (touching digits, not a true
            // gap) has an estimated boundary, not an exact one - inset a couple pixels on
            // that side so a sliver of the neighboring digit's stroke doesn't bleed into this
            // cell's edge segment zones.
            bool leftIsValleySplit = i > 0 && start == finalRuns[i - 1].end + 1;
            bool rightIsValleySplit = i < finalRuns.Count - 1 && end + 1 == finalRuns[i + 1].start;
            int insetLeft = leftIsValleySplit ? 2 : 0;
            int insetRight = rightIsValleySplit ? 2 : 0;

            result.Digits.Add(DecodeDigit(on, start + insetLeft, end - insetRight, inkTop, inkBottom));
        }

        return result;
    }

    private static SKBitmap Upscale(SKBitmap crop)
    {
        int factor = Math.Min(8, (int)Math.Ceiling((double)MinWorkingHeight / Math.Max(1, crop.Height)));
        var info = new SKImageInfo(crop.Width * factor, crop.Height * factor, SKColorType.Rgba8888, SKAlphaType.Premul);
        return crop.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) ?? crop.Copy();
    }

    /// <summary>Lit (digit) pixels, or null when the frame is a flat patch with nothing to read.</summary>
    private static bool[,]? Threshold(SKBitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var on = new bool[w, h];
        var score = new int[w, h];
        var luminance = new int[w, h];
        int maxScore = 0, minLum = 255, maxLum = 0;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                SKColor c = bitmap.GetPixel(x, y);
                // LCD segments are lit in a strong, saturated color (green on this app's
                // reference odometers) against a near-black background - green-minus-other-
                // channels separates lit segments from background/glare far more cleanly
                // than plain grayscale luminance would.
                int s = Math.Max(0, c.Green * 2 - c.Red - c.Blue);
                score[x, y] = s;
                if (s > maxScore) maxScore = s;

                int l = (299 * c.Red + 587 * c.Green + 114 * c.Blue) / 1000;
                luminance[x, y] = l;
                if (l < minLum) minLum = l;
                if (l > maxLum) maxLum = l;
            }
        }

        // Anything that isn't a lit green display is read by brightness instead. With the
        // color score alone, white digits score ~0 everywhere, the threshold collapses onto
        // JPEG noise, and every speck became its own narrow cell decoded as '1'.
        bool useColor = maxScore >= MinColorScore;
        if (!useColor && maxLum - minLum < MinLuminanceContrast)
        {
            return null;
        }

        int[,] signal = useColor ? score : luminance;
        int threshold = OtsuThreshold(signal, w, h, useColor ? 510 : 255);
        int onTotal = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                on[x, y] = signal[x, y] > threshold;
                if (on[x, y]) onTotal++;
            }

        // Digits are the minority of a tight frame - when "bright" is the majority, the digits
        // are the dark ones (dark numerals on a light face).
        if (!useColor && onTotal > w * h / 2)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    on[x, y] = !on[x, y];
        }

        return on;
    }

    /// <summary>Clears every patch of ink connected to the frame's edge.</summary>
    private static void ClearBorder(bool[,] on)
    {
        int w = on.GetLength(0), h = on.GetLength(1);
        var stack = new Stack<(int X, int Y)>();
        for (int x = 0; x < w; x++) { stack.Push((x, 0)); stack.Push((x, h - 1)); }
        for (int y = 0; y < h; y++) { stack.Push((0, y)); stack.Push((w - 1, y)); }

        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            if (x < 0 || y < 0 || x >= w || y >= h || !on[x, y]) continue;
            on[x, y] = false;
            stack.Push((x + 1, y));
            stack.Push((x - 1, y));
            stack.Push((x, y + 1));
            stack.Push((x, y - 1));
        }
    }

    /// <summary>The readout's rows: the tallest band of inked rows. A gap of a few rows (between
    /// a digit's upper and lower segments) doesn't split a band; the "km" printed under the
    /// readout, or a clock above it, is a band of its own.</summary>
    private static (int Top, int Bottom)? DigitBand(bool[,] on)
    {
        int w = on.GetLength(0), h = on.GetLength(1);
        var rowCounts = new int[h];
        for (int y = 0; y < h; y++)
        {
            int count = 0;
            for (int x = 0; x < w; x++) if (on[x, y]) count++;
            rowCounts[y] = count;
        }

        int maxRow = rowCounts.Max();
        if (maxRow == 0)
        {
            return null;
        }

        // Rows with only a stray pixel or two are noise, not the top/bottom of a digit.
        int minRowInk = Math.Max(Math.Max(1, w / 60), maxRow / 8);
        int maxGap = Math.Max(2, h / 25);

        (int Top, int Bottom)? best = null;
        int top = -1, last = -1;
        for (int y = 0; y <= h; y++)
        {
            bool inked = y < h && rowCounts[y] >= minRowInk;
            if (inked && top >= 0 && y - last - 1 <= maxGap)
            {
                last = y;
                continue;
            }

            if (top >= 0 && (best is not (int bt, int bb) || last - top > bb - bt))
            {
                best = (top, last);
            }

            top = inked ? y : -1;
            last = inked ? y : -1;
        }

        return best is (int t, int b) && b > t ? best : null;
    }

    /// <summary>The band's rows with the slant taken out: of the shears tried, the one whose
    /// column profile is sharpest (largest sum of squared column counts) - upright strokes
    /// stack into a few full columns with clean gaps between digits.</summary>
    private static bool[,] Deskew(bool[,] on, int top, int bottom)
    {
        int w = on.GetLength(0);
        int bandHeight = bottom - top + 1;
        int pad = (int)Math.Ceiling(MaxShear * bandHeight / 2) + 1;
        double centre = (bandHeight - 1) / 2.0;

        int Shift(double shear, int y) => (int)Math.Round(shear * (y - centre)) + pad;

        double bestShear = 0, bestScore = -1;
        for (int step = -5; step <= (int)Math.Round(MaxShear / 0.02); step++)
        {
            double shear = step * 0.02;
            var cols = new int[w + 2 * pad];
            for (int y = 0; y < bandHeight; y++)
            {
                int shift = Shift(shear, y);
                for (int x = 0; x < w; x++) if (on[x, top + y]) cols[x + shift]++;
            }

            double score = 0;
            foreach (int c in cols) score += (double)c * c;
            if (score > bestScore) { bestScore = score; bestShear = shear; }
        }

        var result = new bool[w + 2 * pad, bandHeight];
        for (int y = 0; y < bandHeight; y++)
        {
            int shift = Shift(bestShear, y);
            for (int x = 0; x < w; x++) if (on[x, top + y]) result[x + shift, y] = true;
        }
        return result;
    }

    /// <summary>A full digit's width (not a '1'): the median of the runs shaped like one digit,
    /// else the usual seven-segment proportion of the readout's height.</summary>
    private static double DigitWidth(List<(int start, int end)> runs, int inkHeight)
    {
        var single = runs
            .Select(r => r.end - r.start + 1)
            .Where(width => width >= inkHeight * 0.3 && width <= inkHeight * 0.85)
            .OrderBy(width => width)
            .ToList();
        return single.Count > 0 ? single[single.Count / 2] : inkHeight * 0.55;
    }

    private static int RunInkHeight(bool[,] on, int start, int end, int top, int bottom)
    {
        int first = -1, last = -1;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = start; x <= end; x++)
            {
                if (!on[x, y]) continue;
                if (first < 0) first = y;
                last = y;
                break;
            }
        }
        return first < 0 ? 0 : last - first + 1;
    }

    /// <summary>Otsu's threshold: the value that best splits the signal into two classes
    /// (background and strokes), so the cut adapts to dim or glary photos instead of a fixed
    /// fraction of the brightest pixel.</summary>
    private static int OtsuThreshold(int[,] signal, int w, int h, int maxValue)
    {
        var histogram = new long[maxValue + 1];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                histogram[Math.Clamp(signal[x, y], 0, maxValue)]++;

        long total = (long)w * h;
        double sumAll = 0;
        for (int i = 0; i <= maxValue; i++) sumAll += i * (double)histogram[i];

        double sumBelow = 0, bestVariance = -1;
        long countBelow = 0;
        int best = 0;
        for (int t = 0; t <= maxValue; t++)
        {
            countBelow += histogram[t];
            if (countBelow == 0) continue;
            long countAbove = total - countBelow;
            if (countAbove == 0) break;

            sumBelow += t * (double)histogram[t];
            double meanBelow = sumBelow / countBelow;
            double meanAbove = (sumAll - sumBelow) / countAbove;
            double variance = (double)countBelow * countAbove * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (variance > bestVariance) { bestVariance = variance; best = t; }
        }
        return best;
    }

    private static readonly Dictionary<string, char> Patterns = new()
    {
        ["ABCDEF"] = '0',
        ["BC"] = '1',
        ["ABGED"] = '2',
        ["ABGCD"] = '3',
        ["FGBC"] = '4',
        ["AFGCD"] = '5',
        ["AFGECD"] = '6',
        ["ABC"] = '7',
        ["ABCDEFG"] = '8',
        ["ABCDFG"] = '9',
    };

    private static DigitResult DecodeDigit(bool[,] on, int left, int right, int top, int bottom)
    {
        int cw = right - left + 1;
        int ch = bottom - top + 1;
        if (cw <= 0 || ch <= 0) return new DigitResult { Digit = '?', IsConfident = false };

        bool allConfident = true;

        bool SegOn(double x0, double y0, double x1, double y1)
        {
            int sx0 = left + (int)(x0 * cw), sx1 = left + (int)(x1 * cw);
            int sy0 = top + (int)(y0 * ch), sy1 = top + (int)(y1 * ch);
            int total = 0, onCount = 0;
            for (int y = sy0; y <= sy1 && y <= bottom; y++)
                for (int x = sx0; x <= sx1 && x <= right; x++)
                {
                    total++;
                    if (on[x, y]) onCount++;
                }
            double frac = total > 0 ? (double)onCount / total : 0;
            if (frac > OffBand && frac < OnBand) allConfident = false;
            return frac > DecisionThreshold;
        }

        // Thin strips centered on each stroke, well clear of corners/joints where an
        // adjacent segment's own ink would otherwise bleed into the sample.
        bool a = SegOn(0.30, 0.06, 0.70, 0.16);
        bool f = SegOn(0.02, 0.20, 0.22, 0.40);
        bool b = SegOn(0.78, 0.20, 0.98, 0.40);
        bool g = SegOn(0.30, 0.46, 0.70, 0.54);
        bool e = SegOn(0.02, 0.60, 0.22, 0.80);
        bool c = SegOn(0.78, 0.60, 0.98, 0.80);
        bool d = SegOn(0.30, 0.84, 0.70, 0.94);

        var sb = new StringBuilder();
        if (a) sb.Append('A');
        if (b) sb.Append('B');
        if (c) sb.Append('C');
        if (d) sb.Append('D');
        if (e) sb.Append('E');
        if (f) sb.Append('F');
        if (g) sb.Append('G');

        var mySet = new HashSet<char>(sb.ToString());
        foreach (var kv in Patterns)
        {
            if (mySet.SetEquals(new HashSet<char>(kv.Key)))
            {
                return new DigitResult { Digit = kv.Value, IsConfident = allConfident };
            }
        }

        return new DigitResult { Digit = '?', IsConfident = false };
    }
}

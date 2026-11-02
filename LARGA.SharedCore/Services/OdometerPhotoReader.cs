using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Odometer readings from a dashboard photo, ML Kit and the seven-segment decoder together
/// (OdometerScanPage). ML Kit finds where the readout is but reads a seven-segment 6 as a 5 and
/// drops digits next to "ODO"; the decoder reads the segments of the box ML Kit found and the
/// two are reconciled (OdometerReadingParser.Reconcile). Tested on real Vios clusters in
/// LARGA.Tests/Fuel/RealOdometerTests.
/// </summary>
public static class OdometerPhotoReader
{
    /// <summary>A taxi odometer shows at most 6 digits.</summary>
    public const int MaxDigits = 6;

    /// <summary>One OCR line with its box, normalized to 0-1 of the upright photo.</summary>
    public readonly record struct Region(string? Text, double X, double Y, double Width, double Height);

    /// <summary>A reading and the OCR line it was found on (where its button goes).</summary>
    public readonly record struct Found(OdometerReadingParser.Reading Reading, Region Region);

    // ML Kit's line box hugs the glyphs; the decoder needs a little background around them.
    private const double MarginX = 0.10;
    private const double MarginY = 0.30;

    /// <summary>The readings on a photo (upright, as ML Kit saw it), best line first, without
    /// duplicates. Lines without a 5-6 digit candidate are decoded too when they may hold a
    /// partly read one ("o00 5377"). Without the photo, ML Kit's candidates as they are.</summary>
    public static List<Found> Read(SKBitmap? photo, IEnumerable<Region> regions)
    {
        var found = new List<Found>();
        var seen = new HashSet<string>();
        foreach (Region region in regions)
        {
            string? candidate = OdometerReadingParser.Candidates(region.Text).FirstOrDefault(c => !seen.Contains(c));
            string? partial = candidate == null ? OdometerReadingParser.PartialReading(region.Text) : null;
            if (candidate == null && partial == null)
            {
                continue;
            }

            var decoded = photo == null ? new SevenSegmentDecoder.DecodeResult() : DecodeBox(photo, region);
            if (OdometerReadingParser.Reconcile(candidate, decoded, partial) is { } reading && seen.Add(reading.Text))
            {
                found.Add(new Found(reading, region));
            }
        }
        return found;
    }

    /// <summary>The reading in a readout the driver framed: ML Kit's passes over the frame (see
    /// OdometerReadingParser.FramedReading) checked against the decoder - null when neither read
    /// a usable number - and the decoder's own result.</summary>
    public static (OdometerReadingParser.Reading? Reading, SevenSegmentDecoder.DecodeResult Decoded) ReadFramed(
        SKBitmap crop, IEnumerable<IEnumerable<string?>> ocrPasses)
    {
        var decoded = SevenSegmentDecoder.Decode(crop, MaxDigits);
        return (OdometerReadingParser.Reconcile(OdometerReadingParser.FramedReading(ocrPasses), decoded), decoded);
    }

    private static SevenSegmentDecoder.DecodeResult DecodeBox(SKBitmap photo, Region region)
    {
        int left = (int)Math.Floor(Math.Max(0, region.X - region.Width * MarginX) * photo.Width);
        int top = (int)Math.Floor(Math.Max(0, region.Y - region.Height * MarginY) * photo.Height);
        int right = (int)Math.Ceiling(Math.Min(1, region.X + region.Width * (1 + MarginX)) * photo.Width);
        int bottom = (int)Math.Ceiling(Math.Min(1, region.Y + region.Height * (1 + MarginY)) * photo.Height);
        if (right - left < 4 || bottom - top < 4)
        {
            return new SevenSegmentDecoder.DecodeResult();
        }

        using var crop = new SKBitmap(right - left, bottom - top);
        using (var canvas = new SKCanvas(crop))
        {
            canvas.DrawBitmap(photo, new SKRectI(left, top, right, bottom), new SKRect(0, 0, crop.Width, crop.Height));
        }
        return SevenSegmentDecoder.Decode(crop, MaxDigits);
    }
}

using System;
using System.IO;
using LARGA.SharedCore.Services;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;
using SkiaSharp;

namespace LARGA.MobileApp.Services;

/// <summary>
/// A cleaned-up copy of a photo for a second OCR pass when the first one missed something: turned
/// upright (EXIF), scaled so small text is big enough for ML Kit, grayscale, and with its contrast
/// stretched (faded thermal paper, dim dashboards). The original photo is kept for upload.
/// </summary>
public static class OcrImageEnhancer
{
    private const int TargetLongSide = 2000;
    private const float MaxUpscale = 3f;

    /// <summary>Path of the enhanced JPEG in the cache folder, or null if the photo can't be read.</summary>
    public static string? CreateEnhancedCopy(string sourcePath)
    {
        try
        {
            using var codec = SKCodec.Create(sourcePath);
            if (codec == null)
            {
                return null;
            }

            using var decoded = SKBitmap.Decode(codec);
            if (decoded == null)
            {
                return null;
            }

            using var upright = ApplyOrientation(decoded, codec.EncodedOrigin);

            int longSide = Math.Max(upright.Width, upright.Height);
            float scale = Math.Clamp((float)TargetLongSide / longSide, 0.5f, MaxUpscale);
            int width = Math.Max(1, (int)(upright.Width * scale));
            int height = Math.Max(1, (int)(upright.Height * scale));

            (float low, float high) = LuminanceRange(upright);
            float range = Math.Max(high - low, 0.2f);
            float gain = 1f / range;
            float offset = -low * gain;

            // Grayscale (Rec. 601) with a linear stretch so the paper becomes white and ink black.
            float r = 0.299f * gain, g = 0.587f * gain, b = 0.114f * gain;
            var matrix = new[]
            {
                r, g, b, 0, offset,
                r, g, b, 0, offset,
                r, g, b, 0, offset,
                0, 0, 0, 1, 0,
            };

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            using var paint = new SKPaint { ColorFilter = SKColorFilter.CreateColorMatrix(matrix) };
            surface.Canvas.Clear(SKColors.White);
            using var image = SKImage.FromBitmap(upright);
            surface.Canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);

            string target = Path.Combine(FileSystem.CacheDirectory, $"ocr_enhanced_{Guid.NewGuid():N}.jpg");
            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 92);
            using (var stream = File.OpenWrite(target))
            {
                data.SaveTo(stream);
            }
            return target;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OCR image enhance failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>A copy for the license scan's extra passes (LicenseScanPipeline): upright,
    /// optionally cropped to <paramref name="crop"/> (normalized 0-1, in ML Kit's upright frame)
    /// and enlarged, then run through OcrGlareFilter - which strips the shiny seal's colours,
    /// evens out glare/shadows and darkens faded print. Null if the photo can't be read.</summary>
    public static string? CreateLicensePass(string sourcePath, OcrGlareFilter.Mode mode, double sharpen = 0, Rect? crop = null, double? backgroundFraction = null)
    {
        try
        {
            using var upright = LoadUpright(sourcePath);
            if (upright == null)
            {
                return null;
            }

            SKRectI area = new(0, 0, upright.Width, upright.Height);
            if (crop is Rect c)
            {
                area = SKRectI.Intersect(area, new SKRectI(
                    (int)(c.X * upright.Width), (int)(c.Y * upright.Height),
                    (int)Math.Ceiling(c.Right * upright.Width), (int)Math.Ceiling(c.Bottom * upright.Height)));
                if (area.Width < 8 || area.Height < 8)
                {
                    return null;
                }
            }

            // Whole card: ~2000 px on the long side, like the receipt pass. Crop of one row:
            // enlarged up to 3x so the digits are tall enough for ML Kit to separate.
            int target = crop is null ? TargetLongSide : 1800;
            float scale = Math.Clamp((float)target / Math.Max(area.Width, area.Height), 0.25f, MaxUpscale);
            int width = Math.Max(1, (int)(area.Width * scale));
            int height = Math.Max(1, (int)(area.Height * scale));

            using var scaled = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(scaled))
            using (var image = SKImage.FromBitmap(upright))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawImage(image, SKRect.Create(area.Left, area.Top, area.Width, area.Height),
                    new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell), null);
            }

            // A crop is one row of text: the paper-brightness blur must still be wider than a digit.
            byte[] gray = OcrGlareFilter.Apply(scaled.Bytes, width, height, mode,
                backgroundFraction: backgroundFraction ?? (crop is null ? 1.0 / 40 : 1.0 / 12), sharpen: sharpen);

            using var output = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
            System.Runtime.InteropServices.Marshal.Copy(gray, 0, output.GetPixels(), gray.Length);

            string path = Path.Combine(FileSystem.CacheDirectory, $"ocr_license_{Guid.NewGuid():N}.jpg");
            using var data = output.Encode(SKEncodedImageFormat.Jpeg, 92);
            using (var stream = File.OpenWrite(path))
            {
                data.SaveTo(stream);
            }
            return path;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"License OCR pass failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>The photo turned upright (EXIF), as ML Kit sees it - so its normalized boxes land
    /// on the same pixels - or null if it can't be read.</summary>
    public static SKBitmap? LoadUpright(string sourcePath)
    {
        try
        {
            using var codec = SKCodec.Create(sourcePath);
            if (codec == null)
            {
                return null;
            }

            using var decoded = SKBitmap.Decode(codec);
            return decoded == null ? null : ApplyOrientation(decoded, codec.EncodedOrigin);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Load upright photo failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>The 2nd and 98th percentile of luminance (0-1), sampled on a grid.</summary>
    private static (float Low, float High) LuminanceRange(SKBitmap bitmap)
    {
        var histogram = new int[256];
        int step = Math.Max(1, Math.Min(bitmap.Width, bitmap.Height) / 200);
        int total = 0;
        for (int y = 0; y < bitmap.Height; y += step)
        {
            for (int x = 0; x < bitmap.Width; x += step)
            {
                SKColor c = bitmap.GetPixel(x, y);
                histogram[(int)(0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue)]++;
                total++;
            }
        }

        int lowCount = total * 2 / 100, highCount = total * 98 / 100, seen = 0;
        int low = 0, high = 255;
        bool lowFound = false;
        for (int i = 0; i < 256; i++)
        {
            seen += histogram[i];
            if (!lowFound && seen >= lowCount)
            {
                low = i;
                lowFound = true;
            }
            if (seen >= highCount)
            {
                high = i;
                break;
            }
        }
        return (low / 255f, high / 255f);
    }

    private static SKBitmap ApplyOrientation(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        int degrees = PhotoOrientation.DegreesFromExif((int)origin);
        if (degrees == 0)
        {
            return bitmap.Copy();
        }

        bool swap = degrees != 180;
        var rotated = new SKBitmap(swap ? bitmap.Height : bitmap.Width, swap ? bitmap.Width : bitmap.Height);
        using var canvas = new SKCanvas(rotated);
        canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-bitmap.Width / 2f, -bitmap.Height / 2f);
        canvas.DrawBitmap(bitmap, 0, 0);
        return rotated;
    }
}

using System;
using System.IO;
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
        int degrees = origin switch
        {
            SKEncodedOrigin.RightTop => 90,
            SKEncodedOrigin.BottomRight => 180,
            SKEncodedOrigin.LeftBottom => 270,
            _ => 0,
        };
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

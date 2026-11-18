using System;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Cleans a photo of an ID card for OCR when the plain photo didn't read: removes the coloured
/// security print and hologram (the LTO license's shiny seal), evens out uneven light and
/// shadows, and brings faded print back to near-black. Works on raw RGBA pixels so it runs the
/// same on the phone (OcrImageEnhancer feeds it SkiaSharp pixels) and in unit tests.
///
/// 1. Ink channel: <see cref="Mode.ColorMax"/> takes each pixel's brightest channel. Black
///    ink is dark in every channel and stays dark; the seal's yellow/cyan/magenta and the
///    card's pastel background are bright in at least one channel and turn white.
///    <see cref="Mode.Luminance"/> is plain grayscale, for when the ink itself is coloured.
/// 2. Flatten: divides by a heavily blurred copy (the local paper brightness), so a shadow,
///    a dim corner or a bright glare patch all end up the same white, with the ink relative
///    to it - faint ink under glare gets its contrast back.
/// 3. Stretch: the 1st-99th percentile range becomes 0-255.
/// 4. Optional sharpen (unsharp mask) for slightly blurry photos.
/// </summary>
public static class OcrGlareFilter
{
    public enum Mode
    {
        ColorMax,
        Luminance,
    }

    /// <param name="rgba">Width*height*4 bytes, R,G,B,A order.</param>
    /// <param name="backgroundFraction">Blur radius for the paper brightness, as a fraction of
    /// the longer side. Larger than a character, or the inside of thick strokes turns white:
    /// 1/40 for a whole card, about 1/12 for an enlarged crop of one row.</param>
    /// <returns>Width*height gray bytes.</returns>
    public static byte[] Apply(byte[] rgba, int width, int height, Mode mode, double backgroundFraction = 1.0 / 40, double sharpen = 0)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
        {
            throw new ArgumentException("Pixel buffer doesn't match the size.", nameof(rgba));
        }

        int n = width * height;
        var ink = new float[n];
        for (int i = 0, p = 0; i < n; i++, p += 4)
        {
            byte r = rgba[p], g = rgba[p + 1], b = rgba[p + 2];
            ink[i] = mode == Mode.ColorMax
                ? Math.Max(r, Math.Max(g, b))
                : 0.299f * r + 0.587f * g + 0.114f * b;
        }

        int radius = Math.Max(2, (int)(Math.Max(width, height) * backgroundFraction));
        float[] paper = Background(ink, width, height, radius);

        var flat = new float[n];
        for (int i = 0; i < n; i++)
        {
            flat[i] = Math.Min(1.5f, ink[i] / Math.Max(paper[i], 8f));
        }

        if (sharpen > 0)
        {
            float[] soft = BoxBlur(flat, width, height, 1, 2);
            for (int i = 0; i < n; i++)
            {
                flat[i] += (float)sharpen * (flat[i] - soft[i]);
            }
        }

        (float low, float high) = Percentiles(flat, 0.01, 0.99);
        float range = Math.Max(high - low, 0.05f);
        var gray = new byte[n];
        for (int i = 0; i < n; i++)
        {
            float v = (flat[i] - low) / range * 255f;
            gray[i] = (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
        }

        return gray;
    }

    /// <summary>Paper brightness: the ink channel shrunk 8x, box-blurred three times (close to
    /// a Gaussian) and scaled back up bilinearly - cheap even for a 12 MP photo.</summary>
    private static float[] Background(float[] ink, int width, int height, int radius)
    {
        const int Step = 8;
        int sw = Math.Max(1, (width + Step - 1) / Step);
        int sh = Math.Max(1, (height + Step - 1) / Step);
        var small = new float[sw * sh];
        var counts = new int[sw * sh];
        for (int y = 0; y < height; y++)
        {
            int row = (y / Step) * sw;
            for (int x = 0; x < width; x++)
            {
                small[row + x / Step] += ink[y * width + x];
                counts[row + x / Step]++;
            }
        }
        for (int i = 0; i < small.Length; i++)
        {
            small[i] /= Math.Max(1, counts[i]);
        }

        small = BoxBlur(small, sw, sh, Math.Max(1, radius / Step), 3);

        var full = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            float fy = Math.Clamp((y + 0.5f) / Step - 0.5f, 0, sh - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, sh - 1);
            float ty = fy - y0;
            for (int x = 0; x < width; x++)
            {
                float fx = Math.Clamp((x + 0.5f) / Step - 0.5f, 0, sw - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, sw - 1);
                float tx = fx - x0;
                float top = small[y0 * sw + x0] * (1 - tx) + small[y0 * sw + x1] * tx;
                float bottom = small[y1 * sw + x0] * (1 - tx) + small[y1 * sw + x1] * tx;
                full[y * width + x] = top * (1 - ty) + bottom * ty;
            }
        }

        return full;
    }

    /// <summary>Separable box blur, <paramref name="passes"/> times, edges clamped.</summary>
    private static float[] BoxBlur(float[] src, int width, int height, int radius, int passes)
    {
        float[] a = (float[])src.Clone();
        var b = new float[a.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            BlurLine(a, b, width, height, radius, horizontal: true);
            BlurLine(b, a, width, height, radius, horizontal: false);
        }
        return a;
    }

    private static void BlurLine(float[] src, float[] dst, int width, int height, int radius, bool horizontal)
    {
        int lines = horizontal ? height : width;
        int length = horizontal ? width : height;
        float scale = 1f / (2 * radius + 1);
        for (int line = 0; line < lines; line++)
        {
            int Index(int k) => horizontal
                ? line * width + Math.Clamp(k, 0, length - 1)
                : Math.Clamp(k, 0, length - 1) * width + line;

            float sum = 0;
            for (int k = -radius; k <= radius; k++)
            {
                sum += src[Index(k)];
            }
            for (int k = 0; k < length; k++)
            {
                dst[Index(k)] = sum * scale;
                sum += src[Index(k + radius + 1)] - src[Index(k - radius)];
            }
        }
    }

    private static (float Low, float High) Percentiles(float[] values, double low, double high)
    {
        const int Bins = 1024;
        const float Max = 1.5f;
        var histogram = new int[Bins];
        int step = Math.Max(1, values.Length / 250_000);
        int total = 0;
        for (int i = 0; i < values.Length; i += step)
        {
            histogram[Math.Clamp((int)(values[i] / Max * (Bins - 1)), 0, Bins - 1)]++;
            total++;
        }

        float At(double fraction)
        {
            long target = (long)(total * fraction);
            long seen = 0;
            for (int i = 0; i < Bins; i++)
            {
                seen += histogram[i];
                if (seen > target)
                {
                    return i * Max / (Bins - 1);
                }
            }
            return Max;
        }

        return (At(low), At(high));
    }
}

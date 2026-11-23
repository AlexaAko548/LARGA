using LARGA.SharedCore.Services;
using SkiaSharp;

namespace LARGA.Tests.Fuel;

/// <summary>"Frame the Digits" fallback decoder: green LCDs, white/dark digits, and frames that
/// hold no readout at all (which used to come back as a long row of 1s).</summary>
public class SevenSegmentDecoderTests
{
    private const int DigitWidth = 40, DigitHeight = 80, Stroke = 12, Gap = 16, Margin = 20;

    private static readonly Dictionary<char, string> Segments = new()
    {
        ['0'] = "ABCDEF", ['2'] = "ABGED", ['3'] = "ABGCD", ['4'] = "FGBC",
        ['5'] = "AFGCD", ['6'] = "AFGECD", ['8'] = "ABCDEFG", ['9'] = "ABCDFG",
    };

    private static SKBitmap Readout(string digits, SKColor background, SKColor ink)
    {
        var bitmap = new SKBitmap(Margin * 2 + digits.Length * (DigitWidth + Gap), DigitHeight + Margin * 2);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(background);
        using var paint = new SKPaint { Color = ink, IsAntialias = false };

        for (int i = 0; i < digits.Length; i++)
        {
            float x = Margin + i * (DigitWidth + Gap), y = Margin;
            float half = DigitHeight / 2f;
            foreach (char s in Segments[digits[i]])
            {
                SKRect r = s switch
                {
                    'A' => SKRect.Create(x, y, DigitWidth, Stroke),
                    'B' => SKRect.Create(x + DigitWidth - Stroke, y, Stroke, half),
                    'C' => SKRect.Create(x + DigitWidth - Stroke, y + half, Stroke, half),
                    'D' => SKRect.Create(x, y + DigitHeight - Stroke, DigitWidth, Stroke),
                    'E' => SKRect.Create(x, y + half, Stroke, half),
                    'F' => SKRect.Create(x, y, Stroke, half),
                    _ => SKRect.Create(x, y + half - Stroke / 2f, DigitWidth, Stroke),
                };
                canvas.DrawRect(r, paint);
            }
        }
        return bitmap;
    }

    [Fact]
    public void GreenLcd_StillDecodes()
    {
        using var crop = Readout("058296", new SKColor(10, 15, 10), new SKColor(60, 230, 70));
        Assert.Equal("058296", SevenSegmentDecoder.Decode(crop, 6).Text);
    }

    [Fact]
    public void WhiteDigitsOnBlack_Decode()
    {
        using var crop = Readout("523486", new SKColor(20, 20, 24), new SKColor(225, 225, 220));
        Assert.Equal("523486", SevenSegmentDecoder.Decode(crop, 6).Text);
    }

    [Fact]
    public void DarkDigitsOnLightFace_Decode()
    {
        using var crop = Readout("905824", new SKColor(230, 230, 225), new SKColor(25, 25, 30));
        Assert.Equal("905824", SevenSegmentDecoder.Decode(crop, 6).Text);
    }

    [Fact]
    public void NoisyFrame_IsNotAWallOfOnes()
    {
        // A grey patch with JPEG-like speckle: the case that returned "1111...1111".
        var random = new Random(42);
        using var crop = new SKBitmap(360, 120);
        for (int y = 0; y < crop.Height; y++)
            for (int x = 0; x < crop.Width; x++)
            {
                byte v = (byte)(60 + random.Next(0, 70));
                crop.SetPixel(x, y, new SKColor(v, (byte)Math.Min(255, v + random.Next(0, 12)), v));
            }

        var result = SevenSegmentDecoder.Decode(crop, 6);
        Assert.True(result.Digits.Count <= 8, $"decoded {result.Text}");
        Assert.DoesNotContain("1111", result.Text);
    }

    [Fact]
    public void FlatFrame_FindsNothing()
    {
        using var crop = new SKBitmap(300, 100);
        using (var canvas = new SKCanvas(crop)) canvas.Clear(new SKColor(90, 90, 90));
        Assert.Empty(SevenSegmentDecoder.Decode(crop, 6).Digits);
    }
}

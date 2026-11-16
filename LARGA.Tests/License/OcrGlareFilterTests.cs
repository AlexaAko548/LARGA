using LARGA.SharedCore.Services;

namespace LARGA.Tests.License;

/// <summary>OcrGlareFilter on small synthetic cards: black ink on paper with a coloured
/// hologram patch, a glare patch and uneven light.</summary>
public class OcrGlareFilterTests
{
    private const int W = 400, H = 240;

    private static byte[] Card(Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                var (r, g, b) = pixel(x, y);
                int p = (y * W + x) * 4;
                rgba[p] = r;
                rgba[p + 1] = g;
                rgba[p + 2] = b;
                rgba[p + 3] = 255;
            }
        }
        return rgba;
    }

    // Ink: thin vertical strokes in the band y 100-120 (like a row of digits).
    private static bool IsInk(int x, int y) => y is >= 100 and < 120 && x % 20 < 4;

    [Fact]
    public void Coloured_hologram_turns_white_and_black_ink_stays_black()
    {
        byte[] rgba = Card((x, y) =>
            IsInk(x, y) ? ((byte)30, (byte)30, (byte)30)
            : x < 200 ? ((byte)235, (byte)220, (byte)60)   // yellow seal print
            : ((byte)240, (byte)240, (byte)235));          // paper

        byte[] gray = OcrGlareFilter.Apply(rgba, W, H, OcrGlareFilter.Mode.ColorMax);

        Assert.True(gray[60 * W + 50] > 200, "seal colour should become paper white");
        Assert.True(gray[110 * W + 41] < 60, "ink inside the seal should stay dark");
        Assert.True(gray[110 * W + 241] < 60, "ink on paper should stay dark");

        // Plain grayscale keeps the yellow as a gray patch - why ColorMax exists.
        byte[] lum = OcrGlareFilter.Apply(rgba, W, H, OcrGlareFilter.Mode.Luminance);
        Assert.True(lum[60 * W + 50] < gray[60 * W + 50]);
    }

    [Fact]
    public void Faint_ink_under_glare_and_in_shadow_gets_its_contrast_back()
    {
        // Left half in shadow (paper 90, ink 40); right half under glare (paper 250, ink 205).
        byte[] rgba = Card((x, y) =>
        {
            bool shadow = x < 200;
            byte v = IsInk(x, y) ? (byte)(shadow ? 40 : 205) : (byte)(shadow ? 90 : 250);
            return (v, v, v);
        });

        byte[] gray = OcrGlareFilter.Apply(rgba, W, H, OcrGlareFilter.Mode.ColorMax);

        int shadowPaper = gray[60 * W + 100], glarePaper = gray[60 * W + 300];
        int shadowInk = gray[110 * W + 101], glareInk = gray[110 * W + 301];
        Assert.InRange(Math.Abs(shadowPaper - glarePaper), 0, 40);
        Assert.True(glarePaper - glareInk > 40, $"glare ink {glareInk} vs paper {glarePaper}");
        Assert.True(shadowPaper - shadowInk > 80, $"shadow ink {shadowInk} vs paper {shadowPaper}");
    }

    [Fact]
    public void Rejects_a_buffer_of_the_wrong_size() =>
        Assert.Throws<ArgumentException>(() => OcrGlareFilter.Apply(new byte[10], W, H, OcrGlareFilter.Mode.ColorMax));
}

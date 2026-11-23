using System.Globalization;
using System.Text.RegularExpressions;
using LARGA.SharedCore.Services;
using SkiaSharp;
using static LARGA.SharedCore.Services.OdometerPhotoReader;

namespace LARGA.Tests.Fuel;

/// <summary>
/// Real Toyota Vios clusters (tests/odometer: 5-digit italic LCDs, the reading is the file name).
/// Fixtures/Odometer holds what ML Kit returned for each photo and for the readout framed by
/// "Frame the Digits", captured on the emulator with the DEBUG OcrBatchReceiver. ML Kit alone
/// reads a 6 as a 5 and drops the digit next to "ODO"; with the seven-segment decoder reading the
/// same box, the app offers the right number on every photo.
/// </summary>
public class RealOdometerTests
{
    // Where a driver frames each readout, in photo pixels (left, top, right, bottom). Each
    // frame takes in the "km" under the digits and a bit of the bezel, as a real one does.
    public static TheoryData<string, int, int, int, int> Readouts => new()
    {
        { "vios_16098.jpg", 320, 258, 447, 304 },
        { "vios_25377.jfif", 338, 418, 418, 452 },
        { "vios_29044.jfif", 216, 331, 282, 362 },
        { "vios_36806.jfif", 277, 256, 342, 285 },
        { "vios_58609.jfif", 328, 360, 394, 394 },
    };

    [Theory]
    [InlineData("vios_16098", "15098")]  // "I5098": the 6 read as a 5
    [InlineData("vios_25377", null)]     // only "o00 5377" (enhanced pass)
    [InlineData("vios_29044", "29044")]  // enhanced pass only
    [InlineData("vios_36806", "36805")]
    [InlineData("vios_58609", null)]     // "o0o S859"
    public void MlKitAlone_IsNotEnough(string photo, string? mlKitReading)
    {
        string? first = OdometerReadingParser.Candidates(Lines($"mlkit_{photo}.txt")).FirstOrDefault()
                        ?? OdometerReadingParser.Candidates(Lines($"mlkit_{photo}_enhanced.txt")).FirstOrDefault();
        Assert.Equal(mlKitReading, first);
    }

    [Theory]
    [InlineData("vios_16098", false)]
    [InlineData("vios_25377", false)]
    [InlineData("vios_29044", true)]   // ML Kit and the decoder agree
    [InlineData("vios_36806", false)]
    [InlineData("vios_58609", false)]
    public void WholePhoto_RightReadingOffered(string photo, bool confident)
    {
        string truth = photo["vios_".Length..];
        using SKBitmap bitmap = Photo(photo);

        // As OdometerScanPage: the photo's lines, then the enhanced copy's when they hold nothing.
        var found = Read(bitmap, Regions($"mlkit_{photo}.txt"));
        if (found.Count == 0)
        {
            found = Read(bitmap, Regions($"mlkit_{photo}_enhanced.txt"));
        }

        Assert.NotEmpty(found);
        Assert.True(truth == found[0].Reading.Text, $"offered {string.Join(", ", found.Select(f => $"{f.Reading.Text} from \"{f.Region.Text}\""))}");
        Assert.Equal(confident, found[0].Reading.Confident);
    }

    [Theory]
    [MemberData(nameof(Readouts))]
    public void FramedReadout_RightReading(string file, int left, int top, int right, int bottom)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        using SKBitmap crop = Crop(file, left, top, right, bottom);

        // As OdometerScanPage: the enhanced frame first, the plain one unless that held a reading.
        var passes = new List<List<string?>> { Lines($"mlkit_framed_{name}_enhanced.txt") };
        if (OdometerReadingParser.Candidates(passes[0]).Count == 0)
        {
            passes.Add(Lines($"mlkit_framed_{name}.txt"));
        }

        var (reading, _) = ReadFramed(crop, passes);
        Assert.Equal(name["vios_".Length..], reading?.Text);
    }

    [Theory]
    [MemberData(nameof(Readouts))]
    public void Decoder_NeverConfidentlyWrong(string file, int left, int top, int right, int bottom)
    {
        string truth = Path.GetFileNameWithoutExtension(file)["vios_".Length..];
        using SKBitmap crop = Crop(file, left, top, right, bottom);

        var result = SevenSegmentDecoder.Decode(crop, MaxDigits);

        Assert.Equal(truth.Length, result.Digits.Count);
        for (int i = 0; i < truth.Length; i++)
        {
            var digit = result.Digits[i];
            Assert.True(digit.Digit == truth[i] || !digit.IsConfident, $"{file}: digit {i} read as '{digit.Digit}' (confident), is '{truth[i]}' - {result.Text}");
        }
    }

    [Fact]
    public void Decoder_ReadsMostRealReadoutsExactly()
    {
        int exact = Readouts.Count(row =>
        {
            var (file, l, t, r, b) = ((string)row[0], (int)row[1], (int)row[2], (int)row[3], (int)row[4]);
            using SKBitmap crop = Crop(file, l, t, r, b);
            return SevenSegmentDecoder.Decode(crop, MaxDigits).Text == Path.GetFileNameWithoutExtension(file)["vios_".Length..];
        });

        // 25377: the last 7 reads as an unsure 1 (ML Kit's 7 is kept, see FramedReadout_RightReading).
        Assert.True(exact >= 4, $"{exact}/5 exact");
    }

    // ---------- fixtures ----------

    private static readonly Regex LogLine = new(
        @"LARGA-OCR line \d+ norm=\[x(?<x>-?[\d.]+) y(?<y>-?[\d.]+) w(?<w>-?[\d.]+) h(?<h>-?[\d.]+)\].* text=""(?<t>.*)""\s*$");

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Odometer", name);

    /// <summary>ML Kit's lines with their boxes, as OdometerScanPage passes them on.</summary>
    private static List<Region> Regions(string fixture) =>
        File.ReadLines(Fixture(fixture))
            .Select(l => LogLine.Match(l))
            .Where(m => m.Success)
            .Select(m =>
            {
                double D(string g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
                return new Region(m.Groups["t"].Value, D("x"), D("y"), D("w"), D("h"));
            })
            .ToList();

    private static List<string?> Lines(string fixture) => Regions(fixture).Select(r => r.Text).ToList();

    private static SKBitmap Photo(string name)
    {
        string path = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Odometer"), name + ".*")
            .Single(p => !p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
        return SKBitmap.Decode(path) ?? throw new InvalidOperationException($"can't decode {path}");
    }

    private static SKBitmap Crop(string file, int left, int top, int right, int bottom)
    {
        using SKBitmap photo = Photo(Path.GetFileNameWithoutExtension(file));
        var crop = new SKBitmap(right - left, bottom - top);
        using var canvas = new SKCanvas(crop);
        canvas.DrawBitmap(photo, new SKRectI(left, top, right, bottom), new SKRect(0, 0, crop.Width, crop.Height));
        return crop;
    }
}

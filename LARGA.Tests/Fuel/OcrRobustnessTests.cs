using LARGA.SharedCore;
using LARGA.SharedCore.Services;
using static LARGA.SharedCore.Services.FuelReceiptTextParser;

namespace LARGA.Tests.Fuel;

/// <summary>The parsing improvements beyond the sheet: tilted photos, OCR slips inside numbers,
/// guessed totals, and odometer readings.</summary>
public class OcrRobustnessTests
{
    // ---------- receipts ----------

    [Fact]
    public void TiltedPhoto_LabelPairedWithItsOwnRow()
    {
        // Labels on the left, amounts on the right, the photo turned so the right side sits 1.5 rows
        // lower. Without the tilt correction TOTAL would pair with the CASH amount.
        const double slope = 0.06; // dy/dx
        OcrBlock Line(string text, double x, double rowY, double w) =>
            new(text, x, rowY + slope * (x + w / 2), w, 0.02, slope);

        var lines = new List<OcrBlock>
        {
            Line("PETRON", 0.35, 0.05, 0.3),
            Line("TOTAL", 0.05, 0.50, 0.15),
            Line("CASH", 0.05, 0.525, 0.12),
            Line("CHANGE", 0.05, 0.55, 0.17),
            Line("1,000.00", 0.70, 0.50, 0.20),
            Line("1,500.00", 0.70, 0.525, 0.20),
            Line("500.00", 0.75, 0.55, 0.15),
        };

        var scan = ReceiptScan.Run(lines);
        Assert.Equal(1000.00m, scan.Amount);
        Assert.False(scan.AmountUncertain);
    }

    [Theory]
    [InlineData("Php0.o0", "Php0.00")]
    [InlineData("PO.00", "P0.00")]
    [InlineData("Php1, 801. 10", "Php1,801.10")]
    [InlineData("TOTAL 1O0.00", "TOTAL 100.00")]
    [InlineData("Download Shell Go+ to get", "Download Shell Go+ to get")]
    [InlineData("OR No. 0012345", "OR No. 0012345")]
    public void OcrSlipsInsideNumbers_AreFixed_WordsAreNot(string input, string expected) =>
        Assert.Equal(expected, NormalizeNumbers(input));

    [Fact]
    public void NoTotalLabelAndNoVatBreakdown_IsGuessedAndFlagged()
    {
        var scan = ReceiptScan.FromLines("PETRON", "Date: 10/20/2026", "UNLEADED 10.000L", "650.00", "650.00");
        Assert.Equal(650.00m, scan.Amount);
        Assert.True(scan.AmountUncertain);
        Assert.Contains("Total amount was not confidently detected.", scan.Warning);
    }

    [Fact]
    public void NegativeDiscount_IsNeverTheTotal() =>
        Assert.Null(ReceiptScan.FromLines("PETRON", "Discount Php-142.59").Amount);

    [Fact]
    public void TotalPrintedUnderItsLabel_WithoutBoxes() =>
        Assert.Equal(1641.40m, ReceiptScan.FromLines("PETRON", "Total (incl. VAT)", "Php1,641.40", "VATable Sales", "Php1,465.54").Amount);

    [Fact]
    public void LabelOverVatLine_WithoutBoxes_VatIsNotTheTotal()
    {
        // "TOTAL" with no amount, and the VAT line under it: 175.97 is VAT, not the total.
        var scan = ReceiptScan.FromLines("PETRON", "TOTAL", "VAT 175.97", "CASH 2,000.00");
        Assert.NotEqual(175.97m, scan.Amount);
        Assert.True(scan.AmountUncertain);
    }

    [Fact]
    public void LabelAndAmountSplitAcrossLines_FoundInJoinedText()
    {
        var scan = ReceiptScan.FromLines("PETRON", "TOTAL:", "PHP", "1,641.40");
        Assert.Equal(1641.40m, scan.Amount);
        Assert.True(scan.AmountUncertain);
    }

    [Fact]
    public void LitersSplitFromTheirLabel_FoundInJoinedText() =>
        Assert.Equal(25.5m, ReceiptScan.FromLines("PETRON", "QTY", "25.5", "TOTAL 1,000.00").Liters);

    [Fact]
    public void DateSplitAcrossLines_FoundInJoinedText() =>
        Assert.Equal(new DateTime(2026, 10, 20), ReceiptScan.FromLines("PETRON", "Oct 20,", "2026", "TOTAL 500.00").Date);

    [Fact]
    public void LitersFromQuantityTimesPrice_WhenPrintedWithoutUnit() =>
        Assert.Equal(31.94m, ReceiptScan.FromLines("PETRON", "Description Qty. Price Amount", "*XCS", "31.94", "Php56.39", "Php1,801.10", "Total Php1,801.10").Liters);

    [Theory]
    [InlineData("02 - SVPGasoline", FuelStations.Shell)]
    [InlineData("V-POWER RACING 10.000L", FuelStations.Shell)]
    [InlineData("FuelSave Diesel", FuelStations.Shell)]
    [InlineData("*XCS", FuelStations.Petron)]
    [InlineData("BLAZE 100 OCTANE", FuelStations.Petron)]
    [InlineData("Turbo Diesel", FuelStations.Petron)]
    public void StationFromProductName(string product, string expected) =>
        Assert.Equal(expected, ReceiptScan.FromLines("ACME FUELS INC.", product, "TOTAL 500.00").Vendor);

    [Theory]
    [InlineData("SVPRESIDENT JUAN DELA CRUZ")]
    [InlineData("CALTEX SILVER")]
    [InlineData("SEAOIL EXCEED")]
    [InlineData("REFUEL SAVES YOU MORE")]
    [InlineData("SUPERTURBO DIESELS")]
    public void NoFalseStation(string header) =>
        Assert.Null(ReceiptScan.FromLines(header, "TOTAL 500.00").Vendor);

    // ---------- odometer ----------

    [Theory]
    [InlineData("05870O", "058700")]           // emulator capture: last 0 read as O
    [InlineData("ODO 058700 km", "058700")]    // labels used to become extra zeros ("000058700")
    [InlineData("58,700 km", "58700")]
    [InlineData("58 700", "58700")]
    [InlineData("O58 7OO", "058700")]
    [InlineData("TOTAL 123456", "123456")]
    [InlineData("I23456", "123456")]
    [InlineData("58700KM", "58700")]
    public void Odometer_Reading(string ocr, string expected) =>
        Assert.Equal(expected, OdometerReadingParser.Candidates(ocr).FirstOrDefault());

    [Theory]
    [InlineData("120")]                 // speedometer dial
    [InlineData("TRIP A 123.4")]        // trip meter
    [InlineData("12.5 km/L")]
    [InlineData("ODO")]
    [InlineData("000000")]
    [InlineData("OIL DSL")]
    public void Odometer_NotAReading(string ocr) =>
        Assert.Empty(OdometerReadingParser.Candidates(ocr));

    [Fact]
    public void Odometer_LabelledReadingComesFirst()
    {
        var candidates = OdometerReadingParser.Candidates(new[] { "TRIP 004521", "ODO 058700" });
        Assert.Equal("058700", candidates[0]);
    }

    [Fact]
    public void Odometer_FuelEconomyLineIsNotLabelledAsKm()
    {
        // "KM/L" is fuel economy, not the "km" next to an odometer reading.
        var candidates = OdometerReadingParser.Candidates(new[] { "AVG KM/L 123456", "058700" });
        Assert.Equal("058700", candidates[0]);
    }

    [Theory]
    [InlineData("120 140")]        // speedometer marks ML Kit put on one line (Vios cluster)
    [InlineData("100 120 140")]
    [InlineData("120 l40 0")]
    public void Odometer_DialMarksAreNotAReading(string ocr) =>
        Assert.Empty(OdometerReadingParser.Candidates(ocr));

    [Theory]
    [InlineData("O00 35805")]      // "ODO" read as O00 (Vios cluster)
    [InlineData("o0o 35805")]
    [InlineData("000 35805")]
    public void Odometer_OdoReadAsZeros_IsTheLabel(string ocr)
    {
        // The labelled line wins over an unlabelled number elsewhere on the dashboard.
        var candidates = OdometerReadingParser.Candidates(new[] { "15231", ocr });
        Assert.Equal("35805", candidates[0]);
    }

    [Theory]
    [InlineData("o00 5377", true)]   // 25377 with its first digit lost
    [InlineData("o0o S859", true)]   // 58609
    [InlineData("120 140", false)]
    [InlineData("S2 3k P", false)]   // the clock and temperature
    [InlineData("km/h", false)]
    public void Odometer_MayHoldPartReading(string ocr, bool expected) =>
        Assert.Equal(expected, OdometerReadingParser.MayHoldReading(ocr));

    [Fact]
    public void Odometer_Framed_ReadingFromEitherPass_BeforeAShortNumber()
    {
        // The enhanced pass split 25377 into "2537" / "17"; the plain pass read it whole.
        var passes = new[] { new string?[] { "2537", "17" }, new string?[] { "25377" } };
        Assert.Equal("25377", OdometerReadingParser.FramedReading(passes));
        Assert.Equal("2537", OdometerReadingParser.FramedReading(new[] { new string?[] { "2537", "17" } }));
    }

    private static SevenSegmentDecoder.DecodeResult Decoded(string digits, string confident = "") => new()
    {
        Digits = digits.Select((d, i) => new SevenSegmentDecoder.DigitResult
        {
            Digit = d,
            IsConfident = confident.Length == 0 || confident[i] == '+',
        }).ToList(),
    };

    [Theory]
    [InlineData("36805", "36806", "", "36806", false)]            // ML Kit's 6 read as 5, decoder sure
    [InlineData("25377", "25371", "++++?", "25377", false)]       // decoder unsure: ML Kit's digit kept
    [InlineData("29044", "29044", "", "29044", true)]             // both agree
    [InlineData("58509", "58409", "", "58509", false)]            // 5 vs 4 is not one segment: kept, flagged
    [InlineData("058700", "??058700", "", "058700", true)]        // "ODO" decoded as '?' cells
    [InlineData(null, "??25377", "??++++?", "25377", false)]      // decoder alone: offered, never sure
    public void Odometer_Reconcile(string? ocr, string decoded, string confident, string expected, bool sure)
    {
        var reading = OdometerReadingParser.Reconcile(ocr, Decoded(decoded, confident));
        Assert.Equal(expected, reading?.Text);
        Assert.Equal(sure, reading?.Confident);
    }

    [Theory]
    [InlineData("??25371", "??++++?", "5377", "25377")]   // ML Kit's "o00 5377" fixes the unsure last digit
    [InlineData("25371", "+++++", "5377", "25371")]       // a sure decoder digit stands
    [InlineData("25377", "", "2537", "25377")]            // the part may sit at the start too
    public void Odometer_Reconcile_DecoderFilledFromPartOfTheNumber(string decoded, string confident, string partial, string expected)
    {
        var reading = OdometerReadingParser.Reconcile(null, Decoded(decoded, confident), partial);
        Assert.Equal(expected, reading?.Text);
        Assert.False(reading?.Confident);
        Assert.Null(OdometerReadingParser.Reconcile(null, Decoded(""), partial));   // the part alone is never offered
    }

    [Fact]
    public void Odometer_Reconcile_NothingDecoded_KeepsMlKitAsIs()
    {
        // A drum odometer: the decoder finds no segments, ML Kit's reading stands.
        Assert.Equal(new OdometerReadingParser.Reading("058700", true), OdometerReadingParser.Reconcile("058700", Decoded("")));
        Assert.Null(OdometerReadingParser.Reconcile(null, Decoded("2?4")));
    }

    [Theory]
    [InlineData("Phps, 763.60", "Php5,763.60")]   // Petron, Molo: 5 read as s after the peso sign
    [InlineData("Php56.39", "Php56.39")]
    [InlineData("Pumps 3", "Pumps 3")]
    public void PesoSignFollowedByS_IsAFive(string input, string expected) =>
        Assert.Equal(expected, FuelReceiptTextParser.NormalizeNumbers(input));
}

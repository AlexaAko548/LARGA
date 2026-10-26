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
}

using System.Globalization;
using LARGA.SharedCore;
using LARGA.SharedCore.Services;
using static LARGA.SharedCore.Services.FuelReceiptTextParser;

namespace LARGA.Tests.Fuel;

/// <summary>FUEL-003 to FUEL-012: small inputs built from each row's Test Data and Pre-conditions.</summary>
public class ReceiptRuleTests
{
    // ---------- FUEL-003: OCR character confusion in the station name ----------

    [Theory]
    [InlineData("PETR0N SERVICE STATION", FuelStations.Petron)]
    [InlineData("P3TRON CORPORATION", FuelStations.Petron)]
    [InlineData("5HELL PILIPINAS", FuelStations.Shell)]
    [InlineData("SHE11 SELECT", FuelStations.Shell)]
    [InlineData("SHEII", FuelStations.Shell)]
    public void Fuel003_StationOcrSlipsAreNormalized(string header, string expected) =>
        Assert.Equal(expected, ReceiptScan.FromLines(header, "TOTAL 500.00").Vendor);

    // ---------- FUEL-004: non-Petron/Shell station ----------

    [Theory]
    [InlineData("CALTEX STAR MART", "TOTAL 1,000.00")]
    [InlineData("SEAOIL PHILIPPINES INC.", "TOTAL 1,000.00")]
    public void Fuel004_OtherStationIsNotAccepted(string header, string total)
    {
        var scan = ReceiptScan.FromLines(header, total);
        Assert.Null(scan.Vendor);
        // The page then submits "UNKNOWN", which FuelReportViewModel saves as Flagged.
        Assert.False(FuelStations.IsAllowed("UNKNOWN"));
        Assert.Equal("Station is not Petron or Shell", FuelStations.NotAllowedReason);
    }

    [Fact]
    public void Fuel004_ShellaneLpgOnAnotherBrandReceipt_IsNotShell() =>
        Assert.Null(ReceiptScan.FromLines("CALTEX STAR MART", "SHELLANE LPG 11KG", "TOTAL 1,000.00").Vendor);

    // ---------- FUEL-005: unreadable station ----------

    [Fact]
    public void Fuel005_UnreadableHeader_StationUnknownWithWarning()
    {
        var scan = ReceiptScan.FromLines("#~:: ..ll' ..", "10/20/2026", "UNLEADED 10.000L", "TOTAL 600.00");
        Assert.Null(scan.Vendor);
        Assert.Equal("OCR uncertain: Station not recognized as Petron or Shell. Please review/edit before submit.", scan.Warning);
    }

    // ---------- FUEL-006: unit price is not mistaken for the total ----------

    [Fact]
    public void Fuel006_PerLine() =>
        Assert.Equal(95.44m, ReceiptScan.FromLines("SHELL", "0.987L x 96.700P/L", "TOTAL 95.44").Amount);

    [Fact]
    public void Fuel006_TotalLabelAndValueInSeparateBlocks() =>
        Assert.Equal(95.44m, ReceiptScan.Run(new List<OcrBlock>
        {
            new("SHELL", 0.3, 0.05, 0.3, 0.03),
            new("02 - SVPGasoline\n0.987L x 96.700P/L", 0.08, 0.36, 0.35, 0.04),
            new("TOTAL", 0.09, 0.47, 0.20, 0.02),
            new("P95.44", 0.68, 0.47, 0.10, 0.02),
        }).Amount);

    // ---------- FUEL-007: VAT, CHANGE, DISCOUNT are not the total ----------

    [Fact]
    public void Fuel007_PerLine() =>
        Assert.Equal(1000.00m, ReceiptScan.FromLines("PETRON", "TOTAL 1,000.00", "CASH 1,500.00", "CHANGE 500.00", "VAT 107.14").Amount);

    [Fact]
    public void Fuel007_LabelAndValueColumns() =>
        Assert.Equal(1000.00m, ReceiptScan.Run(new List<OcrBlock>
        {
            new("PETRON", 0.3, 0.05, 0.3, 0.03),
            new("TOTAL\nCASH\nCHANGE\nVAT", 0.08, 0.50, 0.20, 0.12),
            new("1,000.00\n1,500.00\n500.00\n107.14", 0.68, 0.50, 0.20, 0.12),
        }).Amount);

    // ---------- FUEL-008: TOTAL and amount in separate blocks, matched by row ----------

    [Fact]
    public void Fuel008_SingleRow() =>
        Assert.Equal(1000.00m, ReceiptScan.Run(new List<OcrBlock>
        {
            new("PETRON", 0.3, 0.05, 0.3, 0.03),
            new("TOTAL", 0.08, 0.55, 0.15, 0.02),
            new("1,000.00", 0.70, 0.55, 0.20, 0.02),
        }).Amount);

    [Fact]
    public void Fuel008_RowAlignmentAmongOtherRows()
    {
        // Each label and each right-aligned value is its own block (labels first, as ML Kit
        // returned them on the emulator). TOTAL's row is y 0.55; CASH and CHANGE are 0.05 away.
        var blocks = new List<OcrBlock>
        {
            new("PETRON", 0.3, 0.05, 0.3, 0.03),
            new("CASH", 0.08, 0.50, 0.12, 0.02),
            new("TOTAL", 0.08, 0.55, 0.15, 0.02),
            new("CHANGE", 0.08, 0.60, 0.17, 0.02),
            new("1,500.00", 0.70, 0.50, 0.20, 0.02),
            new("1,000.00", 0.70, 0.55, 0.20, 0.02),
            new("500.00", 0.75, 0.60, 0.15, 0.02),
        };
        Assert.Equal(1000.00m, ReceiptScan.Run(blocks).Amount);
    }

    // ---------- FUEL-009: liters not detected ----------

    [Fact]
    public void Fuel009_SmudgedLiters_WarningListsLiters()
    {
        var scan = ReceiptScan.FromLines("PETRON", "Date: 10/20/2026", "UNLEADED", "8.2 ~~ 60.72", "TOTAL 500.00");
        Assert.Null(scan.Liters);
        Assert.Contains("Liters/quantity was not confidently detected.", scan.Warning);
    }

    [Fact]
    public void Fuel001_TestData_8234Liters_KeepsThreeDecimals()
    {
        var scan = ReceiptScan.FromLines("PETRON", "Date: 10/20/2026", "XCS 8.234L x 60.72", "TOTAL 500.00");
        Assert.Equal(8.234m, scan.Liters);
        Assert.Equal(500.00m, scan.Amount);
        Assert.Equal("8.234", scan.LitersShown);
    }

    // ---------- FUEL-011: OR number ----------

    [Theory]
    [InlineData("OR No.: 0012345", "0012345", false)]
    [InlineData("SI No. 0045678", "0045678", true)]
    [InlineData("OR# OO12345", "0012345", false)]
    public void Fuel011_OrNumber(string line, string expected, bool uncertain)
    {
        Assert.Equal(expected, FuelReceiptParser.TryParseOrNumber(new[] { "PETRON", line, "TOTAL 500.00" }, out bool u));
        Assert.Equal(uncertain, u);
    }

    [Theory]
    [InlineData("TIN 000-123-456-000")]
    [InlineData("OR No. 000-123-456-000")]
    public void Fuel011_TinShapedValueRejected(string line) =>
        Assert.Null(FuelReceiptParser.TryParseOrNumber(new[] { "PETRON", line, "TOTAL 500.00" }, out _));

    // ---------- FUEL-012: implausible receipt date ----------

    private static string Us(DateTime d) => d.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);

    [Fact]
    public void Fuel012_Year2019_NotDetected()
    {
        var scan = ReceiptScan.FromLines("PETRON", "Date: 03/15/2019", "10.000L", "TOTAL 600.00");
        Assert.Null(scan.Date);
        Assert.Contains("Receipt date was not confidently detected.", scan.Warning);
    }

    [Fact]
    public void Fuel012_TwoDaysAhead_NotDetected()
    {
        var scan = ReceiptScan.FromLines("PETRON", $"Date: {Us(DateTime.Today.AddDays(2))}", "10.000L", "TOTAL 600.00");
        Assert.Null(scan.Date);
        Assert.Contains("Receipt date was not confidently detected.", scan.Warning);
    }

    [Fact]
    public void Fuel012_NoDateAtAll_NotDetected()
    {
        var scan = ReceiptScan.FromLines("PETRON", "10.000L", "TOTAL 600.00");
        Assert.Null(scan.Date);
        Assert.Contains("Receipt date was not confidently detected.", scan.Warning);
    }

    [Fact]
    public void Fuel012_NoDate_MlKitBlocksFromEmulator()
    {
        // The Shell photo cropped below its date line; on the emulator the scan showed "Jan 01, 0001"
        // and fuel_logs/xiRt7gUV1h3SnLHPTDST was saved with receiptTimestamp 0001-01-01.
        var scan = ReceiptScan.Run(ReceiptScan.FromLogcat("mlkit_shell_nodate.txt"));
        Assert.Null(scan.Date);
        Assert.Contains("Receipt date was not confidently detected.", scan.Warning);
    }

    [Fact]
    public void Fuel012_Documents_OneDayAheadIsAccepted() =>
        Assert.Equal(DateTime.Today.AddDays(1), ReceiptScan.FromLines("PETRON", $"Date: {Us(DateTime.Today.AddDays(1))}", "TOTAL 600.00").Date);

    [Fact]
    public void Fuel012_TodaysDate_IsRead() =>
        Assert.Equal(DateTime.Today, ReceiptScan.FromLines("PETRON", $"Date: {Us(DateTime.Today)}", "TOTAL 600.00").Date);
}

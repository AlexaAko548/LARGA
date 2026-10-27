using LARGA.SharedCore;
using static LARGA.SharedCore.Services.FuelReceiptTextParser;

namespace LARGA.Tests.Fuel;

/// <summary>
/// FUEL-001 / FUEL-002 / FUEL-006: the two real receipts, each in three layouts:
///   (a) the exact blocks ML Kit returned on the emulator (Fixtures/Fuel/*.txt),
///   (b) one block per printed line, no bounding boxes,
///   (c) a label column and a right-aligned value column as separate blocks, with realistic boxes.
/// Every assertion is the value PRINTED on the receipt.
/// </summary>
public class RealReceiptTests
{
    // ---------- Petron: Petrogem Petron Service Station, Antipolo ----------

    private const decimal PetronTotal = 1641.40m;
    private const decimal PetronLiters = 31.94m;
    private static readonly DateTime PetronDate = new(2025, 8, 31);

    public static IReadOnlyList<OcrBlock> PetronLines() => new[]
    {
        "PETRON", "Petrogem Petron Service Station", "Dealer: Maria Zenaida F. Junsav", "224 Sumulong Highway Mayamot",
        "Antipolo City", "VAT REG TIN: 483-877-530-000", "POS S/N:GLD3CD2/MIN:16072622225475287", "SALES INVOICE",
        "Date: 08/31/2025 Time: 14:42:13", "S.I.# 0003387130", "Name:", "TIN:", "Address:", "Business Style:",
        "Description Qty. Price Amount", "*XCS (V)", "(P : 07) 31.94 Php56.39 Php1,801.10",
        "Discount amount w/o VAT Php-142.59", "Total (incl. VAT) Php1,641.40", "Zero Rated Sale Php0.00",
        "VAT Exempt Sale Php0.00", "VATable Sales Php1,465.54", "VAT Amount Php175.86",
        "Payment: Store Account Php1,641.40", "Cashier: Veron Gonzales", "Points Awarding: 12.78",
        "Card No: XXXXXXXXXXXXXXXX", "Name: Pa*********y Ta**a",
    }.Select(l => new OcrBlock(l, 0, 0, 0, 0)).ToList();

    public static IReadOnlyList<OcrBlock> PetronColumns() => new List<OcrBlock>
    {
        new("PETRON", 0.37, 0.10, 0.37, 0.05),
        new("Petrogem Petron Service Station\nDealer: Maria Zenaida F. Junsav\n224 Sumulong Highway Mayamot\nAntipolo City", 0.33, 0.15, 0.43, 0.09),
        new("VAT REG TIN: 483-877-530-000", 0.35, 0.25, 0.40, 0.025),
        new("POS S/N:GLD3CD2/MIN:16072622225475287\nSALES INVOICE", 0.25, 0.28, 0.59, 0.057),
        new("Date: 08/31/2025\nS.I.# 0003387130", 0.08, 0.32, 0.25, 0.053),
        new("Time: 14:42:13", 0.63, 0.34, 0.22, 0.024),
        new("Description", 0.06, 0.48, 0.19, 0.036),
        new("Qty.", 0.38, 0.51, 0.07, 0.03),
        new("Price", 0.54, 0.51, 0.08, 0.022),
        new("Amount", 0.69, 0.52, 0.13, 0.025),
        new("*XCS\n(P : 07)\nDiscount amount w/o VAT", 0.04, 0.53, 0.43, 0.09),
        new("31.94", 0.35, 0.57, 0.09, 0.021),
        new("Php56.39", 0.49, 0.575, 0.15, 0.028),
        new("(V)\nPhp1,801.10\nPhp-142.59", 0.67, 0.555, 0.21, 0.08),
        // label column / right-aligned value column
        new("Total (incl. VAT)\nZero Rated Sale\nVAT Exempt Sale\nVATable Sales\nVAT Amount\nPayment: Store Account", 0.02, 0.616, 0.40, 0.16),
        new("Php1,641.40\nPhp0.00\nPhp0.00\nPhp1,465.54\nPhp175.86\nPhp1,641.40", 0.65, 0.62, 0.22, 0.16),
        new("Cashier: Veron Gonzales\nPoints Awarding: 12.78\nCard No: XXXXXXXXXXXXXXXX", 0.0, 0.81, 0.40, 0.10),
    };

    [Fact]
    public void Petron_a_MlKitBlocks()
    {
        var scan = ReceiptScan.Run(ReceiptScan.FromLogcat("mlkit_petron.txt"));
        Assert.Equal(FuelStations.Petron, scan.Vendor);
        Assert.Equal(PetronDate, scan.Date);
        Assert.Equal("0003387130", scan.OrNumber);
        Assert.Equal(PetronTotal, scan.Amount);
        Assert.False(scan.AmountUncertain);
        Assert.Equal(PetronLiters, scan.Liters);
        Assert.Equal(string.Empty, scan.Warning);
    }

    [Fact]
    public void Petron_b_OneBlockPerLine()
    {
        var scan = ReceiptScan.Run(PetronLines());
        Assert.Equal(FuelStations.Petron, scan.Vendor);
        Assert.Equal(PetronDate, scan.Date);
        Assert.Equal("0003387130", scan.OrNumber);
        Assert.Equal(PetronTotal, scan.Amount);
        Assert.Equal(PetronLiters, scan.Liters);
        Assert.Equal(string.Empty, scan.Warning);
    }

    [Fact]
    public void Petron_c_LabelAndValueColumns()
    {
        var scan = ReceiptScan.Run(PetronColumns());
        Assert.Equal(FuelStations.Petron, scan.Vendor);
        Assert.Equal(PetronDate, scan.Date);
        Assert.Equal("0003387130", scan.OrNumber);
        Assert.Equal(PetronTotal, scan.Amount);
        Assert.Equal(PetronLiters, scan.Liters);
    }

    [Fact]
    public void Petron_OrNumber_ComesFromSiLabel_AndIsUncertain()
    {
        var scan = ReceiptScan.Run(ReceiptScan.FromLogcat("mlkit_petron.txt"));
        Assert.Equal("0003387130", scan.OrNumber);
        Assert.True(scan.OrUncertain);
    }

    // ---------- Shell: MH FUELS & MORE INC., Talamban Cebu ----------

    private const decimal ShellTotal = 95.44m;
    private const decimal ShellLiters = 0.987m;
    private static readonly DateTime ShellDate = new(2026, 10, 8);

    public static IReadOnlyList<OcrBlock> ShellLines(bool withFooter = true)
    {
        var lines = new List<string>
        {
            "MH FUELS & MORE INC.", "Owned and Operated By:", "MH FUELS & MORE INC.",
            "VAT Registered TIN: 010-799-035-000", "2nd Floor Talamban Highlands Gov. M Cuenco",
            "Ave. Talamban City of Cebu", "Ship to # 13006291", "MIN: 24050315332035325 PC S/N: PC2KL2B2",
            "10/08/2026 09:41 POS:1 #27 E.S.", "F 000-315617", "*** SERVICE INVOICE ***", "02 - SVPGasoline",
            "0.987L x 96.700P/L VAT P95.44", "Sale Total P95.44", "Cash P95.44", "TOTAL INVOICE P95.44",
            "VATable Sales P85.21", "VAT Amount P10.23", "VAT-Exempt Sales P0.00", "VAT-Zero Rated Sales P0.00",
            "FSC #12", "No Loyalty Points Earned",
        };
        if (withFooter)
        {
            lines.AddRange(new[] { "Download Shell Go+ to get", "our best ever rewards" });
        }
        lines.AddRange(new[] { "Customer Name:", "Addr:", "TIN:", "Business Style:" });
        return lines.Select(l => new OcrBlock(l, 0, 0, 0, 0)).ToList();
    }

    public static IReadOnlyList<OcrBlock> ShellColumns() => new List<OcrBlock>
    {
        new("MH FUELS & MORE INC.\nOwned and Operated By:\nMH FUELS & MORE INC.", 0.25, 0.105, 0.38, 0.064),
        new("VAT Registered TIN: 010-799-035-000\n2nd Floor Talamban Highlands Gov. M Cuenco\nAve. Talamban City of Cebu", 0.07, 0.16, 0.74, 0.07),
        new("Ship to # 13006291", 0.30, 0.224, 0.29, 0.024),
        new("MIN: 24050315332035325 PC S/N: PC2KL2B2\n10/08/2026 09:41\nPOS:1 #27 E.S.\nF 000-315617", 0.08, 0.243, 0.72, 0.06),
        new("*** SERVICE INVOICE ***", 0.24, 0.325, 0.39, 0.017),
        // label column / right-aligned value column
        new("02 - SVPGasoline\n0.987L x 96.700P/L\nSale Total\nCash\nTOTAL INVOICE\nVATable Sales\nVAT Amount\nVAT-Exempt Sales\nVAT-Zero Rated Sales", 0.08, 0.36, 0.42, 0.22),
        new("VAT P95.44\nP95.44\nP95.44\nP95.44\nP85.21\nP10.23\nP0.00\nP0.00", 0.60, 0.379, 0.18, 0.20),
        new("FSC #12", 0.11, 0.60, 0.11, 0.018),
        new("No Loyalty Points Earned", 0.11, 0.633, 0.39, 0.024),
        new("Download Shell Go+ to get\nour best ever rewards", 0.25, 0.665, 0.40, 0.037),
        new("Customer Name:", 0.12, 0.90, 0.22, 0.013),
    };

    [Fact]
    public void Shell_a_MlKitBlocks()
    {
        var scan = ReceiptScan.Run(ReceiptScan.FromLogcat("mlkit_shell.txt"));
        Assert.Equal(FuelStations.Shell, scan.Vendor);
        Assert.Equal(ShellDate, scan.Date);
        Assert.Equal(ShellTotal, scan.Amount);
        Assert.Equal(ShellLiters, scan.Liters);
        Assert.Equal(string.Empty, scan.Warning);
        Assert.Equal("0.987", scan.LitersShown);
    }

    [Fact]
    public void Shell_a_TotalStillRead_WhenVatableValueBlockSitsOnePercentFurtherRight()
    {
        // Same ML Kit blocks; only the "P85.21" (VATable Sales) block is moved 1 % of the width to the
        // right, as a slightly skewed photo would. The total must not depend on that.
        var blocks = ReceiptScan.FromLogcat("mlkit_shell.txt")
            .Select(b => b.Text == "P85.21" ? b with { X = b.X + 0.01 } : b)
            .ToList();
        Assert.Equal(ShellTotal, ReceiptScan.Run(blocks).Amount);
    }

    [Fact]
    public void Shell_b_OneBlockPerLine()
    {
        var scan = ReceiptScan.Run(ShellLines());
        Assert.Equal(FuelStations.Shell, scan.Vendor);
        Assert.Equal(ShellDate, scan.Date);
        Assert.Equal(ShellTotal, scan.Amount);
        Assert.Equal(ShellLiters, scan.Liters);
        Assert.Equal(string.Empty, scan.Warning);
    }

    [Fact]
    public void Shell_c_LabelAndValueColumns()
    {
        var scan = ReceiptScan.Run(ShellColumns());
        Assert.Equal(FuelStations.Shell, scan.Vendor);
        Assert.Equal(ShellDate, scan.Date);
        Assert.Equal(ShellTotal, scan.Amount);   // FUEL-006: must not be the 96.70 unit price
        Assert.Equal(ShellLiters, scan.Liters);
    }

    [Fact]
    public void Shell_WithoutFooter_StationReadFromProduct_a_MlKitBlocks()
    {
        // The header says MH FUELS & MORE INC.; without the "Download Shell Go+" footer the station
        // is still recognised from the product line "02 - SVPGasoline" (Shell V-Power).
        var scan = ReceiptScan.Run(ReceiptScan.FromLogcat("mlkit_shell_nofooter.txt"));
        Assert.Equal(FuelStations.Shell, scan.Vendor);
        Assert.Equal(ShellTotal, scan.Amount);
        Assert.Equal(string.Empty, scan.Warning);
    }

    [Fact]
    public void Shell_WithoutFooter_StationReadFromProduct_b_Lines() =>
        Assert.Equal(FuelStations.Shell, ReceiptScan.Run(ShellLines(withFooter: false)).Vendor);

    // ---------- ML Kit line captures from the fixed build (boxes and tilt per line) ----------

    [Fact]
    public void Petron_Lines_AllFields()
    {
        var scan = ReceiptScan.Run(ReceiptScan.FromLogcat("mlkit_lines_petron.txt"));
        Assert.Equal(FuelStations.Petron, scan.Vendor);
        Assert.Equal(PetronTotal, scan.Amount);
        Assert.False(scan.AmountUncertain);
        Assert.Equal(PetronLiters, scan.Liters);
        Assert.Equal(PetronDate, scan.Date);
        Assert.Equal("0003387130", scan.OrNumber);
        Assert.Equal(string.Empty, scan.Warning);
    }

    [Theory]
    [InlineData("mlkit_lines_shell.txt")]
    [InlineData("mlkit_lines_shell_nofooter.txt")]
    public void Shell_Lines_AllFields(string fixture)
    {
        var scan = ReceiptScan.Run(ReceiptScan.FromLogcat(fixture));
        Assert.Equal(FuelStations.Shell, scan.Vendor);
        Assert.Equal(ShellTotal, scan.Amount);
        Assert.False(scan.AmountUncertain);
        Assert.Equal(ShellLiters, scan.Liters);
        Assert.Equal("0.987", scan.LitersShown);
        Assert.Equal(ShellDate, scan.Date);
        Assert.Equal(string.Empty, scan.Warning);
    }

    [Fact]
    public void Shell_Lines_NoDate()
    {
        var scan = ReceiptScan.Run(ReceiptScan.FromLogcat("mlkit_lines_shell_nodate.txt"));
        Assert.Null(scan.Date);
        Assert.Equal("OCR uncertain: Receipt date was not confidently detected. Please review/edit before submit.", scan.Warning);
        Assert.Equal(ShellTotal, scan.Amount);
    }

    [Fact]
    public void Petron_Lines_TotalFoundByRow_EvenWithoutVatBreakdown()
    {
        // Drop the VATable/VAT amounts so only the "Total (incl. VAT)" row pairing (with the
        // photo's tilt) can find the total.
        var lines = ReceiptScan.FromLogcat("mlkit_lines_petron.txt")
            .Where(l => l.Text is not ("Php1,465.54" or "Php175.86"))
            .ToList();
        var scan = ReceiptScan.Run(lines);
        Assert.Equal(PetronTotal, scan.Amount);
        Assert.False(scan.AmountUncertain);
    }

    [Fact]
    public void Petron_a_BlockLevelCapture_TotalConfirmedByVat()
    {
        var scan = ReceiptScan.Run(ReceiptScan.BlocksFromLogcat("mlkit_petron.txt"));
        Assert.Equal(PetronTotal, scan.Amount);
        Assert.False(scan.AmountUncertain);
        Assert.Equal(PetronLiters, scan.Liters);
    }

    [Fact]
    public void Shell_InvoiceNumber_F000_315617_IsNotRead()
    {
        // Documents current behavior: "F 000-315617" has no OR/SI label, so no OR No. is filled in.
        Assert.Null(ReceiptScan.Run(ReceiptScan.FromLogcat("mlkit_shell.txt")).OrNumber);
        Assert.Null(ReceiptScan.Run(ShellLines()).OrNumber);
    }
}

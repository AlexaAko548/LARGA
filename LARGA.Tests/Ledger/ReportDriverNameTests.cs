using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Services;

namespace LARGA.Tests.Ledger;

/// <summary>BETA-098: the Financial Summary and Fuel &amp; Mileage exports name the driver; the raw ShiftId isn't shown.</summary>
public class ReportDriverNameTests
{
    private static readonly Dictionary<string, string> Names = FleetReportingService.DriverNamesById(new[]
    {
        new UserProfile { UserId = "d1", FullName = "Juan Dela Cruz" },
        new UserProfile { UserId = "d2", FullName = "Maria Santos" },
    });

    [Fact]
    public void BoundaryPayments_DriverNameInsteadOfShiftId()
    {
        var table = FleetReportingService.BoundaryPaymentsTable(new[]
        {
            new BoundaryPayment { ShiftId = "s1", DriverId = "d1", AmountPaid = 800m, Timestamp = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) },
            new BoundaryPayment { ShiftId = "s2", DriverId = "gone", Timestamp = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc) },
            new BoundaryPayment { ShiftId = "s3", DriverId = null, Timestamp = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc) },
        }, Names);

        Assert.DoesNotContain("ShiftId", table.Headers);
        Assert.Equal("DriverName", table.Headers[1]);
        Assert.Equal("Juan Dela Cruz", table.Rows[0][1]);
        Assert.Equal("Unknown driver", table.Rows[1][1]);
        Assert.Equal(string.Empty, table.Rows[2][1]);
        Assert.DoesNotContain(table.Rows, row => row.Contains("s1") || row.Contains("s2") || row.Contains("s3"));
        Assert.Equal(new[] { "ExpectedBoundary", "LateFees", "AmountPaid" }, table.MoneyColumns.Order().Select(i => table.Headers[i]));
    }

    [Fact]
    public void FuelLogs_DriverFromLog_ElseFromItsShift()
    {
        var shifts = new[] { new ShiftLog { DocumentId = "doc-s2", ShiftId = "s2", DriverId = "d2" } };
        var table = FleetReportingService.FuelLogsTable(new[]
        {
            new FuelLog { ShiftId = "s1", DriverId = "d1", ReceiptTimestamp = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) },
            new FuelLog { ShiftId = "s2", DriverId = "", ReceiptTimestamp = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc) },
            new FuelLog { ShiftId = "UNKNOWN_SHIFT", DriverId = "", ReceiptTimestamp = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc) },
        }, Names, shifts);

        Assert.DoesNotContain("ShiftId", table.Headers);
        Assert.Equal("DriverName", table.Headers[1]);
        Assert.Equal("Juan Dela Cruz", table.Rows[0][1]);
        Assert.Equal("Maria Santos", table.Rows[1][1]);
        Assert.Equal(string.Empty, table.Rows[2][1]);
        Assert.Equal("FuelCost", table.Headers[table.MoneyColumns.Single()]);
        Assert.All(table.Rows, row => Assert.Equal(table.Headers.Count, row.Length));
    }
}

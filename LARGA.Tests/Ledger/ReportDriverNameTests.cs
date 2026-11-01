using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Services;

namespace LARGA.Tests.Ledger;

/// <summary>BETA-098: the Financial Summary and Fuel &amp; Mileage exports name the driver beside the raw ShiftId.</summary>
public class ReportDriverNameTests
{
    private static readonly Dictionary<string, string> Names = FleetReportingService.DriverNamesById(new[]
    {
        new UserProfile { UserId = "d1", FullName = "Juan Dela Cruz" },
        new UserProfile { UserId = "d2", FullName = "Maria Santos" },
    });

    [Fact]
    public void BoundaryPayments_DriverNameNextToShiftId()
    {
        var table = FleetReportingService.BoundaryPaymentsTable(new[]
        {
            new BoundaryPayment { ShiftId = "s1", DriverId = "d1", AmountPaid = 800m, Timestamp = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) },
            new BoundaryPayment { ShiftId = "s2", DriverId = "gone", Timestamp = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc) },
            new BoundaryPayment { ShiftId = "s3", DriverId = null, Timestamp = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc) },
        }, Names);

        Assert.Equal("ShiftId", table.Headers[1]);
        Assert.Equal("DriverName", table.Headers[2]);
        Assert.Equal(new object?[] { "s1", "Juan Dela Cruz" }, table.Rows[0][1..3]);
        Assert.Equal("Unknown driver", table.Rows[1][2]);
        Assert.Equal(string.Empty, table.Rows[2][2]);
        // The peso columns moved one to the right with it.
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

        Assert.Equal("ShiftId", table.Headers[1]);
        Assert.Equal("DriverName", table.Headers[2]);
        Assert.Equal("Juan Dela Cruz", table.Rows[0][2]);
        Assert.Equal("Maria Santos", table.Rows[1][2]);
        Assert.Equal(string.Empty, table.Rows[2][2]);
        Assert.Equal("FuelCost", table.Headers[table.MoneyColumns.Single()]);
        Assert.All(table.Rows, row => Assert.Equal(table.Headers.Count, row.Length));
    }
}

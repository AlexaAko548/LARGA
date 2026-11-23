using LARGA.Shared.Models.Entities;
using LARGA.SharedCore;

namespace LARGA.Tests.Maintenance;

/// <summary>INV-010: Normal / Low Stock / Critical (SparePartStockRules), shared by the Inventory page and the LowStock alert.</summary>
public class SparePartStockRulesTests
{
    [Theory]
    [InlineData(11, 10, "Normal")]
    [InlineData(10, 10, "Low Stock")]
    [InlineData(6, 10, "Low Stock")]
    [InlineData(5, 10, "Critical")] // half the minimum
    [InlineData(3, 5, "Low Stock")]
    [InlineData(2, 5, "Critical")]  // 5 / 2 = 2
    [InlineData(1, 1, "Low Stock")]
    [InlineData(0, 1, "Critical")]
    [InlineData(0, 0, "Critical")]  // out of stock is always Critical
    [InlineData(1, 0, "Normal")]
    public void StatusOf_MatchesTheInventoryPage(int stock, int minimum, string expected) =>
        Assert.Equal(expected, SparePartStockRules.StatusOf(new SparePart { StockQuantity = stock, ReorderLevel = minimum }));
}

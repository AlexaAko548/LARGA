using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore;

/// <summary>
/// INV-010: a spare part's stock status - one rule for ManagerWeb's Inventory page and the LowStock
/// alert (AlertService), so the two never disagree. Critical when out of stock or at or below half its
/// minimum (ReorderLevel, halved with whole-number division: a minimum of 5 is Critical at 2); Low Stock
/// at or below the minimum; Normal above it.
/// </summary>
public static class SparePartStockRules
{
    public const string Normal = "Normal";
    public const string LowStock = "Low Stock";
    public const string Critical = "Critical";

    public static string StatusOf(int stockQuantity, int reorderLevel)
    {
        if (stockQuantity <= 0 || stockQuantity <= reorderLevel / 2)
        {
            return Critical;
        }

        return stockQuantity <= reorderLevel ? LowStock : Normal;
    }

    public static string StatusOf(SparePart part) => StatusOf(part.StockQuantity, part.ReorderLevel);
}

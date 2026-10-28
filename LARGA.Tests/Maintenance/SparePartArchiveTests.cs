using LARGA.Shared.Models.Entities;
using LARGA.SharedCore;

namespace LARGA.Tests.Maintenance;

/// <summary>SRS REQ-4.5-1: spare parts are archived, never deleted (SparePartArchiveRules).</summary>
public class SparePartArchiveTests
{
    private static SparePart Part(string name, int stock, bool archived = false) => new()
    {
        PartId = name.ToLowerInvariant(),
        PartName = name,
        StockQuantity = stock,
        ReorderLevel = 2,
        Unit = "pcs",
        IsArchived = archived,
    };

    [Fact]
    public void Active_LeavesArchivedPartsOut()
    {
        var parts = new[] { Part("Brake Pads", 4), Part("Old Filter", 0, archived: true), Part("Spark Plug", 9) };
        Assert.Equal(new[] { "Brake Pads", "Spark Plug" }, SparePartArchiveRules.Active(parts).Select(p => p.PartName));
    }

    [Fact]
    public void ConfirmMessage_WarnsWhenStockIsLeft()
    {
        string withStock = SparePartArchiveRules.ArchiveConfirmMessage(Part("Brake Pads", 4));
        Assert.Contains("Warning: 4 pcs are still in stock", withStock);

        string empty = SparePartArchiveRules.ArchiveConfirmMessage(Part("Brake Pads", 0));
        Assert.DoesNotContain("Warning", empty);
        Assert.Contains("usage history is kept", empty);
    }

    [Fact]
    public void ChangesToAnArchivedPart_AreRefusedWithAReadableMessage()
    {
        var ex = new PartArchivedException(Part("Old Filter", 0, archived: true));
        Assert.Equal("'Old Filter' is archived - restore it first.", ex.Message);
    }

    [Fact]
    public void AuditActions()
    {
        Assert.Equal("InventoryPartArchived", SparePartArchiveRules.ArchivedAction);
        Assert.Equal("InventoryPartRestored", SparePartArchiveRules.RestoredAction);
    }
}

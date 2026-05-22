using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class PalletLabelPrintSelectionServiceTests
{
    [Fact]
    public void BuildGroups_GroupsRowsByItemName()
    {
        var rows = new[]
        {
            CreateRow(1, "Горчица Печагин, 1 кг", "HU-0000478", ProductionPalletStatus.Filled),
            CreateRow(2, "Горчица Печагин, 1 кг", "HU-0000479", ProductionPalletStatus.Filled),
            CreateRow(3, "Горчица Печагин, 1 кг", "HU-0000485", ProductionPalletStatus.Planned),
            CreateRow(4, "Кетчуп", "HU-0000490", ProductionPalletStatus.Planned)
        };

        var groups = PalletLabelPrintSelectionService.BuildGroups(rows);

        Assert.Equal(2, groups.Count);
        Assert.Equal("Горчица Печагин, 1 кг", groups[0].ItemName);
        Assert.Equal(3, groups[0].Rows.Count);
        Assert.Equal("Кетчуп", groups[1].ItemName);
    }

    [Fact]
    public void ResolveDefaultSelectedPalletIds_SelectsOnlyPlannedRows()
    {
        var rows = new[]
        {
            CreateRow(1, "Товар", "HU-0000478", ProductionPalletStatus.Filled),
            CreateRow(2, "Товар", "HU-0000479", ProductionPalletStatus.Printed),
            CreateRow(3, "Товар", "HU-0000485", ProductionPalletStatus.Planned),
            CreateRow(4, "Товар", "HU-0000486", ProductionPalletStatus.Planned)
        };

        var selected = PalletLabelPrintSelectionService.ResolveDefaultSelectedPalletIds(rows);

        Assert.Equal(new[] { 3L, 4L }, selected);
    }

    private static ProductionPalletPrintRow CreateRow(long palletId, string itemName, string huCode, string status)
    {
        return new ProductionPalletPrintRow
        {
            PalletId = palletId,
            OrderId = 72,
            OrderRef = "072",
            HuCode = huCode,
            ItemName = itemName,
            Qty = 600,
            Status = status
        };
    }
}

using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderLineCanonicalPresentationTests
{
    [Fact]
    public void ResolveProductionHuCodesDisplay_UsesArrayWhenDisplayMissing()
    {
        var display = OrderLineCanonicalPresentation.ResolveProductionHuCodesDisplay(
            null,
            ["HU-0000540", "HU-0000523", "HU-0000524"]);

        Assert.Equal("HU-0000523, HU-0000524, HU-0000540", display);
    }

    [Fact]
    public void ApplyPersistedLine_UpdatesProductionHuCodes_ForWpfReload()
    {
        var target = new OrderLineView
        {
            Id = 191,
            OrderId = 86,
            ItemId = 6,
            ItemName = "Item",
            QtyOrdered = 1200,
            ProductionHuCodes = "HU-OLD-1, HU-OLD-2",
            QtyProduced = 1200,
            FilledPalletQty = 1200
        };
        var source = new OrderLineView
        {
            Id = 191,
            OrderId = 86,
            ItemId = 6,
            ItemName = "Item",
            QtyOrdered = 2400,
            ProductionHuCodes = "HU-0000523, HU-0000524, HU-0000540, HU-0000542",
            QtyProduced = 1200,
            QtyRemaining = 1200,
            FilledPalletQty = 1200,
            PlannedPalletQty = 1200,
            PlannedPalletCount = 2,
            FilledPalletCount = 2
        };

        var changed = false;
        target.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(OrderLineView.ProductionHuCodes))
            {
                changed = true;
            }
        };

        OrderLineCanonicalPresentation.ApplyPersistedLine(target, source, OrderType.Internal);

        Assert.True(changed);
        Assert.Equal(2400, target.QtyOrdered, 3);
        Assert.Contains("HU-0000540", target.ProductionHuCodes, StringComparison.Ordinal);
        Assert.Contains("HU-0000542", target.ProductionHuCodes, StringComparison.Ordinal);
        Assert.Equal(1200, target.QtyRemaining, 3);
    }
}

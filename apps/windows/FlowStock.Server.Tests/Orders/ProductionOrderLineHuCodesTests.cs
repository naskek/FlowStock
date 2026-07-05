using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.ProductionPallets;

namespace FlowStock.Server.Tests.Orders;

public sealed class ProductionOrderLineHuCodesTests
{
    [Fact]
    public void BuildByOrder_CustomerOrder083Like_ShowsPlannedHuPerLine()
    {
        var harness = CreateCustomerTwoLineHarness();
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(83);
        var pallets = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .OrderBy(pallet => pallet.OrderLineId)
            .ToArray();

        var huByLine = ProductionOrderLineHuCodes.BuildByOrder(harness.Store, 83);

        Assert.Equal(2, huByLine.Count);
        Assert.Equal(new[] { pallets[0].HuCode }, huByLine[222]);
        Assert.Equal(new[] { pallets[1].HuCode }, huByLine[223]);
    }

    [Fact]
    public void BuildByOrder_CustomerWithoutProductionPallets_UsesBoundHuOnly()
    {
        var harness = CreateCustomerTwoLineHarness();
        harness.SeedOrderReceiptPlanLines(83,
        [
            new OrderReceiptPlanLine
            {
                Id = 501,
                OrderId = 83,
                OrderLineId = 222,
                ItemId = 29,
                QtyPlanned = 120,
                ToHu = "HU-BOUND-1",
                SortOrder = 1
            }
        ]);
        harness.SeedLedgerEntry(docId: 900, itemId: 29, locationId: 1, qtyDelta: 120, huCode: "HU-BOUND-1");

        var huByLine = ProductionOrderLineHuCodes.BuildByOrder(harness.Store, 83);

        Assert.Single(huByLine);
        Assert.Equal(new[] { "HU-BOUND-1" }, huByLine[222]);
    }

    [Fact]
    public void OrderLineHuDisplayRows_ApiOnlyReload_DropsProductionEntries_UntilStoreEnrichment()
    {
        var harness = CreateCustomerTwoLineHarness();
        MixedPlanTestSeed.SeedPlan(harness, 900, 83, "083",
            MixedPlanTestSeed.Mixed("HU-0000083", MixedPlanTestSeed.C(222, 29, 120), MixedPlanTestSeed.C(223, 13, 80)));
        var display = ProductionOrderLineHuCodes.BuildProductionDisplayByOrder(harness.Store, 83);
        var palletHu = display[222].Single().HuCode;

        var canonicalLine = new OrderLineView
        {
            Id = 222,
            ProductionHuDisplayEntries = display[222]
        };
        Assert.NotEmpty(canonicalLine.HuDisplayRows);
        Assert.Contains(canonicalLine.HuDisplayRows, row => row.HuCode == palletHu);

        var apiOnlyLine = new OrderLineView
        {
            Id = 222,
            ProductionHuCodes = palletHu
        };
        Assert.Empty(apiOnlyLine.HuDisplayRows);

        apiOnlyLine.ProductionHuDisplayEntries = display[222];
        Assert.NotEmpty(apiOnlyLine.HuDisplayRows);
        Assert.Contains(apiOnlyLine.HuDisplayRows, row => row.HuCode == palletHu);

        var mixedLine = new OrderLineView
        {
            Id = 223,
            ProductionHuDisplayEntries = display[223]
        };
        Assert.Contains(mixedLine.HuDisplayRows, row => row.HuCode == palletHu);
    }

    [Fact]
    public void BuildProductionDisplayByOrder_PartialMixedHu_UsesComponentLineStatus()
    {
        var harness = CreateCustomerTwoLineHarness();
        var plan = MixedPlanTestSeed.SeedPlan(harness, 900, 83, "083",
            MixedPlanTestSeed.Mixed("HU-0000083", MixedPlanTestSeed.C(222, 29, 120), MixedPlanTestSeed.C(223, 13, 80)));
        var pallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId));
        var filledComponent = pallet.Lines.Single(line => line.OrderLineId == 222);

        harness.Store.MarkProductionPalletComponentsFilled(pallet.Id, [filledComponent.Id], new DateTime(2026, 6, 8, 12, 0, 0));

        var display = ProductionOrderLineHuCodes.BuildProductionDisplayByOrder(harness.Store, 83);

        var filledEntry = Assert.Single(display[222]);
        Assert.Equal(pallet.HuCode, filledEntry.HuCode);
        Assert.Equal("наполнено", filledEntry.Label);
        Assert.Equal(120, filledEntry.Qty, 3);
        Assert.Equal("/ 120", filledEntry.FateSuffix);
        var filledLineView = new OrderLineView { ProductionHuDisplayEntries = display[222] };
        Assert.Contains(filledLineView.HuDisplayRows, row => row.Label == "наполнено");

        var waitingEntry = Assert.Single(display[223]);
        Assert.Equal(pallet.HuCode, waitingEntry.HuCode);
        Assert.Equal("ожидает", waitingEntry.Label);
        Assert.Equal(0, waitingEntry.Qty, 3);
        Assert.Equal("/ 80", waitingEntry.FateSuffix);
    }

    [Fact]
    public void BuildProductionDisplayByOrder_MixedPlannedHu_ExposesComponentQtyTotalAndMixedFlag()
    {
        var harness = CreateCustomerTwoLineHarness();
        MixedPlanTestSeed.SeedPlan(harness, 900, 83, "083",
            MixedPlanTestSeed.Mixed("HU-0000083", MixedPlanTestSeed.C(222, 29, 120), MixedPlanTestSeed.C(223, 13, 80)));

        var display = ProductionOrderLineHuCodes.BuildProductionDisplayByOrder(harness.Store, 83);

        var entry222 = Assert.Single(display[222]);
        Assert.Equal("HU-0000083", entry222.HuCode);
        Assert.Equal(120, entry222.Qty, 3);
        Assert.Equal(200, entry222.HuTotalQty, 3);
        Assert.True(entry222.IsMixed);

        var entry223 = Assert.Single(display[223]);
        Assert.Equal(80, entry223.Qty, 3);
        Assert.Equal(200, entry223.HuTotalQty, 3);
        Assert.True(entry223.IsMixed);

        var row222 = Assert.Single(new OrderLineView { ProductionHuDisplayEntries = display[222] }.HuDisplayRows);
        Assert.Equal("HU-0000083 · план · 120 из 200 · МИКС", row222.DisplayText);
        var row223 = Assert.Single(new OrderLineView { ProductionHuDisplayEntries = display[223] }.HuDisplayRows);
        Assert.Equal("HU-0000083 · план · 80 из 200 · МИКС", row223.DisplayText);
    }

    private static CloseDocumentHarness CreateCustomerTwoLineHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item { Id = 29, Name = "Товар A", BaseUom = "шт", MaxQtyPerHu = 600 });
        harness.SeedItem(new Item { Id = 13, Name = "Товар B", BaseUom = "шт", MaxQtyPerHu = 600 });
        harness.SeedOrder(new Order
        {
            Id = 83,
            OrderRef = "083",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 22, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine { Id = 222, OrderId = 83, ItemId = 29, QtyOrdered = 120 });
        harness.SeedOrderLine(new OrderLine { Id = 223, OrderId = 83, ItemId = 13, QtyOrdered = 80 });
        return harness;
    }

}

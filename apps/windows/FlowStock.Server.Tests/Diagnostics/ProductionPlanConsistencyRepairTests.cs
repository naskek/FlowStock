using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Diagnostics;

public sealed class ProductionPlanConsistencyRepairTests
{
    [Fact]
    public void Repair_DryRun_ReturnsPlanWithoutMutations()
    {
        var harness = SeedRepairScenario();
        var order067QtyBefore = harness.GetOrderLines(67).Single(line => line.ItemId == 6).QtyOrdered;
        var ledgerCountBefore = harness.LedgerEntries.Count;

        var result = new ProductionPlanConsistencyRepairService(harness.Store)
            .Repair(ProductionPlanConsistencyRepairService.Repair067072MustardMode, apply: false);

        Assert.True(result.Ok);
        Assert.False(result.Applied);
        Assert.Contains(result.Steps, step => step.Action == "restore_order_line_qty");
        Assert.Contains(result.Steps, step => step.Action == "mark_pallet_filled");
        Assert.Contains(result.Steps, step => step.Action == "add_ledger");
        Assert.Contains(result.Steps, step => step.Action == "cancel_empty_pallet");
        Assert.Contains(result.Steps, step => step.Action == "keep_pallet" && step.Target == "HU-0000478");
        Assert.Equal(order067QtyBefore, harness.GetOrderLines(67).Single(line => line.ItemId == 6).QtyOrdered);
        Assert.Equal(ledgerCountBefore, harness.LedgerEntries.Count);
    }

    [Fact]
    public void Repair_Apply_CreatesLedgerOnlyForMissingFilledPallets()
    {
        var harness = SeedRepairScenario();

        var first = new ProductionPlanConsistencyRepairService(harness.Store)
            .Repair(ProductionPlanConsistencyRepairService.Repair067072MustardMode, apply: true);
        Assert.True(first.Ok);

        var ledger0462 = harness.LedgerEntries.Single(entry => entry.HuCode == "HU-0000462");
        var ledger0463 = harness.LedgerEntries.Single(entry => entry.HuCode == "HU-0000463");
        Assert.Equal(600, ledger0462.QtyDelta);
        Assert.Equal(600, ledger0463.QtyDelta);
        Assert.Single(harness.LedgerEntries, entry => entry.HuCode == "HU-0000478");
        Assert.Single(harness.LedgerEntries, entry => entry.HuCode == "HU-0000479");
        Assert.DoesNotContain(harness.LedgerEntries, entry => entry.HuCode == "HU-0000476");
        Assert.DoesNotContain(harness.LedgerEntries, entry => entry.HuCode == "HU-0000477");

        var ledgerCountAfterFirst = harness.LedgerEntries.Count;
        var second = new ProductionPlanConsistencyRepairService(harness.Store)
            .Repair(ProductionPlanConsistencyRepairService.Repair067072MustardMode, apply: true);
        Assert.True(second.Ok);
        Assert.Equal(ledgerCountAfterFirst, harness.LedgerEntries.Count);
    }

    [Fact]
    public void Repair_Apply_CancelsEmptyPrintedPalletsOn072()
    {
        var harness = SeedRepairScenario();

        new ProductionPlanConsistencyRepairService(harness.Store)
            .Repair(ProductionPlanConsistencyRepairService.Repair067072MustardMode, apply: true);

        var pallets072 = harness.Store.GetProductionPalletsByDoc(181);
        var pallet476 = pallets072.Single(pallet => pallet.HuCode == "HU-0000476");
        var pallet477 = pallets072.Single(pallet => pallet.HuCode == "HU-0000477");
        var pallet478 = pallets072.Single(pallet => pallet.HuCode == "HU-0000478");
        var pallet479 = pallets072.Single(pallet => pallet.HuCode == "HU-0000479");

        Assert.Equal(ProductionPalletStatus.Cancelled, pallet476.Status);
        Assert.Equal(ProductionPalletStatus.Cancelled, pallet477.Status);
        Assert.Equal(ProductionPalletStatus.Filled, pallet478.Status);
        Assert.Equal(ProductionPalletStatus.Filled, pallet479.Status);
    }

    [Fact]
    public void Repair_Apply_AssignsMarkingCodesToReceiptLines()
    {
        var harness = SeedRepairScenario();

        new ProductionPlanConsistencyRepairService(harness.Store)
            .Repair(ProductionPlanConsistencyRepairService.Repair067072MustardMode, apply: true);

        var applied = harness.MarkingCodes
            .Where(code => code.ReceiptDocId == 171)
            .ToArray();
        Assert.Equal(1200, applied.Length);
        Assert.Equal(600, applied.Count(code => code.ReceiptLineId == 17101));
        Assert.Equal(600, applied.Count(code => code.ReceiptLineId == 17102));
    }

    [Fact]
    public void Repair_Apply_ClearsBlockingDiagnosticsFor067And072()
    {
        var harness = SeedRepairScenario();

        var result = new ProductionPlanConsistencyRepairService(harness.Store)
            .Repair(ProductionPlanConsistencyRepairService.Repair067072MustardMode, apply: true);

        Assert.True(result.Ok);
        Assert.DoesNotContain(
            result.DiagnosticsAfter,
            item => item.OrderId == 67 && item.ProblemCode == ProductionPlanConsistencyProblemCode.OrderZeroButPalletsExist);
        Assert.DoesNotContain(
            result.DiagnosticsAfter,
            item => item.OrderId == 72 && item.ProblemCode == ProductionPlanConsistencyProblemCode.PalletsExceedOrderQty);
    }

    private static CloseDocumentHarness SeedRepairScenario()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 6, Name = "Горчица", BaseUom = "шт", MaxQtyPerHu = 600, IsActive = true });
        harness.SeedLocation(new Location { Id = 10, Code = "A1", Name = "Производство" });

        harness.SeedOrder(new Order
        {
            Id = 67,
            OrderRef = "067",
            Type = OrderType.Internal,
            Status = OrderStatus.Shipped,
            CreatedAt = new DateTime(2026, 5, 1)
        });
        harness.SeedOrderLine(new OrderLine { Id = 6701, OrderId = 67, ItemId = 6, QtyOrdered = 0 });

        harness.SeedOrder(new Order
        {
            Id = 72,
            OrderRef = "072",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 2)
        });
        harness.SeedOrderLine(new OrderLine { Id = 7201, OrderId = 72, ItemId = 6, QtyOrdered = 1200 });

        SeedPrdPallet(
            harness,
            orderId: 67,
            orderLineId: 6701,
            prdDocId: 171,
            docRef: "PRD-2026-000163",
            huCodes: ["HU-0000462", "HU-0000463"],
            palletStatus: ProductionPalletStatus.Printed,
            seedLedger: false);

        SeedPrdPallet(
            harness,
            orderId: 72,
            orderLineId: 7201,
            prdDocId: 181,
            docRef: "PRD-2026-000181",
            huCodes: ["HU-0000476", "HU-0000477", "HU-0000478", "HU-0000479"],
            palletStatus: ProductionPalletStatus.Printed,
            seedLedger: false);

        var palletsBeforeFill = harness.Store.GetProductionPalletsByDoc(181);
        var filled478 = palletsBeforeFill.Single(pallet => pallet.HuCode == "HU-0000478");
        var filled479 = palletsBeforeFill.Single(pallet => pallet.HuCode == "HU-0000479");
        harness.Store.MarkProductionPalletFilled(filled478.Id, DateTime.UtcNow, "seed");
        harness.Store.MarkProductionPalletFilled(filled479.Id, DateTime.UtcNow, "seed");
        harness.SeedLedgerEntry(181, 6, 10, 600, "HU-0000478");
        harness.SeedLedgerEntry(181, 6, 10, 600, "HU-0000479");

        var markingOrderId = Guid.NewGuid();
        harness.SeedMarkingOrder(new MarkingOrder
        {
            Id = markingOrderId,
            OrderId = 67,
            SourceOrderId = 67,
            ItemId = 6,
            RequestedQuantity = 1200,
            RequestNumber = "MO-067",
            Status = MarkingOrderStatus.CodesBound,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        harness.SeedMarkingCodes(markingOrderId, count: 1200);

        return harness;
    }

    private static void SeedPrdPallet(
        CloseDocumentHarness harness,
        long orderId,
        long orderLineId,
        long prdDocId,
        string docRef,
        IReadOnlyList<string> huCodes,
        string palletStatus,
        bool seedLedger)
    {
        harness.SeedDoc(new Doc
        {
            Id = prdDocId,
            DocRef = docRef,
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = orderId,
            OrderRef = orderId.ToString("000"),
            CreatedAt = new DateTime(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc)
        });

        for (var index = 0; index < huCodes.Count; index++)
        {
            var huCode = huCodes[index];
            var docLineId = prdDocId * 100 + index + 1;
            var palletId = prdDocId * 10 + index + 1;
            harness.SeedLine(new DocLine
            {
                Id = docLineId,
                DocId = prdDocId,
                OrderLineId = orderLineId,
                ItemId = 6,
                Qty = 600,
                ToLocationId = 10,
                ToHu = huCode,
                PackSingleHu = true
            });
            harness.SeedProductionPallet(new ProductionPallet
            {
                Id = palletId,
                PrdDocId = prdDocId,
                DocLineId = docLineId,
                OrderId = orderId,
                OrderLineId = orderLineId,
                ItemId = 6,
                HuCode = huCode,
                PlannedQty = 600,
                ToLocationId = 10,
                Status = palletStatus,
                PalletNo = index + 1,
                PalletCount = huCodes.Count,
                CreatedAt = new DateTime(2026, 5, 1, 11, 5, 0, DateTimeKind.Utc),
                Lines = new[]
                {
                    new ProductionPalletComponentLine
                    {
                        Id = palletId * 100,
                        ProductionPalletId = palletId,
                        DocLineId = docLineId,
                        OrderLineId = orderLineId,
                        ItemId = 6,
                        PlannedQty = 600,
                        FilledQty = string.Equals(palletStatus, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase) ? 600 : 0,
                        CreatedAt = new DateTime(2026, 5, 1, 11, 5, 0, DateTimeKind.Utc)
                    }
                }
            });

            if (seedLedger)
            {
                harness.SeedLedgerEntry(prdDocId, 6, 10, 600, huCode);
            }
        }
    }
}

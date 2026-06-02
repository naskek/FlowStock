using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletServiceTests
{
    [Fact]
    public void GetFillingOrders_DoesNotReturnOrderWithoutPreparedPallets()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);

        Assert.Empty(service.GetFillingOrders());
    }

    [Fact]
    public void PlanOrder_CreatesProductionPalletsWithServerGeneratedHus_AndNoLedger()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10);

        Assert.Equal(10, result.OrderId);
        Assert.Equal("056", result.OrderRef);
        Assert.StartsWith("PRD-", result.PrdDocRef, StringComparison.Ordinal);
        Assert.Equal(2, result.Summary.PlannedPalletCount);
        Assert.Equal(1200, result.Summary.PlannedQty);
        Assert.Equal(0, result.Summary.FilledPalletCount);
        Assert.Equal(1200, result.Summary.RemainingQty);
        Assert.Empty(harness.LedgerEntries);
        var pallets = harness.Store.GetProductionPalletsByDoc(result.PrdDocId);
        Assert.Equal(2, pallets.Count);
        Assert.All(pallets, pallet => Assert.Matches("^HU-[0-9]{7}$", pallet.HuCode));
        Assert.Equal(2, pallets.Select(pallet => pallet.HuCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Single(service.GetFillingOrders());
    }

    [Fact]
    public void CancelOrderPlan_DeletesEmptyDraftPrd()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);

        var plan = service.PlanOrder(10);
        service.CancelOrderPlan(10);

        var prdAfterCancel = harness.Store.GetDoc(plan.PrdDocId);
        Assert.Null(prdAfterCancel);
        Assert.False(harness.Store.HasProductionPallets(plan.PrdDocId));
        Assert.Empty(harness.Store.GetDocLines(plan.PrdDocId));
    }

    [Fact]
    public void PlanOrder_IsIdempotent()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);

        var first = service.PlanOrder(10);
        var firstHuCodes = harness.Store.GetProductionPalletsByDoc(first.PrdDocId).Select(pallet => pallet.HuCode).ToArray();
        var second = service.PlanOrder(10);
        var secondHuCodes = harness.Store.GetProductionPalletsByDoc(second.PrdDocId).Select(pallet => pallet.HuCode).ToArray();

        Assert.Equal(first.PrdDocId, second.PrdDocId);
        Assert.Equal(first.PrdDocRef, second.PrdDocRef);
        Assert.False(first.WasExisting);
        Assert.True(second.WasExisting);
        Assert.Equal(1, harness.Store.GetDocsByOrder(10).Count(doc => doc.Type == DocType.ProductionReceipt));
        Assert.Equal(2, harness.Store.GetDocLines(first.PrdDocId).Count);
        Assert.Equal(2, harness.Store.GetProductionPalletsByDoc(first.PrdDocId).Count);
        Assert.Equal(2, secondHuCodes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(firstHuCodes, secondHuCodes);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void PlanOrder_CustomerOrderWithBoundHu_PlansOnlyUncoveredLine()
    {
        var harness = CreateCustomerPlanningHarness(
            (101, 100, 378),
            (102, 200, 1824),
            (103, 300, 1200),
            (104, 400, 1134),
            (105, 500, 3648));
        SeedCustomerBoundHu(
            harness,
            (1, 101, 100, 378, "HU-900101"),
            (2, 102, 200, 1824, "HU-900102"),
            (3, 103, 300, 1200, "HU-900103"),
            (4, 105, 500, 3648, "HU-900105"));
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10);

        Assert.Equal(1134, result.Summary.PlannedQty);
        Assert.Equal(2, result.Summary.PlannedPalletCount);
        var pallets = harness.Store.GetProductionPalletsByDoc(result.PrdDocId);
        Assert.Equal(2, pallets.Count);
        Assert.All(pallets, pallet => Assert.Equal(104, pallet.OrderLineId));
        Assert.Equal(1134, pallets.Sum(pallet => pallet.PlannedQty));
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void PlanOrder_CustomerLinePartiallyCoveredByBoundHu_PlansOnlyShortage()
    {
        var harness = CreateCustomerPlanningHarness((101, 100, 2000));
        SeedCustomerBoundHu(harness, (1, 101, 100, 1200, "HU-900101"));
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10);

        Assert.Equal(800, result.Summary.PlannedQty);
        Assert.Equal(2, result.Summary.PlannedPalletCount);
        Assert.Equal(800, harness.Store.GetProductionPalletsByDoc(result.PrdDocId).Sum(pallet => pallet.PlannedQty));
    }

    [Fact]
    public void PlanOrder_CustomerFullyCoveredByBoundHu_ReturnsNoProductionRequired()
    {
        var harness = CreateCustomerPlanningHarness((101, 100, 600), (102, 200, 400));
        SeedCustomerBoundHu(
            harness,
            (1, 101, 100, 600, "HU-900101"),
            (2, 102, 200, 400, "HU-900102"));
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10);

        Assert.False(result.ProductionRequired);
        Assert.Equal("Заказ покрыт складскими остатками, производство не требуется.", result.Message);
        Assert.Equal(0, result.PrdDocId);
        Assert.Empty(harness.Store.GetDocsByOrder(10).Where(doc => doc.Type == DocType.ProductionReceipt));
    }

    [Fact]
    public void PlanOrder_CustomerLinePartiallyCoveredByBoundHu_IsIdempotent()
    {
        var harness = CreateCustomerPlanningHarness((101, 100, 2000));
        SeedCustomerBoundHu(harness, (1, 101, 100, 1200, "HU-900101"));
        var service = new ProductionPalletService(harness.Store);

        var first = service.PlanOrder(10);
        var firstHuCodes = harness.Store.GetProductionPalletsByDoc(first.PrdDocId)
            .Select(pallet => pallet.HuCode)
            .ToArray();
        var second = service.PlanOrder(10);
        var secondHuCodes = harness.Store.GetProductionPalletsByDoc(second.PrdDocId)
            .Select(pallet => pallet.HuCode)
            .ToArray();

        Assert.Equal(first.PrdDocId, second.PrdDocId);
        Assert.True(second.WasExisting);
        Assert.Equal(2, harness.Store.GetProductionPalletsByDoc(first.PrdDocId).Count);
        Assert.Equal(firstHuCodes, secondHuCodes);
    }

    [Fact]
    public void PlanOrder_CustomerBoundHuAndExistingActivePallet_CreateNoAdditionalPallets()
    {
        var harness = CreateCustomerPlanningHarness((101, 100, 2000));
        SeedCustomerBoundHu(harness, (1, 101, 100, 1200, "HU-900101"));
        SeedExistingCustomerPalletPlan(harness, plannedQty: 800, status: ProductionPalletStatus.Filled);
        var service = new ProductionPalletService(harness.Store);

        var before = harness.Store.GetProductionPalletsByDoc(20).Count;
        var result = service.PlanOrder(10);

        Assert.Equal(20, result.PrdDocId);
        Assert.Equal(before, harness.Store.GetProductionPalletsByDoc(20).Count);
        Assert.Equal(800, harness.Store.GetProductionPalletsByDoc(20).Sum(pallet => pallet.PlannedQty));
    }

    [Fact]
    public void PlanOrder_CustomerCancelledPalletsDoNotCoverShortage()
    {
        var harness = CreateCustomerPlanningHarness((101, 100, 2000));
        SeedCustomerBoundHu(harness, (1, 101, 100, 1200, "HU-900101"));
        SeedExistingCustomerPalletPlan(harness, plannedQty: 800, status: ProductionPalletStatus.Cancelled);
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10);
        var activePallets = harness.Store.GetDocsByOrder(10)
            .SelectMany(doc => harness.Store.GetProductionPalletsByDoc(doc.Id))
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Equal(800, result.Summary.PlannedQty);
        Assert.Equal(800, activePallets.Sum(pallet => pallet.PlannedQty));
    }

    [Fact]
    public void PlanOrder_InternalOrderBehavior_DoesNotSubtractCustomerBoundHu()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        harness.SeedOrderReceiptPlanLines(
            10,
            new OrderReceiptPlanLine
            {
                Id = 1,
                OrderId = 10,
                OrderLineId = 101,
                ItemId = 100,
                QtyPlanned = 1200,
                ToLocationId = 1,
                ToHu = "HU-900101",
                SortOrder = 1
            });
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10);

        Assert.Equal(1200, result.Summary.PlannedQty);
        Assert.Equal(2, result.Summary.PlannedPalletCount);
    }

    [Fact]
    public void UpdateOrderLineQty_ClearsOnlyPlannedPalletsForChangedLine()
    {
        var harness = CreateHarnessWithTwoOrderLines(firstQty: 1200, secondQty: 600);
        var palletService = new ProductionPalletService(harness.Store);
        var orderService = new OrderService(harness.Store);
        var plan = palletService.PlanOrder(10);
        var otherLineHus = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .Where(pallet => pallet.OrderLineId == 102)
            .Select(pallet => pallet.HuCode)
            .ToArray();

        orderService.UpdateOrder(
            10,
            "056",
            null,
            null,
            null,
            [
                new OrderLineView { ItemId = 100, QtyOrdered = 600, ProductionPurpose = ProductionLinePurpose.InternalStock },
                new OrderLineView { ItemId = 200, QtyOrdered = 600, ProductionPurpose = ProductionLinePurpose.InternalStock }
            ],
            OrderType.Internal);

        var pallets = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Single(pallets, pallet => pallet.OrderLineId == 101);
        Assert.Equal(otherLineHus, pallets.Where(pallet => pallet.OrderLineId == 102).Select(pallet => pallet.HuCode).ToArray());
    }

    [Fact]
    public void UpdateOrderLineQty_PreservesFilledPalletsForChangedLine()
    {
        var harness = CreateHarnessWithTwoOrderLines(firstQty: 1200, secondQty: 600);
        var palletService = new ProductionPalletService(harness.Store);
        var orderService = new OrderService(harness.Store);
        var plan = palletService.PlanOrder(10);
        var filledHu = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .Where(pallet => pallet.OrderLineId == 101)
            .OrderBy(pallet => pallet.Id)
            .First()
            .HuCode;
        palletService.Fill(filledHu, "TSD-01");

        orderService.UpdateOrder(
            10,
            "056",
            null,
            null,
            null,
            [
                new OrderLineView { ItemId = 100, QtyOrdered = 600, ProductionPurpose = ProductionLinePurpose.InternalStock },
                new OrderLineView { ItemId = 200, QtyOrdered = 600, ProductionPurpose = ProductionLinePurpose.InternalStock }
            ],
            OrderType.Internal);

        var pallets = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId);
        var filled = Assert.Single(pallets, pallet => pallet.HuCode == filledHu);
        Assert.Equal(ProductionPalletStatus.Filled, filled.Status);
        Assert.DoesNotContain(pallets, pallet => pallet.OrderLineId == 101 && pallet.Status == ProductionPalletStatus.Planned);
        Assert.Contains(pallets, pallet => pallet.OrderLineId == 102);
    }

    [Fact]
    public void DeleteOrderLine_WithOnlyPlannedPallets_RemovesOnlyThatLinePlan()
    {
        var harness = CreateHarnessWithTwoOrderLines(firstQty: 600, secondQty: 600);
        var palletService = new ProductionPalletService(harness.Store);
        var orderService = new OrderService(harness.Store);
        var plan = palletService.PlanOrder(10);
        var otherLineHu = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Single(pallet => pallet.OrderLineId == 102).HuCode;

        orderService.UpdateOrder(
            10,
            "056",
            null,
            null,
            null,
            [new OrderLineView { ItemId = 200, QtyOrdered = 600, ProductionPurpose = ProductionLinePurpose.InternalStock }],
            OrderType.Internal);

        var pallets = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId);
        Assert.DoesNotContain(pallets, pallet => pallet.OrderLineId == 101);
        Assert.Equal(otherLineHu, Assert.Single(pallets, pallet => pallet.OrderLineId == 102).HuCode);
        Assert.Single(harness.Store.GetOrderLines(10));
    }

    [Fact]
    public void DeleteOrderLine_WithFilledPallets_IsBlocked()
    {
        var harness = CreateHarnessWithTwoOrderLines(firstQty: 600, secondQty: 600);
        var palletService = new ProductionPalletService(harness.Store);
        var orderService = new OrderService(harness.Store);
        var plan = palletService.PlanOrder(10);
        var filledHu = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Single(pallet => pallet.OrderLineId == 101).HuCode;
        palletService.Fill(filledHu, "TSD-01");

        var ex = Assert.Throws<InvalidOperationException>(() => orderService.UpdateOrder(
            10,
            "056",
            null,
            null,
            null,
            [new OrderLineView { ItemId = 200, QtyOrdered = 600, ProductionPurpose = ProductionLinePurpose.InternalStock }],
            OrderType.Internal));

        Assert.Equal("Товар: нельзя удалить строку, есть заполненные паллеты/HU.", ex.Message);
        Assert.Contains(harness.Store.GetOrderLines(10), line => line.Id == 101);
        Assert.Contains(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId), pallet => pallet.HuCode == filledHu);
    }

    [Fact]
    public void ChangeOrderLineItem_WithFilledPallets_IsBlockedBeforeAddingReplacement()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        harness.SeedItem(new Item
        {
            Id = 200,
            Name = "Замена",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        var palletService = new ProductionPalletService(harness.Store);
        var orderService = new OrderService(harness.Store);
        var plan = palletService.PlanOrder(10);
        var filledHu = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Single().HuCode;
        palletService.Fill(filledHu, "TSD-01");

        var ex = Assert.Throws<InvalidOperationException>(() => orderService.UpdateOrder(
            10,
            "056",
            null,
            null,
            null,
            [new OrderLineView { ItemId = 200, QtyOrdered = 600, ProductionPurpose = ProductionLinePurpose.InternalStock }],
            OrderType.Internal));

        Assert.Equal("Товар: нельзя удалить строку, есть заполненные паллеты/HU.", ex.Message);
        Assert.Contains(harness.Store.GetOrderLines(10), line => line.Id == 101 && line.ItemId == 100);
        Assert.DoesNotContain(harness.Store.GetOrderLines(10), line => line.ItemId == 200);
        Assert.Single(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId), pallet => pallet.HuCode == filledHu);
    }

    [Fact]
    public void DecreaseOrderLineQty_BelowFilledCoverage_IsBlocked()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        var palletService = new ProductionPalletService(harness.Store);
        var orderService = new OrderService(harness.Store);
        var plan = palletService.PlanOrder(10);
        palletService.Fill(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Single().HuCode, "TSD-01");

        var ex = Assert.Throws<InvalidOperationException>(() => orderService.UpdateOrder(
            10,
            "056",
            null,
            null,
            null,
            [new OrderLineView { ItemId = 100, QtyOrdered = 500, ProductionPurpose = ProductionLinePurpose.InternalStock }],
            OrderType.Internal));

        Assert.Equal("Нельзя уменьшить количество ниже уже заполненного/выпущенного объема: заполнено 600.", ex.Message);
    }

    [Fact]
    public void DecreaseOrderLineQty_ToExactlyFilled_CancelsSurplusPlannedAndPrinted()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 4800, maxQtyPerHu: 600);
        var palletService = new ProductionPalletService(harness.Store);
        var orderService = new OrderService(harness.Store);
        var plan = palletService.PlanOrder(10);
        var pallets = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).OrderBy(pallet => pallet.Id).ToArray();
        Assert.Equal(8, pallets.Length);
        foreach (var pallet in pallets.Take(2))
        {
            palletService.Fill(pallet.HuCode, "TSD-01");
        }

        palletService.MarkPrinted(10, new DateTime(2026, 5, 13, 11, 0, 0));

        orderService.UpdateOrder(
            10,
            "056",
            null,
            null,
            null,
            [new OrderLineView { ItemId = 100, QtyOrdered = 1200, ProductionPurpose = ProductionLinePurpose.InternalStock }],
            OrderType.Internal);

        var after = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId);
        Assert.Equal(2, after.Count(pallet => pallet.Status == ProductionPalletStatus.Filled));
        Assert.DoesNotContain(after, pallet =>
            pallet.Status == ProductionPalletStatus.Planned
            || pallet.Status == ProductionPalletStatus.Printed);
        Assert.Equal(6, after.Count(pallet => pallet.Status == ProductionPalletStatus.Cancelled));
        Assert.Equal(1200, after.Where(pallet => pallet.Status == ProductionPalletStatus.Filled).Sum(pallet => pallet.PlannedQty), 3);
        var printRows = palletService.GetPrintRows(10);
        Assert.DoesNotContain(printRows, row =>
            row.Status == ProductionPalletStatus.Planned
            || row.Status == ProductionPalletStatus.Printed);
        Assert.Empty(PalletLabelPrintSelectionService.ResolveDefaultSelectedPalletIds(printRows));
        Assert.All(after.Where(pallet => pallet.Status == ProductionPalletStatus.Cancelled), pallet =>
            Assert.DoesNotContain(printRows, row => string.Equals(row.HuCode, pallet.HuCode, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void DecreaseOrderLineQty_PrintedSurplusDoesNotBlockDecrease()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 2400, maxQtyPerHu: 600);
        var palletService = new ProductionPalletService(harness.Store);
        var orderService = new OrderService(harness.Store);
        var plan = palletService.PlanOrder(10);
        palletService.MarkPrinted(10, new DateTime(2026, 5, 13, 11, 0, 0));

        orderService.UpdateOrder(
            10,
            "056",
            null,
            null,
            null,
            [new OrderLineView { ItemId = 100, QtyOrdered = 1200, ProductionPurpose = ProductionLinePurpose.InternalStock }],
            OrderType.Internal);

        var after = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(2, after.Length);
        Assert.All(after, pallet => Assert.Equal(ProductionPalletStatus.Printed, pallet.Status));
    }

    [Fact]
    public void DecreaseOrderLineQty_DoesNotAffectOtherOrderLines()
    {
        var harness = CreateHarnessWithTwoOrderLines(firstQty: 2400, secondQty: 600);
        var palletService = new ProductionPalletService(harness.Store);
        var orderService = new OrderService(harness.Store);
        var plan = palletService.PlanOrder(10);
        var secondLineHu = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Single(pallet => pallet.OrderLineId == 102).HuCode;

        orderService.UpdateOrder(
            10,
            "056",
            null,
            null,
            null,
            [
                new OrderLineView { ItemId = 100, QtyOrdered = 1200, ProductionPurpose = ProductionLinePurpose.InternalStock },
                new OrderLineView { ItemId = 200, QtyOrdered = 600, ProductionPurpose = ProductionLinePurpose.InternalStock }
            ],
            OrderType.Internal);

        var active = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(secondLineHu, Assert.Single(active, pallet => pallet.OrderLineId == 102).HuCode);
        Assert.Equal(2, active.Count(pallet => pallet.OrderLineId == 101));
    }

    [Fact]
    public void SyncOrderLinePlan_IsIdempotent_WhenOrderedEqualsFilledPlusPlanned()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var palletService = new ProductionPalletService(harness.Store);
        var orderService = new OrderService(harness.Store);
        var plan = palletService.PlanOrder(10);
        foreach (var pallet in harness.Store.GetProductionPalletsByDoc(plan.PrdDocId))
        {
            palletService.Fill(pallet.HuCode, "TSD-01");
        }

        orderService.UpdateOrder(
            10,
            "056",
            null,
            null,
            null,
            [new OrderLineView { ItemId = 100, QtyOrdered = 1200, ProductionPurpose = ProductionLinePurpose.InternalStock }],
            OrderType.Internal);

        var afterUpdate = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Select(pallet => pallet.Id).OrderBy(id => id).ToArray();
        palletService.SyncOrderLinePlan(10, 101, 1200);
        var afterSync = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Select(pallet => pallet.Id).OrderBy(id => id).ToArray();
        Assert.Equal(afterUpdate, afterSync);
        Assert.Equal(2, harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Count(pallet => pallet.Status == ProductionPalletStatus.Filled));
        Assert.DoesNotContain(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId), pallet => pallet.Status == ProductionPalletStatus.Planned);
    }

    [Fact]
    public void GetPrintRows_ExcludesCancelledPallets()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var palletService = new ProductionPalletService(harness.Store);
        var orderService = new OrderService(harness.Store);
        var plan = palletService.PlanOrder(10);
        var cancelledHu = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).OrderByDescending(pallet => pallet.Id).First().HuCode;
        harness.Store.CancelProductionPallets([harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Single(pallet => pallet.HuCode == cancelledHu).Id]);

        orderService.UpdateOrder(
            10,
            "056",
            null,
            null,
            null,
            [new OrderLineView { ItemId = 100, QtyOrdered = 600, ProductionPurpose = ProductionLinePurpose.InternalStock }],
            OrderType.Internal);

        var rows = palletService.GetPrintRows(10);
        Assert.DoesNotContain(rows, row => string.Equals(row.HuCode, cancelledHu, StringComparison.OrdinalIgnoreCase));
        Assert.Single(rows);
    }

    [Fact]
    public void IncreaseOrderLineQty_PlansOnlyMissingQtyWithoutDuplicatingFilledPallet()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        var palletService = new ProductionPalletService(harness.Store);
        var orderService = new OrderService(harness.Store);
        var plan = palletService.PlanOrder(10);
        var filledHu = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Single().HuCode;
        palletService.Fill(filledHu, "TSD-01");

        orderService.UpdateOrder(
            10,
            "056",
            null,
            null,
            null,
            [new OrderLineView { ItemId = 100, QtyOrdered = 1200, ProductionPurpose = ProductionLinePurpose.InternalStock }],
            OrderType.Internal);
        var replan = palletService.PlanOrder(10);

        var pallets = harness.Store.GetProductionPalletsByDoc(replan.PrdDocId);
        Assert.Equal(2, pallets.Count);
        Assert.Single(pallets, pallet => pallet.HuCode == filledHu && pallet.Status == ProductionPalletStatus.Filled);
        Assert.Single(pallets, pallet => pallet.Status == ProductionPalletStatus.Planned);
        Assert.Equal(1200, pallets.Sum(pallet => pallet.PlannedQty), 3);
    }

    [Fact]
    public void IncreaseOrderLineQty_WithTwoFilledPallets_AppendsOnlyMissingPlannedQty()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var palletService = new ProductionPalletService(harness.Store);
        var orderService = new OrderService(harness.Store);
        var plan = palletService.PlanOrder(10);
        var filledHus = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .OrderBy(pallet => pallet.Id)
            .Select(pallet => pallet.HuCode)
            .ToArray();
        Assert.Equal(2, filledHus.Length);
        foreach (var hu in filledHus)
        {
            palletService.Fill(hu, "TSD-01");
        }

        orderService.UpdateOrder(
            10,
            "056",
            null,
            null,
            null,
            [new OrderLineView { ItemId = 100, QtyOrdered = 2400, ProductionPurpose = ProductionLinePurpose.InternalStock }],
            OrderType.Internal);

        var pallets = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId);
        Assert.Equal(4, pallets.Count);
        Assert.Equal(2, pallets.Count(pallet => pallet.Status == ProductionPalletStatus.Filled));
        Assert.Equal(2, pallets.Count(pallet => pallet.Status == ProductionPalletStatus.Planned));
        Assert.Equal(2400, pallets.Sum(pallet => pallet.PlannedQty), 3);
        Assert.Equal(filledHus, pallets
            .Where(pallet => pallet.Status == ProductionPalletStatus.Filled)
            .Select(pallet => pallet.HuCode)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    [Fact]
    public void PlanOrder_WhenCoverageAlreadyComplete_DoesNotCreateDuplicates()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var palletService = new ProductionPalletService(harness.Store);
        var orderService = new OrderService(harness.Store);
        var plan = palletService.PlanOrder(10);
        foreach (var pallet in harness.Store.GetProductionPalletsByDoc(plan.PrdDocId))
        {
            palletService.Fill(pallet.HuCode, "TSD-01");
        }

        orderService.UpdateOrder(
            10,
            "056",
            null,
            null,
            null,
            [new OrderLineView { ItemId = 100, QtyOrdered = 2400, ProductionPurpose = ProductionLinePurpose.InternalStock }],
            OrderType.Internal);

        var afterUpdate = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId);
        palletService.PlanOrder(10);
        var afterReplan = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId);

        Assert.Equal(afterUpdate.Select(pallet => pallet.Id).OrderBy(id => id), afterReplan.Select(pallet => pallet.Id).OrderBy(id => id));
        Assert.Equal(4, afterReplan.Count);
    }

    [Fact]
    public void PlanOrder_WithFilledPallets_DoesNotThrowReassignmentError()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var palletService = new ProductionPalletService(harness.Store);
        var plan = palletService.PlanOrder(10);
        foreach (var pallet in harness.Store.GetProductionPalletsByDoc(plan.PrdDocId))
        {
            palletService.Fill(pallet.HuCode, "TSD-01");
        }

        harness.Store.UpdateOrderLineQty(101, 2400);

        var result = palletService.PlanOrder(10);

        Assert.Equal(4, harness.Store.GetProductionPalletsByDoc(result.PrdDocId).Count);
    }

    [Fact]
    public void PlanOrder_InternalFilledPalletMovedToDedicatedPrd_CountsAsCoverage()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var palletService = CreateAutoClosePalletService(harness);
        var plan = palletService.PlanOrder(10);
        var first = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .OrderBy(pallet => pallet.Id)
            .First();

        var fill = palletService.Fill(first.HuCode, "TSD-01", orderId: 10, prdDocId: plan.PrdDocId);
        var beforeIds = GetActivePalletsByOrder(harness, 10)
            .Select(pallet => pallet.Id)
            .Order()
            .ToArray();

        var replan = palletService.PlanOrder(10);
        var after = GetActivePalletsByOrder(harness, 10);

        Assert.True(fill.Success, fill.Error);
        Assert.NotEqual(plan.PrdDocId, fill.ClosedPrdDocId);
        Assert.Equal(plan.PrdDocId, replan.PrdDocId);
        Assert.Equal(beforeIds, after.Select(pallet => pallet.Id).Order().ToArray());
        Assert.Equal(2, after.Count);
        Assert.Contains(after, pallet =>
            pallet.Id == first.Id
            && pallet.PrdDocId == fill.ClosedPrdDocId
            && pallet.Status == ProductionPalletStatus.Filled);
        Assert.Equal(1200, after.Sum(pallet => pallet.PlannedQty), 3);
    }

    [Fact]
    public void PlanOrder_InternalFiveLinesWithDedicatedFilledPallets_PlansOnlyIncreasedLineDelta()
    {
        var harness = CreateHarnessWithFiveInternalOrderLines();
        var palletService = CreateAutoClosePalletService(harness);
        var plan = palletService.PlanOrder(10);
        var initialPallets = GetActivePalletsByOrder(harness, 10);
        var husToFill = initialPallets
            .Where(pallet => pallet.OrderLineId is 101 or 102 or 103)
            .OrderBy(pallet => pallet.Id)
            .Select(pallet => pallet.HuCode)
            .ToArray();

        foreach (var huCode in husToFill)
        {
            var fill = palletService.Fill(huCode, "TSD-01", orderId: 10, prdDocId: plan.PrdDocId);
            Assert.True(fill.Success, fill.Error);
        }

        harness.Store.UpdateOrderLineQty(103, 1800);
        var before = GetActivePalletsByOrder(harness, 10);
        var beforeIds = before.Select(pallet => pallet.Id).ToHashSet();

        var replan = palletService.PlanOrder(10);
        var after = GetActivePalletsByOrder(harness, 10);
        var created = after
            .Where(pallet => !beforeIds.Contains(pallet.Id))
            .ToArray();
        var afterSecondIds = after.Select(pallet => pallet.Id).Order().ToArray();
        palletService.PlanOrder(10);
        var afterThirdIds = GetActivePalletsByOrder(harness, 10)
            .Select(pallet => pallet.Id)
            .Order()
            .ToArray();

        Assert.Equal(plan.PrdDocId, replan.PrdDocId);
        var createdPallet = Assert.Single(created);
        Assert.Equal(103, createdPallet.OrderLineId);
        Assert.Equal(600, createdPallet.PlannedQty, 3);
        Assert.Equal(ProductionPalletStatus.Planned, createdPallet.Status);
        Assert.Equal(before.Count + 1, after.Count);
        Assert.Equal(1, after.Count(pallet => pallet.OrderLineId == 101));
        Assert.Equal(3, after.Count(pallet => pallet.OrderLineId == 102));
        Assert.Equal(3, after.Count(pallet => pallet.OrderLineId == 103));
        Assert.Equal(1824, after.Where(pallet => pallet.OrderLineId == 101).Sum(pallet => pallet.PlannedQty), 3);
        Assert.Equal(1134, after.Where(pallet => pallet.OrderLineId == 102).Sum(pallet => pallet.PlannedQty), 3);
        Assert.Equal(1800, after.Where(pallet => pallet.OrderLineId == 103).Sum(pallet => pallet.PlannedQty), 3);
        Assert.Equal(afterSecondIds, afterThirdIds);
    }

    [Fact]
    public void PlanOrder_InternalPrintedPalletCountsAsCoverage()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        var palletService = new ProductionPalletService(harness.Store);
        var plan = palletService.PlanOrder(10);
        palletService.MarkPrinted(10, new DateTime(2026, 5, 13, 11, 0, 0));
        var beforeIds = GetActivePalletsByOrder(harness, 10)
            .Select(pallet => pallet.Id)
            .Order()
            .ToArray();

        palletService.PlanOrder(10);
        var after = GetActivePalletsByOrder(harness, 10);

        Assert.Equal(beforeIds, after.Select(pallet => pallet.Id).Order().ToArray());
        var pallet = Assert.Single(after);
        Assert.Equal(plan.PrdDocId, pallet.PrdDocId);
        Assert.Equal(ProductionPalletStatus.Printed, pallet.Status);
    }

    [Fact]
    public void PlanOrder_InternalCancelledPalletDoesNotCountAsCoverage()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        var palletService = new ProductionPalletService(harness.Store);
        var plan = palletService.PlanOrder(10);
        var cancelled = Assert.Single(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId));
        harness.Store.CancelProductionPallets([cancelled.Id]);

        var replan = palletService.PlanOrder(10);
        var allPallets = harness.Store.GetDocsByOrder(10)
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .SelectMany(doc => harness.Store.GetProductionPalletsByDoc(doc.Id))
            .ToArray();
        var active = allPallets
            .Where(pallet => pallet.Status != ProductionPalletStatus.Cancelled)
            .ToArray();

        Assert.Contains(allPallets, pallet => pallet.Id == cancelled.Id && pallet.Status == ProductionPalletStatus.Cancelled);
        var activePallet = Assert.Single(active);
        Assert.Equal(600, activePallet.PlannedQty, 3);
        Assert.Equal(replan.PrdDocId, activePallet.PrdDocId);
    }

    [Fact]
    public void PlanOrder_MixedComponentCoverageCountsByComponentOrderLine()
    {
        var harness = CreateHarnessWithMixedOrderOnly();
        var palletService = new ProductionPalletService(harness.Store);
        var plan = palletService.PlanOrder(10);
        var mixedPallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId));
        harness.Store.UpdateOrderLineQty(101, 900);

        palletService.PlanOrder(10);
        var pallets = GetActivePalletsByOrder(harness, 10);
        var created = Assert.Single(pallets.Where(pallet => pallet.Id != mixedPallet.Id));

        Assert.Equal(101, created.OrderLineId);
        Assert.Equal(600, created.PlannedQty, 3);
        Assert.Equal(900, SumTestCoverageByOrderLine(pallets, 101), 3);
        Assert.Equal(200, SumTestCoverageByOrderLine(pallets, 102), 3);
    }

    [Fact]
    public void PlanOrder_ComponentPalletIgnoresHeaderOrderLineWhenLinesExist()
    {
        var harness = CreateHarnessWithComponentHeaderMismatch();
        var palletService = new ProductionPalletService(harness.Store);

        var replan = palletService.PlanOrder(10);
        var active = GetActivePalletsByOrder(harness, 10);
        var created = Assert.Single(active.Where(pallet => pallet.Id != 1));

        Assert.Equal(20, replan.PrdDocId);
        Assert.Equal(101, created.OrderLineId);
        Assert.Equal(600, created.PlannedQty, 3);
        Assert.Equal(600, SumTestCoverageByOrderLine(active, 101), 3);
        Assert.Equal(200, SumTestCoverageByOrderLine(active, 102), 3);
    }

    [Fact]
    public void PlanOrder_GeneratesHuAfterExistingHuNumber()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        harness.SeedHu(new HuRecord
        {
            Id = 42,
            Code = "HU-0000042",
            Status = "OPEN",
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10);

        var pallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(result.PrdDocId));
        Assert.Matches("^HU-[0-9]{7}$", pallet.HuCode);
        Assert.True(long.Parse(Regex.Match(pallet.HuCode, "[0-9]+").Value) > 42);
    }

    [Fact]
    public void PlanOrder_TwoMixedGroups_CreatesTwoDistinctHus_IgnoringMaxQtyPerHu()
    {
        var harness = CreateHarnessWithFourLineTwoMixedGroups();
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10);

        Assert.Equal(2, result.Summary.PlannedPalletCount);
        Assert.Equal(2400, result.Summary.PlannedQty);
        var pallets = harness.Store.GetProductionPalletsByDoc(result.PrdDocId).OrderBy(pallet => pallet.Id).ToArray();
        Assert.Equal(2, pallets.Length);
        Assert.Equal(2, pallets.Select(pallet => pallet.HuCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(pallets, pallet =>
        {
            Assert.True(pallet.IsMixedPallet);
            Assert.Equal(2, pallet.Lines.Count);
        });
        Assert.Equal(new[] { 101L, 102L }, pallets[0].Lines.Select(line => line.OrderLineId!.Value).Order().ToArray());
        Assert.Equal(new[] { 103L, 104L }, pallets[1].Lines.Select(line => line.OrderLineId!.Value).Order().ToArray());
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void PlanOrder_SingleMixedGroupOverCapacity_SucceedsWithOneHuAndAllLines()
    {
        var harness = CreateHarnessWithFourLineSingleMixedGroupOverCapacity();
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10);

        Assert.Equal(1, result.Summary.PlannedPalletCount);
        Assert.Equal(2400, result.Summary.PlannedQty);
        var pallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(result.PrdDocId));
        Assert.True(pallet.IsMixedPallet);
        Assert.Equal(4, pallet.Lines.Count);
        Assert.Equal(new[] { 101L, 102L, 103L, 104L }, pallet.Lines.Select(line => line.OrderLineId!.Value).Order().ToArray());
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void PlanOrder_AfterFilledMixedPlan_DoesNotReassignHu()
    {
        var harness = CreateHarnessWithFourLineTwoMixedGroups();
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        var pallets = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).OrderBy(pallet => pallet.Id).ToArray();
        var huCodesBefore = pallets.Select(pallet => pallet.HuCode).ToArray();

        service.Fill(pallets[0].HuCode, "TSD-01");
        var replan = service.PlanOrder(10);

        Assert.Equal(plan.PrdDocId, replan.PrdDocId);
        var huCodesAfter = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .OrderBy(pallet => pallet.Id)
            .Select(pallet => pallet.HuCode)
            .ToArray();
        Assert.Equal(huCodesBefore, huCodesAfter);
    }

    [Fact]
    public void ScanAndFill_TwoMixedGroups_FillsWithoutWritingLedger()
    {
        var harness = CreateHarnessWithFourLineTwoMixedGroups();
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        var pallets = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).OrderBy(pallet => pallet.Id).ToArray();

        foreach (var pallet in pallets)
        {
            var scan = service.Scan(10, plan.PrdDocId, pallet.HuCode);
            Assert.True(scan.Success);
            Assert.True(scan.IsMixedPallet);
            Assert.Equal(2, scan.Lines.Count);

            var fill = service.Fill(pallet.HuCode, "TSD-01");
            Assert.True(fill.Success);
            Assert.False(fill.AlreadyFilled);
        }

        Assert.Empty(harness.LedgerEntries);
        Assert.All(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId), pallet => Assert.Equal(ProductionPalletStatus.Filled, pallet.Status));
    }

    [Fact]
    public void PlanOrder_MixedLines_CreateOneHuWithMultipleComponentLines_AndNoLedger()
    {
        var harness = CreateHarnessWithMixedOrderOnly();
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10);

        Assert.Equal(1, result.Summary.PlannedPalletCount);
        Assert.Equal(500, result.Summary.PlannedQty);
        Assert.Empty(harness.LedgerEntries);
        var pallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(result.PrdDocId));
        Assert.Matches("^HU-[0-9]{7}$", pallet.HuCode);
        Assert.True(pallet.IsMixedPallet);
        Assert.Equal(2, pallet.Lines.Count);
        Assert.Equal(new[] { 101L, 102L }, pallet.Lines.Select(line => line.OrderLineId!.Value).Order().ToArray());
        Assert.Single(harness.Store.GetProductionPalletsByDoc(result.PrdDocId).Select(p => p.HuCode).Distinct());
    }

    [Fact]
    public void PlanOrder_MixedLines_IsIdempotent()
    {
        var harness = CreateHarnessWithMixedOrderOnly();
        var service = new ProductionPalletService(harness.Store);

        var first = service.PlanOrder(10);
        var firstHu = harness.Store.GetProductionPalletsByDoc(first.PrdDocId).Single().HuCode;
        var second = service.PlanOrder(10);
        var secondHu = harness.Store.GetProductionPalletsByDoc(second.PrdDocId).Single().HuCode;

        Assert.Equal(first.PrdDocId, second.PrdDocId);
        Assert.Single(harness.Store.GetProductionPalletsByDoc(first.PrdDocId));
        Assert.Equal(2, harness.Store.GetDocLines(first.PrdDocId).Count);
        Assert.Equal(firstHu, secondHu);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void PlanOrder_AfterMixedCheckboxesCleared_RebuildsAsSeparateHus()
    {
        var harness = CreateHarnessWithMixedOrderOnly();
        var service = new ProductionPalletService(harness.Store);
        var orderService = new OrderService(harness.Store);
        var first = service.PlanOrder(10);

        orderService.UpdateOrder(
            10,
            "056",
            null,
            null,
            null,
            [
                new OrderLineView { ItemId = 100, QtyOrdered = 300, ProductionPurpose = ProductionLinePurpose.InternalStock },
                new OrderLineView { ItemId = 200, QtyOrdered = 200, ProductionPurpose = ProductionLinePurpose.InternalStock }
            ],
            OrderType.Internal);
        var second = service.PlanOrder(10);

        Assert.Equal(first.PrdDocId, second.PrdDocId);
        var pallets = harness.Store.GetProductionPalletsByDoc(second.PrdDocId).OrderBy(pallet => pallet.Id).ToArray();
        Assert.Equal(2, pallets.Length);
        Assert.Equal(2, pallets.Select(pallet => pallet.HuCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(pallets, pallet => Assert.False(pallet.IsMixedPallet));
        Assert.Equal(new[] { 101L, 102L }, pallets.SelectMany(pallet => pallet.Lines).Select(line => line.OrderLineId!.Value).Order().ToArray());
        Assert.Equal(2, harness.Store.GetDocLines(second.PrdDocId).Count);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void GetPrintRows_MixedPallet_ReturnsOneRowWithComposition()
    {
        var harness = CreateHarnessWithMixedOrderOnly();
        var service = new ProductionPalletService(harness.Store);
        service.PlanOrder(10);

        var row = Assert.Single(service.GetPrintRows(10));

        Assert.True(row.IsMixedPallet);
        Assert.Equal("Микс-паллета", row.ItemName);
        Assert.Equal(500, row.Qty);
        Assert.Contains("Товар", row.Composition);
        Assert.Contains("Добавка", row.Composition);
        Assert.Equal(2, row.Lines.Count);
    }

    [Fact]
    public void ScanAndFill_MixedPallet_ReturnsCompositionAndFillsWithoutLedger()
    {
        var harness = CreateHarnessWithMixedOrderOnly();
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        var hu = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Single().HuCode;

        var scan = service.Scan(10, plan.PrdDocId, hu);
        var firstFill = service.Fill(hu, "TSD-01");
        var secondFill = service.Fill(hu, "TSD-01");

        Assert.True(scan.Success);
        Assert.True(scan.IsMixedPallet);
        Assert.Equal(2, scan.Lines.Count);
        Assert.True(firstFill.Success);
        Assert.False(firstFill.AlreadyFilled);
        Assert.True(secondFill.Success);
        Assert.True(secondFill.AlreadyFilled);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void PlanOrder_RequiresPalletCapacity()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: null);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<InvalidOperationException>(() => service.PlanOrder(10));

        Assert.Equal("Не задано количество на паллете для номенклатуры", ex.Message);
        Assert.Empty(harness.Store.GetDocsByOrder(10));
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void GetFillingOrders_ReturnsOrderWithPreparedPallets_WhenHasUnfilledPallets()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 2);
        var service = new ProductionPalletService(harness.Store);

        var order = Assert.Single(service.GetFillingOrders());

        Assert.Equal(10, order.OrderId);
        Assert.Equal("056", order.OrderRef);
        Assert.Equal(20, order.PrdDocId);
        Assert.Equal(6, order.Summary.PlannedPalletCount);
        Assert.Equal(2, order.Summary.FilledPalletCount);
        Assert.Equal(4, order.Summary.RemainingPalletCount);
        Assert.Equal(2400, order.Summary.RemainingQty);
    }

    [Fact]
    public void GetFillingOrders_DoesNotReturnOrder_WhenAllPreparedPalletsFilled()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 6);
        var service = new ProductionPalletService(harness.Store);

        Assert.Empty(service.GetFillingOrders());
    }

    [Fact]
    public void GetFillingContext_ReturnsPreparedContext_WithoutCreatingPlan()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 2);
        var service = new ProductionPalletService(harness.Store);

        var context = service.GetFillingContext(10);

        Assert.Equal(10, context.OrderId);
        Assert.Equal("056", context.OrderRef);
        Assert.Equal(20, context.PrdDocId);
        Assert.Equal("PRD-2026-000001", context.PrdDocRef);
        Assert.Equal(6, context.Document.Summary.PlannedPalletCount);
        Assert.Equal(1, harness.Store.GetDocsByOrder(10).Count(doc => doc.Type == DocType.ProductionReceipt));
        Assert.Single(harness.Store.GetDocLines(20));
        Assert.Equal(6, harness.Store.GetProductionPalletsByDoc(20).Count);
    }

    [Fact]
    public void TsdFillingChain_PlannedOrder_ScansAndFillsAllPalletsWithoutDuplicates()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);

        var plan = service.PlanOrder(10);
        var plannedHus = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .OrderBy(pallet => pallet.Id)
            .Select(pallet => pallet.HuCode)
            .ToArray();
        var order = Assert.Single(service.GetFillingOrders());
        var context = service.GetFillingContext(order.OrderId);

        Assert.Equal(plan.PrdDocId, context.PrdDocId);
        Assert.Equal(2, context.Document.Summary.PlannedPalletCount);
        Assert.Equal(2, context.Document.Summary.RemainingPalletCount);
        Assert.Equal(plannedHus, context.Document.Pallets.Select(pallet => pallet.HuCode).ToArray());

        var firstScan = service.Scan(context.OrderId, context.PrdDocId, plannedHus[0]);
        var firstFill = service.Fill(plannedHus[0], "TSD-01", context.OrderId, context.PrdDocId);
        var afterFirst = service.GetFillingContext(context.OrderId);
        var secondScan = service.Scan(context.OrderId, context.PrdDocId, plannedHus[1]);
        var secondFill = service.Fill(plannedHus[1], "TSD-01", context.OrderId, context.PrdDocId);
        var duplicateFill = service.Fill(plannedHus[1], "TSD-01", context.OrderId, context.PrdDocId);

        Assert.True(firstScan.Success);
        Assert.Equal(1, firstScan.PalletIndex);
        Assert.Equal(2, firstScan.PalletCount);
        Assert.True(firstFill.Success);
        Assert.False(firstFill.AlreadyFilled);
        Assert.Equal(1, firstFill.Document?.Summary.FilledPalletCount);
        Assert.Equal(1, afterFirst.Document.Summary.RemainingPalletCount);
        Assert.True(secondScan.Success);
        Assert.Equal(2, secondScan.PalletIndex);
        Assert.True(secondFill.Success);
        Assert.False(secondFill.AlreadyFilled);
        Assert.Equal(2, secondFill.Document?.Summary.FilledPalletCount);
        Assert.True(duplicateFill.Success);
        Assert.True(duplicateFill.AlreadyFilled);
        Assert.Empty(service.GetFillingOrders());
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void TsdFillingChain_MixedOrder_ReturnsCompositionAndFillsWholeHu()
    {
        var harness = CreateHarnessWithMixedOrderOnly();
        var service = new ProductionPalletService(harness.Store);

        var plan = service.PlanOrder(10);
        var context = service.GetFillingContext(10);
        var hu = Assert.Single(context.Document.Pallets).HuCode;

        var scan = service.Scan(context.OrderId, context.PrdDocId, hu);
        var fill = service.Fill(hu, "TSD-01", context.OrderId, context.PrdDocId);
        var repeated = service.Fill(hu, "TSD-01", context.OrderId, context.PrdDocId);

        Assert.Equal(plan.PrdDocId, context.PrdDocId);
        Assert.True(scan.Success);
        Assert.True(scan.IsMixedPallet);
        Assert.Equal("Микс-паллета", scan.ItemName);
        Assert.Equal(2, scan.Lines.Count);
        Assert.Equal(500, scan.Lines.Sum(line => line.Qty));
        Assert.True(fill.Success);
        Assert.False(fill.AlreadyFilled);
        Assert.Equal(1, fill.Document?.Summary.FilledPalletCount);
        Assert.Equal(0, fill.Document?.Summary.RemainingPalletCount);
        Assert.True(repeated.Success);
        Assert.True(repeated.AlreadyFilled);
        Assert.Empty(service.GetFillingOrders());
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void GetFillingContext_WithoutPreparedPallets_ReturnsClearError()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<InvalidOperationException>(() => service.GetFillingContext(10));

        Assert.Equal(
            "Для заказа не сформирован план паллет. Сформируйте и напечатайте паллетные этикетки перед наполненением.",
            ex.Message);
        Assert.Empty(harness.Store.GetDocsByOrder(10));
    }

    [Fact]
    public void GetFillingContext_ClosedPrdAllFilled_ReturnsCompletedMessage()
    {
        var harness = CreateHarnessWithCustomerOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        var documents = harness.CreateService();
        var service = new ProductionPalletService(
            harness.Store,
            new ProductionFillCloseService(
                harness.Store,
                documents,
                new FlowStockLedgerFlowOptions { ProductionAutoCloseOnFill = true }));
        var plan = service.PlanOrder(10);
        var hu = Assert.Single(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)).HuCode;
        Assert.True(service.Fill(hu, "TSD-01", orderId: 10).Success);
        Assert.Equal(OrderStatus.Accepted, harness.GetOrder(10).Status);

        var ex = Assert.Throws<InvalidOperationException>(() => service.GetFillingContext(10));

        Assert.Contains("завершён", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Нет паллет к наполнению", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScanPallet_KnownHu_ReturnsPreviewWithoutLedger()
    {
        var harness = CreateHarnessWithSinglePallet(ProductionPalletStatus.Planned);
        var service = new ProductionPalletService(harness.Store);

        var result = service.Scan(orderId: 10, prdDocId: 20, huCode: "HU-000001");

        Assert.True(result.Success);
        Assert.False(result.AlreadyFilled);
        Assert.Equal("056", result.OrderRef);
        Assert.Equal("PRD-2026-000001", result.PrdDocRef);
        Assert.Equal("HU-000001", result.HuCode);
        Assert.Equal("Товар", result.ItemName);
        Assert.Equal(600, result.PlannedQty);
        Assert.Equal(1, result.PalletIndex);
        Assert.Equal(1, result.PalletCount);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void ScanPallet_WrongSelectedOrderOrPrd_IsRejected()
    {
        var harness = CreateHarnessWithSinglePallet(ProductionPalletStatus.Planned);
        var service = new ProductionPalletService(harness.Store);

        var result = service.Scan(orderId: 999, prdDocId: 20, huCode: "HU-000001");

        Assert.False(result.Success);
        Assert.Equal("Эта паллета относится к другому заказу", result.Error);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void ScanPallet_FilledHu_ReturnsAlreadyFilledStateWithoutLedger()
    {
        var harness = CreateHarnessWithSinglePallet(ProductionPalletStatus.Filled);
        var service = new ProductionPalletService(harness.Store);

        var result = service.Scan(orderId: 10, prdDocId: 20, huCode: "HU-000001");

        Assert.True(result.Success);
        Assert.True(result.AlreadyFilled);
        Assert.Equal(ProductionPalletStatus.Filled, result.PalletStatus);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void ScanPallet_UnknownHu_IsRejectedWithoutLedger()
    {
        var harness = CreateHarnessWithSinglePallet(ProductionPalletStatus.Planned);
        var service = new ProductionPalletService(harness.Store);

        var result = service.Scan(orderId: 10, prdDocId: 20, huCode: "HU-404");

        Assert.False(result.Success);
        Assert.Equal("Паллета не найдена в плане выпуска", result.Error);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void ScanPallet_CancelledHu_IsRejectedWithoutLedger()
    {
        var harness = CreateHarnessWithSinglePallet(ProductionPalletStatus.Cancelled);
        var service = new ProductionPalletService(harness.Store);

        var result = service.Scan(orderId: 10, prdDocId: 20, huCode: "HU-000001");

        Assert.False(result.Success);
        Assert.Equal("Паллета отменена и не может быть наполнена.", result.Error);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void FillPallet_PostsLedgerOnce_AndRepeatedScanIsIdempotent()
    {
        var harness = CreateHarnessWithSinglePallet(ProductionPalletStatus.Planned);
        var service = new ProductionPalletService(harness.Store);

        var first = service.Fill("HU-000001", "TSD-01");
        var second = service.Fill("HU-000001", "TSD-01");

        Assert.True(first.Success);
        Assert.False(first.AlreadyFilled);
        Assert.True(second.Success);
        Assert.True(second.AlreadyFilled);
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(1, first.Document?.Summary.FilledPalletCount);
        Assert.Equal(600, first.Document?.Summary.FilledQty);
        Assert.Equal(0, first.Document?.Summary.RemainingQty);
    }

    [Fact]
    public void FillPallet_RejectsOverproduction()
    {
        var harness = new CloseDocumentHarness();
        SeedBase(harness, orderQty: 1000, plannedQty: 300, huCode: "HU-000002");
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            PrdDocId = 20,
            DocLineId = 201,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 100,
            ItemName = "Товар",
            HuCode = "HU-000001",
            PlannedQty = 800,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 13, 10, 0, 0),
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });
        harness.SeedProductionPallet(BuildPallet(id: 2, huCode: "HU-000002", plannedQty: 300));
        var service = new ProductionPalletService(harness.Store);

        var result = service.Fill("HU-000002", "TSD-01");

        Assert.False(result.Success);
        Assert.Equal("Выпуск превышает остаток по строке заказа", result.Error);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void Get_ReturnsProductionPalletSummary()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 2);
        var service = new ProductionPalletService(harness.Store);

        var document = service.Get(20);

        Assert.Equal(6, document.Summary.PlannedPalletCount);
        Assert.Equal(3600, document.Summary.PlannedQty);
        Assert.Equal(2, document.Summary.FilledPalletCount);
        Assert.Equal(1200, document.Summary.FilledQty);
        Assert.Equal(4, document.Summary.RemainingPalletCount);
        Assert.Equal(2400, document.Summary.RemainingQty);
        var line = Assert.Single(document.Lines);
        Assert.Equal(3600, line.OrderedQty);
        Assert.Equal(6, line.PlannedPalletCount);
        Assert.Equal(2, line.FilledPalletCount);
    }

    [Fact]
    public void GetPrintRows_ReturnsPreparedPalletLabelRows()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 0);
        var service = new ProductionPalletService(harness.Store);

        var rows = service.GetPrintRows(10);

        Assert.Equal(6, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("056", row.OrderRef);
            Assert.Equal("ПЕЧАГИН ПРОДУКТ", row.ClientName);
            Assert.Equal("PRD-2026-000001", row.PrdRef);
            Assert.Equal("Товар", row.ItemName);
            Assert.Equal("Печагин", row.Brand);
            Assert.Equal(600, row.Qty);
            Assert.Equal("шт", row.Uom);
            Assert.Equal("MAIN", row.StoragePlace);
            Assert.Equal(new DateTime(2026, 5, 13), row.ProductionDate);
        });
        Assert.Equal(1, rows[0].PalletNo);
        Assert.Equal(6, rows[0].PalletCount);
        Assert.Equal("HU-000001", rows[0].HuCode);
        Assert.Equal(6, rows[^1].PalletNo);
        Assert.Equal("HU-000006", rows[^1].HuCode);
    }

    [Fact]
    public void GetPrintRows_ShippedOrderWithoutOpenPrd_UsesLatestProductionReceiptWithPallets()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 6);
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "056",
            Type = OrderType.Internal,
            Status = OrderStatus.Shipped,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedDoc(new Doc
        {
            Id = 20,
            DocRef = "PRD-2026-000001",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0),
            ClosedAt = new DateTime(2026, 5, 13, 12, 0, 0)
        });
        var service = new ProductionPalletService(harness.Store);

        var rows = service.GetPrintRows(10);

        Assert.Equal(6, rows.Count);
        Assert.All(rows, row => Assert.Equal("PRD-2026-000001", row.PrdRef));
        Assert.Equal("HU-000001", rows[0].HuCode);
        Assert.Equal("HU-000006", rows[^1].HuCode);
    }

    [Fact]
    public void GetPrintRows_WithoutPreparedPlan_ReturnsClearError()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<InvalidOperationException>(() => service.GetPrintRows(10));

        Assert.Equal("Сначала сформируйте план паллет", ex.Message);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void GetPrintRows_DoesNotCreateHuOrLedger()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 0);
        var service = new ProductionPalletService(harness.Store);
        var before = harness.Store.GetProductionPalletsByDoc(20).Select(pallet => pallet.HuCode).ToArray();

        _ = service.GetPrintRows(10);

        var after = harness.Store.GetProductionPalletsByDoc(20).Select(pallet => pallet.HuCode).ToArray();
        Assert.Equal(before, after);
        Assert.Equal(6, harness.Store.GetProductionPalletsByDoc(20).Count);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void GetPrintRows_CustomerOrderWithBoundHu_ReturnsReservedHuRows()
    {
        var harness = CreateCustomerHarnessWithBoundHu(
            new OrderReceiptPlanLine
            {
                Id = 501,
                OrderId = 78,
                OrderLineId = 101,
                ItemId = 100,
                ItemName = "Товар",
                QtyPlanned = 600,
                ToLocationCode = "MAIN",
                ToHu = "HU-0000478",
                SortOrder = 1
            },
            new OrderReceiptPlanLine
            {
                Id = 502,
                OrderId = 78,
                OrderLineId = 101,
                ItemId = 100,
                ItemName = "Товар",
                QtyPlanned = 400,
                ToLocationCode = "MAIN",
                ToHu = "HU-0000479",
                SortOrder = 2
            });
        var service = new ProductionPalletService(harness.Store);

        var rows = service.GetPrintRows(78);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(ProductionPalletPrintSourceType.ReservedHu, row.SourceType));
        Assert.Equal("078", rows[0].OrderRef);
        Assert.Contains(rows, row => string.Equals(row.HuCode, "HU-0000478", StringComparison.OrdinalIgnoreCase) && row.Qty == 600);
        Assert.Contains(rows, row => string.Equals(row.HuCode, "HU-0000479", StringComparison.OrdinalIgnoreCase) && row.Qty == 400);
        Assert.Empty(PalletLabelPrintSelectionService.ResolveDefaultSelectedPalletIds(rows));
    }

    [Fact]
    public void GetPrintRows_CustomerOrderWithNoBoundHu_ReturnsEmpty()
    {
        var harness = CreateCustomerHarnessWithBoundHu();
        var service = new ProductionPalletService(harness.Store);

        Assert.Empty(service.GetPrintRows(78));
    }

    [Fact]
    public void GetPrintRows_CustomerOrderWithPartialCoverage_ReturnsOnlyBoundHu()
    {
        var harness = CreateCustomerHarnessWithBoundHu(
            new OrderReceiptPlanLine
            {
                Id = 501,
                OrderId = 78,
                OrderLineId = 101,
                ItemId = 100,
                ItemName = "Товар",
                QtyPlanned = 300,
                ToHu = "HU-0000485",
                SortOrder = 1
            });
        var service = new ProductionPalletService(harness.Store);

        var rows = service.GetPrintRows(78);

        Assert.Single(rows);
        Assert.Equal("HU-0000485", rows[0].HuCode);
        Assert.Equal(300, rows[0].Qty);
    }

    [Fact]
    public void GetPrintRows_CustomerOrder_WithProductionPalletPlan_ReturnsBoundHuAndPalletRows()
    {
        var harness = CreateCustomerHarnessWithBoundHu(
            new OrderReceiptPlanLine
            {
                Id = 501,
                OrderId = 78,
                OrderLineId = 101,
                ItemId = 100,
                ItemName = "Товар",
                QtyPlanned = 600,
                ToHu = "HU-BOUND",
                SortOrder = 1
            });
        harness.SeedDoc(new Doc
        {
            Id = 200,
            DocRef = "PRD-2026-009999",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 78,
            CreatedAt = new DateTime(2026, 5, 20, 9, 0, 0)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 90,
            PrdDocId = 200,
            DocLineId = 0,
            OrderId = 78,
            OrderLineId = 101,
            ItemId = 100,
            ItemName = "Товар",
            HuCode = "HU-PLAN",
            PlannedQty = 600,
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 5, 20, 9, 0, 0)
        });
        var service = new ProductionPalletService(harness.Store);

        var rows = service.GetPrintRows(78);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row =>
            row.SourceType == ProductionPalletPrintSourceType.ReservedHu
            && string.Equals(row.HuCode, "HU-BOUND", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rows, row =>
            row.SourceType == ProductionPalletPrintSourceType.ProductionPallet
            && string.Equals(row.HuCode, "HU-PLAN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetPrintRows_CustomerOrder083Like_AfterPlan_ReturnsProductionPalletRows()
    {
        var harness = CreateHarnessWithCustomerTwoOrderLines(firstQty: 120, secondQty: 80, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);

        var plan = service.PlanOrder(83);
        var pallets = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pallet => pallet.OrderLineId)
            .ToArray();

        Assert.Equal(2, plan.Summary.PlannedPalletCount);
        Assert.Equal(2, pallets.Length);

        var rows = service.GetPrintRows(83);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(ProductionPalletPrintSourceType.ProductionPallet, row.SourceType));
        Assert.Equal(pallets.Select(pallet => pallet.HuCode).ToArray(), rows.Select(row => row.HuCode).ToArray());
        Assert.Contains(rows, row => row.Qty == 120);
        Assert.Contains(rows, row => row.Qty == 80);
    }

    [Fact]
    public void MarkPrinted_CustomerOrder_WithProductionPalletPlan_UpdatesStatuses()
    {
        var harness = CreateHarnessWithCustomerTwoOrderLines(firstQty: 120, secondQty: 80, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(83);
        var printedAt = new DateTime(2026, 5, 22, 12, 0, 0);

        var updated = service.MarkPrinted(83, printedAt);

        Assert.Equal(2, updated);
        var pallets = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId);
        Assert.All(pallets, pallet => Assert.Equal(ProductionPalletStatus.Printed, pallet.Status));
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void GetPrintRows_CustomerOrder_DoesNotCreateProductionPalletsOrLedger()
    {
        var harness = CreateCustomerHarnessWithBoundHu(
            new OrderReceiptPlanLine
            {
                Id = 501,
                OrderId = 78,
                OrderLineId = 101,
                ItemId = 100,
                ItemName = "Товар",
                QtyPlanned = 600,
                ToHu = "HU-0000478",
                SortOrder = 1
            });
        var service = new ProductionPalletService(harness.Store);
        var docsBefore = harness.Store.GetDocsByOrder(78).Count();

        _ = service.GetPrintRows(78);

        Assert.Equal(docsBefore, harness.Store.GetDocsByOrder(78).Count);
        Assert.False(harness.Store.GetDocsByOrder(78).Any(doc => harness.Store.HasProductionPallets(doc.Id)));
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void MarkPrinted_CustomerOrder_IsNoOp()
    {
        var harness = CreateCustomerHarnessWithBoundHu(
            new OrderReceiptPlanLine
            {
                Id = 501,
                OrderId = 78,
                OrderLineId = 101,
                ItemId = 100,
                ItemName = "Товар",
                QtyPlanned = 600,
                ToHu = "HU-0000478",
                SortOrder = 1
            });
        var service = new ProductionPalletService(harness.Store);

        var updated = service.MarkPrinted(78, new[] { 501L }, new DateTime(2026, 5, 20, 12, 0, 0));

        Assert.Equal(0, updated);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void MarkPrinted_ChangesOnlyPlannedPallets()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 1);
        var service = new ProductionPalletService(harness.Store);

        var updated = service.MarkPrinted(10, new DateTime(2026, 5, 13, 11, 0, 0));

        Assert.Equal(5, updated);
        var pallets = harness.Store.GetProductionPalletsByDoc(20);
        Assert.Equal(ProductionPalletStatus.Filled, pallets[0].Status);
        Assert.All(pallets.Skip(1), pallet => Assert.Equal(ProductionPalletStatus.Printed, pallet.Status));
        Assert.Empty(harness.LedgerEntries);
    }

    private static CloseDocumentHarness CreateHarnessWithSinglePallet(string status)
    {
        var harness = new CloseDocumentHarness();
        SeedBase(harness, orderQty: 600, plannedQty: 600, huCode: "HU-000001");
        harness.SeedProductionPallet(BuildPallet(id: 1, huCode: "HU-000001", plannedQty: 600, status: status));
        return harness;
    }

    private static CloseDocumentHarness CreateCustomerHarnessWithBoundHu(params OrderReceiptPlanLine[] planLines)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар",
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedOrder(new Order
        {
            Id = 78,
            OrderRef = "078",
            Type = OrderType.Customer,
            PartnerName = "ПЕЧАГИН ПРОДУКТ",
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 5, 20, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 78,
            ItemId = 100,
            QtyOrdered = 1200
        });
        if (planLines.Length > 0)
        {
            harness.SeedOrderReceiptPlanLines(78, planLines);
        }

        return harness;
    }

    private static CloseDocumentHarness CreateCustomerPlanningHarness(
        params (long OrderLineId, long ItemId, double QtyOrdered)[] lines)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "086",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            PartnerName = "Клиент",
            CreatedAt = new DateTime(2026, 5, 25, 8, 0, 0)
        });

        foreach (var line in lines)
        {
            harness.SeedItem(new Item
            {
                Id = line.ItemId,
                Name = $"Товар {line.ItemId}",
                BaseUom = "шт",
                MaxQtyPerHu = 600
            });
            harness.SeedOrderLine(new OrderLine
            {
                Id = line.OrderLineId,
                OrderId = 10,
                ItemId = line.ItemId,
                QtyOrdered = line.QtyOrdered,
                ProductionPurpose = ProductionLinePurpose.CustomerOrder
            });
        }

        return harness;
    }

    private static void SeedCustomerBoundHu(
        CloseDocumentHarness harness,
        params (long Id, long OrderLineId, long ItemId, double Qty, string HuCode)[] lines)
    {
        foreach (var line in lines)
        {
            harness.SeedBalance(line.ItemId, 1, line.Qty, line.HuCode);
        }

        harness.SeedOrderReceiptPlanLines(
            10,
            lines.Select((line, index) => new OrderReceiptPlanLine
            {
                Id = line.Id,
                OrderId = 10,
                OrderLineId = line.OrderLineId,
                ItemId = line.ItemId,
                QtyPlanned = line.Qty,
                ToLocationId = 1,
                ToHu = line.HuCode,
                SortOrder = index + 1
            }).ToArray());
    }

    private static ProductionPalletService CreateAutoClosePalletService(CloseDocumentHarness harness)
    {
        var documents = harness.CreateService();
        var fillClose = new ProductionFillCloseService(
            harness.Store,
            documents,
            new FlowStockLedgerFlowOptions { ProductionAutoCloseOnFill = true });
        return new ProductionPalletService(harness.Store, fillClose);
    }

    private static IReadOnlyList<ProductionPallet> GetActivePalletsByOrder(CloseDocumentHarness harness, long orderId)
    {
        return harness.Store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .SelectMany(doc => harness.Store.GetProductionPalletsByDoc(doc.Id))
            .Where(pallet => pallet.Status != ProductionPalletStatus.Cancelled)
            .OrderBy(pallet => pallet.Id)
            .ToArray();
    }

    private static double SumTestCoverageByOrderLine(IEnumerable<ProductionPallet> pallets, long orderLineId)
    {
        return pallets.Sum(pallet =>
        {
            if (pallet.Lines.Count > 0)
            {
                return pallet.Lines
                    .Where(line => line.OrderLineId == orderLineId)
                    .Sum(line => Math.Max(0, line.PlannedQty));
            }

            return pallet.OrderLineId == orderLineId
                ? Math.Max(0, pallet.PlannedQty)
                : 0;
        });
    }

    private static void SeedExistingCustomerPalletPlan(
        CloseDocumentHarness harness,
        double plannedQty,
        string status)
    {
        harness.SeedDoc(new Doc
        {
            Id = 20,
            DocRef = "PRD-2026-000001",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 5, 25, 9, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 201,
            DocId = 20,
            OrderLineId = 101,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ItemId = 100,
            Qty = plannedQty,
            ToLocationId = 1,
            ToHu = "HU-900201",
            PackSingleHu = true
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            PrdDocId = 20,
            DocLineId = 201,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 100,
            ItemName = "Товар 100",
            HuCode = "HU-900201",
            PlannedQty = plannedQty,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = status,
            FilledAt = string.Equals(status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
                ? new DateTime(2026, 5, 25, 10, 0, 0)
                : null,
            CreatedAt = new DateTime(2026, 5, 25, 9, 0, 0)
        });
    }

    private static CloseDocumentHarness CreateHarnessWithFiveInternalOrderLines()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "104",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 26, 8, 0, 0)
        });

        SeedInternalItemAndLine(harness, 101, 100, "Аджика, Печагин, 200 гр", 1824, 1824);
        SeedInternalItemAndLine(harness, 102, 200, "Аджика Печагин, 1 кг", 1134, 378);
        SeedInternalItemAndLine(harness, 103, 300, "Линия с увеличением", 1200, 600);
        SeedInternalItemAndLine(harness, 104, 400, "Плановая линия 4", 600, 600);
        SeedInternalItemAndLine(harness, 105, 500, "Плановая линия 5", 600, 600);
        return harness;
    }

    private static CloseDocumentHarness CreateHarnessWithComponentHeaderMismatch()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "056",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        SeedInternalItemAndLine(harness, 101, 100, "Товар", 600, 600);
        SeedInternalItemAndLine(harness, 102, 200, "Добавка", 200, 400);
        harness.SeedDoc(new Doc
        {
            Id = 20,
            DocRef = "PRD-2026-000001",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            OrderRef = "056",
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 201,
            DocId = 20,
            OrderLineId = 102,
            ItemId = 200,
            Qty = 200,
            ToLocationId = 1,
            ToHu = "HU-0000201",
            PackSingleHu = true
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            PrdDocId = 20,
            DocLineId = 201,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 200,
            ItemName = "Добавка",
            HuCode = "HU-0000201",
            PlannedQty = 800,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0),
            Lines =
            [
                new ProductionPalletComponentLine
                {
                    Id = 1001,
                    ProductionPalletId = 1,
                    DocLineId = 201,
                    OrderLineId = 102,
                    ItemId = 200,
                    ItemName = "Добавка",
                    PlannedQty = 200,
                    CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
                }
            ]
        });
        return harness;
    }

    private static void SeedInternalItemAndLine(
        CloseDocumentHarness harness,
        long orderLineId,
        long itemId,
        string itemName,
        double qtyOrdered,
        double maxQtyPerHu)
    {
        harness.SeedItem(new Item
        {
            Id = itemId,
            Name = itemName,
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = maxQtyPerHu
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = orderLineId,
            OrderId = 10,
            ItemId = itemId,
            QtyOrdered = qtyOrdered
        });
    }

    private static CloseDocumentHarness CreateHarnessWithOrderOnly(double orderQty, double? maxQtyPerHu)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар",
            BaseUom = "шт",
            MaxQtyPerHu = maxQtyPerHu
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "056",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = orderQty
        });
        return harness;
    }

    private static CloseDocumentHarness CreateHarnessWithCustomerTwoOrderLines(
        double firstQty,
        double secondQty,
        double? maxQtyPerHu)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 29,
            Name = "Товар A",
            BaseUom = "шт",
            MaxQtyPerHu = maxQtyPerHu
        });
        harness.SeedItem(new Item
        {
            Id = 13,
            Name = "Товар B",
            BaseUom = "шт",
            MaxQtyPerHu = maxQtyPerHu
        });
        harness.SeedOrder(new Order
        {
            Id = 83,
            OrderRef = "083",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            PartnerName = "ПЕЧАГИН ПРОДУКТ",
            CreatedAt = new DateTime(2026, 5, 22, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 222,
            OrderId = 83,
            ItemId = 29,
            QtyOrdered = firstQty,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 223,
            OrderId = 83,
            ItemId = 13,
            QtyOrdered = secondQty,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        return harness;
    }

    private static CloseDocumentHarness CreateHarnessWithCustomerOrderOnly(double orderQty, double? maxQtyPerHu)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар",
            BaseUom = "шт",
            MaxQtyPerHu = maxQtyPerHu
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "080",
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            PartnerId = 500,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = orderQty,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        return harness;
    }

    private static CloseDocumentHarness CreateHarnessWithTwoOrderLines(double firstQty, double secondQty)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedItem(new Item
        {
            Id = 200,
            Name = "Добавка",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "056",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = firstQty
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 102,
            OrderId = 10,
            ItemId = 200,
            QtyOrdered = secondQty
        });
        return harness;
    }

    [Fact]
    public void FillPallet_WrongSelectedOrder_IsRejected()
    {
        var harness = CreateHarnessWithSinglePallet(ProductionPalletStatus.Planned);
        var service = new ProductionPalletService(harness.Store);

        var result = service.Fill("HU-000001", "TSD-01", orderId: 999, prdDocId: 20);

        Assert.False(result.Success);
        Assert.Equal("Эта паллета относится к другому заказу", result.Error);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void PlanOrder_AfterPrintedPlan_DoesNotReassignHu()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        var huBeforePrint = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Single().HuCode;

        service.MarkPrinted(10, new DateTime(2026, 5, 13, 11, 0, 0));
        var replan = service.PlanOrder(10);

        Assert.Equal(plan.PrdDocId, replan.PrdDocId);
        Assert.Equal(huBeforePrint, harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Single().HuCode);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void CancelOrderPlan_RemovesPlannedPallets_AndAllowsReplan()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);

        var cancel = service.CancelOrderPlan(10);

        Assert.Equal(plan.PrdDocId, cancel.PrdDocId);
        Assert.Equal(2, cancel.RemovedPalletCount);
        Assert.Equal(2, cancel.RemovedLineCount);
        Assert.False(harness.Store.HasProductionPallets(plan.PrdDocId));
        Assert.Empty(harness.Store.GetDocLines(plan.PrdDocId));
        Assert.Null(harness.Store.GetDoc(plan.PrdDocId));
        Assert.Empty(harness.LedgerEntries);

        var replan = service.PlanOrder(10);
        Assert.Equal(2, replan.Summary.PlannedPalletCount);
        Assert.Equal(1200, replan.Summary.PlannedQty);
    }

    [Fact]
    public void CancelOrderPlan_AllowsPrintedPallets()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        service.MarkPrinted(10, new DateTime(2026, 5, 13, 11, 0, 0));

        var cancel = service.CancelOrderPlan(10);

        Assert.Equal(1, cancel.RemovedPalletCount);
        Assert.False(harness.Store.HasProductionPallets(plan.PrdDocId));
        var replan = service.PlanOrder(10);
        Assert.Equal(1, replan.Summary.PlannedPalletCount);
    }

    [Fact]
    public void CancelOrderPlan_WithFilledAndPrinted_RemovesOnlyPrintedAndKeepsFilled()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        var pallets = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).OrderBy(pallet => pallet.Id).ToArray();
        service.MarkPrinted(10, [pallets[1].Id], new DateTime(2026, 5, 13, 11, 0, 0));
        harness.Store.MarkProductionPalletFilled(pallets[0].Id, new DateTime(2026, 5, 13, 12, 0, 0), "TSD-01");
        harness.SeedLedgerEntry(plan.PrdDocId, pallets[0].ItemId, pallets[0].ToLocationId ?? 1, pallets[0].PlannedQty, pallets[0].HuCode);
        var ledgerBefore = harness.LedgerEntries.Count;

        var cancel = service.CancelOrderPlan(10, [pallets[1].Id]);

        Assert.Equal(1, cancel.RemovedPalletCount);
        Assert.Equal(1, cancel.RemovedLineCount);
        var remaining = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId);
        var filled = Assert.Single(remaining);
        Assert.Equal(pallets[0].Id, filled.Id);
        Assert.Equal(ProductionPalletStatus.Filled, filled.Status);
        Assert.Single(harness.Store.GetDocLines(plan.PrdDocId));
        Assert.Equal(ledgerBefore, harness.LedgerEntries.Count);
    }

    [Fact]
    public void CancelOrderPlan_WithTwoPrintedPallets_RemovesOnlyRequestedPallet()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        var pallets = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).OrderBy(pallet => pallet.Id).ToArray();
        service.MarkPrinted(10, pallets.Select(pallet => pallet.Id).ToArray(), new DateTime(2026, 5, 13, 11, 0, 0));

        var cancel = service.CancelOrderPlan(10, [pallets[0].Id]);

        Assert.Equal(new[] { pallets[0].Id }, cancel.RequestedPalletIds);
        Assert.Equal(new[] { pallets[0].Id }, cancel.RemovedPalletIds);
        Assert.Empty(cancel.SkippedPalletIds);
        Assert.Equal(1, cancel.RemovedPalletCount);
        var remaining = Assert.Single(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId));
        Assert.Equal(pallets[1].Id, remaining.Id);
        Assert.Equal(ProductionPalletStatus.Printed, remaining.Status);
    }

    [Fact]
    public async Task CancelPlanEndpoint_WithEmptyPalletIds_RemovesNothingAndReturnsValidation()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        var before = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Select(pallet => pallet.Id).Order().ToArray();
        await using var host = await ProductionPalletTsdHttpHost.StartAsync(harness);

        using var response = await host.Client.PostAsJsonAsync(
            "/api/orders/10/production-pallets/cancel-plan",
            new { pallet_ids = Array.Empty<long>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Select(pallet => pallet.Id).Order().ToArray());
    }

    [Fact]
    public void CancelPlanOptions_AllFilledPalletsAreDisabled()
    {
        var harness = CreateHarnessWithSinglePallet(ProductionPalletStatus.Filled);
        var service = new ProductionPalletService(harness.Store);

        var options = service.GetCancelPlanOptions(10);
        var cancel = service.CancelOrderPlan(10, options.Rows.Select(row => row.PalletId).ToArray());

        var row = Assert.Single(options.Rows);
        Assert.False(row.IsSelectable);
        Assert.False(row.IsSelectedByDefault);
        Assert.Equal("Нельзя удалить: паллета уже наполнена/выпущена", row.DisabledReason);
        Assert.Equal(0, cancel.RemovedPalletCount);
        Assert.Equal(ProductionPalletStatus.Filled, Assert.Single(harness.Store.GetProductionPalletsByDoc(20)).Status);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void CancelOrderPlan_SelectedPlannedPallet_RemovesRelatedDraftPrdLine()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        var pallet = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).OrderBy(p => p.Id).First();

        var cancel = service.CancelOrderPlan(10, [pallet.Id]);

        Assert.Equal(1, cancel.RemovedPalletCount);
        Assert.Equal(1, cancel.RemovedLineCount);
        Assert.DoesNotContain(harness.Store.GetDocLines(plan.PrdDocId), line => line.Id == pallet.DocLineId);
        Assert.DoesNotContain(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId), p => p.Id == pallet.Id);
        Assert.Single(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId));
        Assert.NotNull(harness.Store.GetDoc(plan.PrdDocId));
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void CancelOrderPlan_SelectedLastPlannedPallet_DeletesEmptyDraftPrd()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        var pallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId));

        var cancel = service.CancelOrderPlan(10, [pallet.Id]);

        Assert.Equal(1, cancel.RemovedPalletCount);
        Assert.Equal(1, cancel.RemovedLineCount);
        Assert.Null(harness.Store.GetDoc(plan.PrdDocId));
        Assert.Empty(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId));
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void CancelOrderPlan_DoesNotModifyClosedProductionReceipt()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        var doc = harness.Store.GetDoc(plan.PrdDocId)!;
        harness.Store.UpdateDocStatus(doc.Id, DocStatus.Closed, new DateTime(2026, 5, 13, 12, 0, 0));

        var result = service.CancelOrderPlan(10);

        Assert.Equal(0, result.RemovedPalletCount);
        Assert.NotNull(harness.Store.GetDoc(plan.PrdDocId));
        Assert.Single(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId));
        Assert.Single(harness.Store.GetDocLines(plan.PrdDocId));
    }

    [Fact]
    public void CancelOrderPlan_AfterQtyChange_ReplansByCurrentQty()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        service.CancelOrderPlan(10);

        var orderLine = harness.Store.GetOrderLines(10).Single();
        harness.Store.UpdateOrderLineQty(orderLine.Id, 600);

        var replan = service.PlanOrder(10);
        Assert.Equal(1, replan.Summary.PlannedPalletCount);
        Assert.Equal(600, replan.Summary.PlannedQty);
        Assert.Single(harness.Store.GetDocLines(replan.PrdDocId));
    }

    [Fact]
    public void AdoptPlanFromInternal_MovesPlannedPalletsToCustomer()
    {
        var harness = CreateHarnessForAdopt();
        var service = new ProductionPalletService(harness.Store);

        var result = service.AdoptPlanFromInternal(targetCustomerOrderId: 67, sourceInternalOrderId: 66);

        Assert.True(result.Success);
        Assert.Equal(162, result.SourcePrdDocId);
        Assert.True(result.TargetPrdDocId > 0);
        Assert.Equal(2, result.TransferredPalletCount);
        Assert.Equal(2, result.TransferredLineCount);
        Assert.Equal(new[] { "HU-0000462", "HU-0000463" }, result.TransferredHuCodes.Order().ToArray());
        Assert.False(harness.Store.HasProductionPallets(162));
        Assert.Empty(harness.Store.GetDocLines(162));
        Assert.Null(harness.Store.GetDoc(162));
        Assert.DoesNotContain(harness.Store.GetDocsByOrder(66), doc => doc.Id == 162);
        var targetPallets = harness.Store.GetProductionPalletsByDoc(result.TargetPrdDocId);
        Assert.Equal(2, targetPallets.Count);
        Assert.All(targetPallets, pallet =>
        {
            Assert.Equal(67, pallet.OrderId);
            Assert.Equal(172, pallet.OrderLineId);
            Assert.Equal(result.TargetPrdDocId, pallet.PrdDocId);
        });
        Assert.All(harness.Store.GetDocLines(result.TargetPrdDocId), line =>
        {
            Assert.Equal(172, line.OrderLineId);
            Assert.Equal(ProductionLinePurpose.CustomerOrder, line.ProductionPurpose);
        });
        Assert.Contains(harness.Store.GetActiveProductionPalletWorkItems(), item =>
            item.OrderId == 67
            && item.Summary.PlannedPalletCount == 2
            && item.Summary.FilledPalletCount == 0);
        Assert.Equal("MERGED", result.SourceOrderStatus);
        Assert.True(result.SourceOrderCommentUpdated);
        Assert.Equal(OrderStatus.Merged, harness.Store.GetOrder(66)?.Status);
        Assert.Contains("Объединён с заказом №067", harness.Store.GetOrder(66)?.Comment ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Store.GetActiveProductionPalletWorkItems(), item => item.OrderId == 66);
    }

    [Fact]
    public void AdoptPlanFromInternal_RejectsFilledPallet()
    {
        var harness = CreateHarnessForAdopt(ProductionPalletStatus.Filled);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<ProductionPalletPlanAdoptionException>(() => service.AdoptPlanFromInternal(67, 66));

        Assert.Equal("SOURCE_HAS_FILLED_PALLETS", ex.Code);
    }

    [Fact]
    public void AdoptPlanFromInternal_RejectsClosedSourcePrd()
    {
        var harness = CreateHarnessForAdopt(sourceDocStatus: DocStatus.Closed);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<ProductionPalletPlanAdoptionException>(() => service.AdoptPlanFromInternal(67, 66));

        Assert.Equal("SOURCE_PRD_CLOSED", ex.Code);
    }

    [Fact]
    public void AdoptPlanFromInternal_RejectsSourceLedger()
    {
        var harness = CreateHarnessForAdopt();
        harness.Store.AddLedgerEntry(new LedgerEntry
        {
            DocId = 162,
            ItemId = 100,
            LocationId = 1,
            QtyDelta = 600,
            HuCode = "HU-0000462",
            Timestamp = DateTime.Now
        });
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<ProductionPalletPlanAdoptionException>(() => service.AdoptPlanFromInternal(67, 66));

        Assert.Equal("SOURCE_HAS_LEDGER", ex.Code);
    }

    [Fact]
    public void AdoptPlanFromInternal_RejectsTargetExistingPlan()
    {
        var harness = CreateHarnessForAdopt(targetHasPlan: true);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<ProductionPalletPlanAdoptionException>(() => service.AdoptPlanFromInternal(67, 66));

        Assert.Equal("TARGET_ALREADY_HAS_PALLET_PLAN", ex.Code);
    }

    [Fact]
    public void AdoptPlanFromInternal_RejectsMissingTargetLine()
    {
        var harness = CreateHarnessForAdopt(targetHasMatchingLine: false);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<ProductionPalletPlanAdoptionException>(() => service.AdoptPlanFromInternal(67, 66));

        Assert.Equal("TARGET_LINE_NOT_FOUND", ex.Code);
    }

    private static CloseDocumentHarness CreateHarnessWithFourLineTwoMixedGroups()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар A",
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedItem(new Item
        {
            Id = 200,
            Name = "Товар B",
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = 400
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "056",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine { Id = 101, OrderId = 10, ItemId = 100, QtyOrdered = 700, ProductionPalletGroup = "MIX-1" });
        harness.SeedOrderLine(new OrderLine { Id = 102, OrderId = 10, ItemId = 200, QtyOrdered = 500, ProductionPalletGroup = "MIX-1" });
        harness.SeedOrderLine(new OrderLine { Id = 103, OrderId = 10, ItemId = 100, QtyOrdered = 700, ProductionPalletGroup = "MIX-2" });
        harness.SeedOrderLine(new OrderLine { Id = 104, OrderId = 10, ItemId = 200, QtyOrdered = 500, ProductionPalletGroup = "MIX-2" });
        return harness;
    }

    private static CloseDocumentHarness CreateHarnessWithFourLineSingleMixedGroupOverCapacity()
    {
        var harness = CreateHarnessWithFourLineTwoMixedGroups();
        harness.Store.UpdateOrderLineProductionPalletGroup(103, "MIX-1");
        harness.Store.UpdateOrderLineProductionPalletGroup(104, "MIX-1");
        return harness;
    }

    private static CloseDocumentHarness CreateHarnessWithMixedOrderOnly()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар",
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedItem(new Item
        {
            Id = 200,
            Name = "Добавка",
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = 400
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "056",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = 300,
            ProductionPalletGroup = "MIX-1"
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 102,
            OrderId = 10,
            ItemId = 200,
            QtyOrdered = 200,
            ProductionPalletGroup = "MIX-1"
        });
        return harness;
    }

    private static CloseDocumentHarness CreateHarnessForAdopt(
        string sourcePalletStatus = ProductionPalletStatus.Planned,
        DocStatus sourceDocStatus = DocStatus.Draft,
        bool targetHasPlan = false,
        bool targetHasMatchingLine = true)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар",
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedOrder(new Order
        {
            Id = 66,
            OrderRef = "066",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 18, 16, 58, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 171,
            OrderId = 66,
            ItemId = 100,
            QtyOrdered = 0
        });
        harness.SeedOrder(new Order
        {
            Id = 67,
            OrderRef = "067",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 5, 18, 16, 58, 34)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 172,
            OrderId = 67,
            ItemId = targetHasMatchingLine ? 100 : 200,
            QtyOrdered = 2400
        });
        harness.SeedDoc(new Doc
        {
            Id = 162,
            DocRef = "PRD-2026-000156",
            Type = DocType.ProductionReceipt,
            Status = sourceDocStatus,
            OrderId = 66,
            OrderRef = "066",
            CreatedAt = new DateTime(2026, 5, 18, 16, 58, 10)
        });
        harness.SeedLine(new DocLine
        {
            Id = 1752,
            DocId = 162,
            OrderLineId = 171,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ItemId = 100,
            Qty = 600,
            ToLocationId = 1,
            ToHu = "HU-0000462",
            PackSingleHu = true
        });
        harness.SeedLine(new DocLine
        {
            Id = 1753,
            DocId = 162,
            OrderLineId = 171,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ItemId = 100,
            Qty = 600,
            ToLocationId = 1,
            ToHu = "HU-0000463",
            PackSingleHu = true
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 35,
            PrdDocId = 162,
            DocLineId = 1752,
            OrderId = 66,
            OrderLineId = 171,
            ItemId = 100,
            ItemName = "Товар",
            HuCode = "HU-0000462",
            PlannedQty = 600,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = sourcePalletStatus,
            CreatedAt = new DateTime(2026, 5, 18, 16, 58, 10)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 36,
            PrdDocId = 162,
            DocLineId = 1753,
            OrderId = 66,
            OrderLineId = 171,
            ItemId = 100,
            ItemName = "Товар",
            HuCode = "HU-0000463",
            PlannedQty = 600,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = sourcePalletStatus,
            CreatedAt = new DateTime(2026, 5, 18, 16, 58, 10)
        });

        if (targetHasPlan)
        {
            harness.SeedDoc(new Doc
            {
                Id = 163,
                DocRef = "PRD-2026-000157",
                Type = DocType.ProductionReceipt,
                Status = DocStatus.Draft,
                OrderId = 67,
                OrderRef = "067",
                CreatedAt = new DateTime(2026, 5, 18, 17, 13, 7)
            });
            harness.SeedLine(new DocLine
            {
                Id = 1754,
                DocId = 163,
                OrderLineId = 172,
                ProductionPurpose = ProductionLinePurpose.CustomerOrder,
                ItemId = 100,
                Qty = 600,
                ToLocationId = 1,
                ToHu = "HU-0000464",
                PackSingleHu = true
            });
            harness.SeedProductionPallet(new ProductionPallet
            {
                Id = 37,
                PrdDocId = 163,
                DocLineId = 1754,
                OrderId = 67,
                OrderLineId = 172,
                ItemId = 100,
                ItemName = "Товар",
                HuCode = "HU-0000464",
                PlannedQty = 600,
                ToLocationId = 1,
                ToLocationCode = "MAIN",
                Status = ProductionPalletStatus.Planned,
                CreatedAt = new DateTime(2026, 5, 18, 17, 13, 7)
            });
        }

        return harness;
    }

    private static CloseDocumentHarness CreateHarnessWithSixPallets(int filledCount)
    {
        var harness = new CloseDocumentHarness();
        SeedBase(harness, orderQty: 3600, plannedQty: 600, huCode: "HU-000001");
        for (var i = 1; i <= 6; i++)
        {
            harness.SeedProductionPallet(BuildPallet(
                id: i,
                huCode: $"HU-00000{i}",
                plannedQty: 600,
                status: i <= filledCount ? ProductionPalletStatus.Filled : ProductionPalletStatus.Planned));
        }

        return harness;
    }

    private static void SeedBase(CloseDocumentHarness harness, double orderQty, double plannedQty, string huCode)
    {
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item { Id = 100, Name = "Товар", Brand = "Печагин", BaseUom = "шт" });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "056",
            Type = OrderType.Internal,
            PartnerName = "ПЕЧАГИН ПРОДУКТ",
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = orderQty
        });
        harness.SeedDoc(new Doc
        {
            Id = 20,
            DocRef = "PRD-2026-000001",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 201,
            DocId = 20,
            OrderLineId = 101,
            ItemId = 100,
            Qty = plannedQty,
            ToLocationId = 1,
            ToHu = huCode
        });
    }

    private static ProductionPallet BuildPallet(
        long id,
        string huCode,
        double plannedQty,
        string status = ProductionPalletStatus.Planned)
    {
        return new ProductionPallet
        {
            Id = id,
            PrdDocId = 20,
            DocLineId = 201,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 100,
            ItemName = "Товар",
            HuCode = huCode,
            PlannedQty = plannedQty,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = status,
            FilledAt = status == ProductionPalletStatus.Filled ? new DateTime(2026, 5, 13, 10, 0, 0) : null,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        };
    }
}

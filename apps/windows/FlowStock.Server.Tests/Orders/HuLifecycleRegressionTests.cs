using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Orders;

public sealed class HuLifecycleRegressionTests
{
    private const long ItemId = 6;
    private const long LocationId = 1;
    private const long InternalOrderId = 167;
    private const long InternalLineId = 16701;
    private const long CustomerOrderId = 170;
    private const long CustomerLineId = 17001;
    private const double PalletQty = 600;

    [Fact]
    public void ProductionPalletPlan_GeneratesUniqueHuCodes_ForInternalOrder()
    {
        var harness = CreateLifecycleHarness();
        var pallets = CreatePalletService(harness);

        var plan = pallets.PlanOrder(InternalOrderId);
        var planned = harness.Store.GetProductionPalletsByOrder(InternalOrderId)
            .OrderBy(pallet => pallet.Id)
            .ToArray();

        Assert.Equal(6, planned.Length);
        Assert.All(planned, pallet =>
        {
            Assert.False(string.IsNullOrWhiteSpace(pallet.HuCode));
            Assert.Equal(InternalOrderId, pallet.OrderId);
            Assert.Equal(ProductionPalletStatus.Planned, pallet.Status);
        });
        Assert.Equal(6, planned.Select(pallet => pallet.HuCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Empty(harness.LedgerEntries);
        AssertNoDuplicateProductionPalletHus(harness, InternalOrderId);
    }

    [Fact]
    public void ProductionPalletPlan_IsIdempotent_DoesNotRegenerateHuCodes()
    {
        var harness = CreateLifecycleHarness();
        var pallets = CreatePalletService(harness);

        var first = pallets.PlanOrder(InternalOrderId);
        var firstHuCodes = harness.Store.GetProductionPalletsByOrder(InternalOrderId)
            .OrderBy(pallet => pallet.Id)
            .Select(pallet => pallet.HuCode)
            .ToArray();
        var second = pallets.PlanOrder(InternalOrderId);
        var secondHuCodes = harness.Store.GetProductionPalletsByOrder(InternalOrderId)
            .OrderBy(pallet => pallet.Id)
            .Select(pallet => pallet.HuCode)
            .ToArray();

        Assert.False(first.WasExisting);
        Assert.True(second.WasExisting);
        Assert.Equal(first.PrdDocId, second.PrdDocId);
        Assert.Equal(firstHuCodes, secondHuCodes);
        Assert.Equal(6, secondHuCodes.Length);
        Assert.Equal(6, secondHuCodes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Empty(harness.LedgerEntries);
        AssertNoDuplicateProductionPalletHus(harness, InternalOrderId);
    }

    [Fact]
    public void InternalPlanFillAndCustomerReservation_PreservesHuIdentityAndProductionNeedMath()
    {
        var harness = CreateLifecycleHarness();
        var pallets = CreatePalletService(harness);
        var internalPlan = pallets.PlanOrder(InternalOrderId);
        var plannedInternalHus = harness.Store.GetProductionPalletsByOrder(InternalOrderId)
            .OrderBy(pallet => pallet.Id)
            .Select(pallet => pallet.HuCode)
            .ToArray();

        FillHus(pallets, InternalOrderId, internalPlan.PrdDocId, plannedInternalHus.Take(2));
        var filledInternalHus = plannedInternalHus.Take(2).ToArray();
        SeedCustomerOrder(harness, CustomerOrderId, CustomerLineId, "CUST-170", qty: 2400);

        var reservation = new OrderProducedHuReservationService(harness.Store).Reserve(new OrderProducedHuReservationRequest
        {
            SourceInternalOrderId = InternalOrderId,
            TargetCustomerOrderId = CustomerOrderId,
            TargetOrderLineId = CustomerLineId,
            ItemId = ItemId,
            HuCodes = filledInternalHus
        });

        Assert.Equal(6, plannedInternalHus.Length);
        Assert.Equal(6, plannedInternalHus.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(filledInternalHus, reservation.ReservedHuCodes.OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.Equal(1200, reservation.QtyReserved);
        AssertCustomerReservationState(harness, filledInternalHus);
        AssertProductionNeed(harness, toCloseOrdersQty: 1200, openInternalOrderQty: 2400, qtyToCreate: 1200);
        AssertNoDuplicateProductionPalletHus(harness, InternalOrderId, CustomerOrderId);
        AssertFilledPalletsHaveSinglePositiveLedgerReceipt(harness, InternalOrderId);
    }

    [Fact]
    public void ReservedHu_CannotBeReservedTwiceToDifferentOrders()
    {
        var scenario = CreateReservedCustomerScenario();
        const long secondCustomerOrderId = 171;
        const long secondCustomerLineId = 17101;
        SeedCustomerOrder(scenario.Harness, secondCustomerOrderId, secondCustomerLineId, "CUST-171", qty: 1200);

        var service = new OrderProducedHuReservationService(scenario.Harness.Store);
        var ex = Assert.Throws<InvalidOperationException>(() => service.Reserve(new OrderProducedHuReservationRequest
        {
            SourceInternalOrderId = InternalOrderId,
            TargetCustomerOrderId = secondCustomerOrderId,
            TargetOrderLineId = secondCustomerLineId,
            ItemId = ItemId,
            HuCodes = scenario.ReservedHus
        }));

        Assert.Contains("другой клиентский заказ", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(scenario.Harness.GetOrderReceiptPlanLines(secondCustomerOrderId));
        Assert.Equal(2, scenario.Harness.GetOrderReceiptPlanLines(CustomerOrderId).Count);
    }

    [Fact]
    public void RepeatProducedHuReservation_DoesNotDuplicateCustomerPlanRows()
    {
        var scenario = CreateReservedCustomerScenario();
        var service = new OrderProducedHuReservationService(scenario.Harness.Store);

        var ex = Assert.Throws<InvalidOperationException>(() => service.Reserve(new OrderProducedHuReservationRequest
        {
            SourceInternalOrderId = InternalOrderId,
            TargetCustomerOrderId = CustomerOrderId,
            TargetOrderLineId = CustomerLineId,
            ItemId = ItemId,
            HuCodes = scenario.ReservedHus
        }));

        Assert.Contains("уже зарезервированы", ex.Message, StringComparison.OrdinalIgnoreCase);
        var plan = scenario.Harness.GetOrderReceiptPlanLines(CustomerOrderId);
        Assert.Equal(2, plan.Count);
        Assert.Equal(2, plan.Select(line => line.ToHu).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(1200, plan.Sum(line => line.QtyPlanned));
    }

    [Fact]
    public void CustomerShortageProduction_UsesCustomerOwnedUniqueHu_AndInternalHusStayInternal()
    {
        var scenario = CreateReservedCustomerScenario();
        var harness = scenario.Harness;
        var pallets = scenario.PalletService;
        var allInternalHusBeforeCustomerPlan = GetOrderPallets(harness, InternalOrderId)
            .Select(pallet => pallet.HuCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var customerPlan = pallets.PlanOrder(CustomerOrderId);
        var customerPallets = harness.Store.GetProductionPalletsByOrder(CustomerOrderId)
            .OrderBy(pallet => pallet.Id)
            .ToArray();
        var customerHus = customerPallets.Select(pallet => pallet.HuCode).ToArray();

        Assert.Equal(2, customerPallets.Length);
        Assert.Equal(1200, customerPallets.Sum(pallet => pallet.PlannedQty));
        Assert.Equal(2, customerHus.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(customerHus, allInternalHusBeforeCustomerPlan.Contains);
        Assert.All(customerPallets, pallet =>
        {
            Assert.Equal(CustomerOrderId, pallet.OrderId);
            Assert.Equal(ProductionPalletStatus.Planned, pallet.Status);
        });
        Assert.All(GetOrderPallets(harness, InternalOrderId), pallet => Assert.Equal(InternalOrderId, pallet.OrderId));
        Assert.Equal(2, harness.LedgerEntries.Count);

        FillHus(pallets, CustomerOrderId, customerPlan.PrdDocId, customerHus);

        foreach (var huCode in customerHus)
        {
            var pallet = harness.Store.GetProductionPalletByHu(huCode);
            Assert.NotNull(pallet);
            Assert.Equal(CustomerOrderId, pallet.OrderId);
            Assert.Equal(ProductionPalletStatus.Filled, pallet.Status);
            Assert.Single(harness.LedgerEntries.Where(entry =>
                entry.QtyDelta > 0
                && string.Equals(entry.HuCode, huCode, StringComparison.OrdinalIgnoreCase)));
        }

        Assert.Equal(0, harness.Store.GetOrderReceiptRemaining(CustomerOrderId).Single().QtyRemaining);
        Assert.All(GetOrderPallets(harness, InternalOrderId), pallet => Assert.Equal(InternalOrderId, pallet.OrderId));
        AssertNoDuplicateProductionPalletHus(harness, InternalOrderId, CustomerOrderId);
    }

    [Fact]
    public void InternalRemainingFill_AfterCustomerReservation_GoesToFreeStockNotCustomerAutomatically()
    {
        var scenario = CreateReservedCustomerScenario();
        var harness = scenario.Harness;
        var pallets = scenario.PalletService;
        var customerPlan = pallets.PlanOrder(CustomerOrderId);
        var customerHus = harness.Store.GetProductionPalletsByOrder(CustomerOrderId)
            .Select(pallet => pallet.HuCode)
            .ToArray();
        FillHus(pallets, CustomerOrderId, customerPlan.PrdDocId, customerHus);

        var remainingInternalHus = GetOrderPallets(harness, InternalOrderId)
            .Where(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pallet => pallet.Id)
            .Select(pallet => pallet.HuCode)
            .ToArray();
        FillHus(pallets, InternalOrderId, 0, remainingInternalHus);

        var customerReservedPlan = harness.GetOrderReceiptPlanLines(CustomerOrderId);
        Assert.Equal(4, customerReservedPlan.Count);
        Assert.Equal(2400, customerReservedPlan.Sum(line => line.QtyPlanned));
        Assert.DoesNotContain(customerReservedPlan, line => remainingInternalHus.Contains(line.ToHu, StringComparer.OrdinalIgnoreCase));
        foreach (var huCode in remainingInternalHus)
        {
            var context = harness.Store.GetHuOrderContextRows().Single(row =>
                row.ItemId == ItemId
                && string.Equals(row.HuCode, huCode, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(InternalOrderId, context.OriginInternalOrderId);
            Assert.Null(context.ReservedCustomerOrderId);
        }

        var stock = harness.Store.GetStock(null).Single(row => row.ItemId == ItemId);
        Assert.Equal(4800, stock.Qty);
        Assert.Equal(2400, stock.ReservedCustomerOrderQty);
        Assert.Equal(2400, stock.Qty - stock.ReservedCustomerOrderQty);
        AssertProductionNeed(harness, toCloseOrdersQty: 0, openInternalOrderQty: 0, qtyToCreate: 1200);
        AssertNoDuplicateProductionPalletHus(harness, InternalOrderId, CustomerOrderId);
    }

    private static LifecycleScenario CreateReservedCustomerScenario()
    {
        var harness = CreateLifecycleHarness();
        var pallets = CreatePalletService(harness);
        var internalPlan = pallets.PlanOrder(InternalOrderId);
        var plannedInternalHus = harness.Store.GetProductionPalletsByOrder(InternalOrderId)
            .OrderBy(pallet => pallet.Id)
            .Select(pallet => pallet.HuCode)
            .ToArray();
        var reservedHus = plannedInternalHus.Take(2).ToArray();

        FillHus(pallets, InternalOrderId, internalPlan.PrdDocId, reservedHus);
        SeedCustomerOrder(harness, CustomerOrderId, CustomerLineId, "CUST-170", qty: 2400);
        new OrderProducedHuReservationService(harness.Store).Reserve(new OrderProducedHuReservationRequest
        {
            SourceInternalOrderId = InternalOrderId,
            TargetCustomerOrderId = CustomerOrderId,
            TargetOrderLineId = CustomerLineId,
            ItemId = ItemId,
            HuCodes = reservedHus
        });

        return new LifecycleScenario(harness, pallets, reservedHus);
    }

    private static CloseDocumentHarness CreateLifecycleHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location
        {
            Id = LocationId,
            Code = "FG-01",
            Name = "Готовая продукция"
        });
        harness.SeedPartner(new Partner
        {
            Id = 500,
            Code = "CUST-500",
            Name = "Тестовый клиент",
            CreatedAt = new DateTime(2026, 5, 25, 8, 0, 0)
        });
        harness.SeedItemType(new ItemType
        {
            Id = 60,
            Name = "Готовая продукция",
            EnableMinStockControl = true,
            EnableOrderReservation = true
        });
        harness.SeedItem(new Item
        {
            Id = ItemId,
            Name = "Горчица, Печагин, 1 кг",
            Brand = "Печагин",
            BaseUom = "шт",
            ItemTypeId = 60,
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMinStockControl = true,
            MinStockQty = 3600,
            MaxQtyPerHu = PalletQty
        });
        harness.SeedOrder(new Order
        {
            Id = InternalOrderId,
            OrderRef = "001",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 25, 9, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = InternalLineId,
            OrderId = InternalOrderId,
            ItemId = ItemId,
            QtyOrdered = 3600,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
        return harness;
    }

    private static void SeedCustomerOrder(
        CloseDocumentHarness harness,
        long orderId,
        long orderLineId,
        string orderRef,
        double qty)
    {
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = orderRef,
            Type = OrderType.Customer,
            PartnerId = 500,
            PartnerName = "Тестовый клиент",
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 5, 25, 10, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = orderLineId,
            OrderId = orderId,
            ItemId = ItemId,
            QtyOrdered = qty,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
    }

    private static ProductionPalletService CreatePalletService(CloseDocumentHarness harness)
    {
        var documents = harness.CreateService();
        var fillClose = new ProductionFillCloseService(
            harness.Store,
            documents,
            new FlowStockLedgerFlowOptions { ProductionAutoCloseOnFill = true });
        return new ProductionPalletService(harness.Store, fillClose);
    }

    private static void FillHus(
        ProductionPalletService service,
        long orderId,
        long prdDocId,
        IEnumerable<string> huCodes)
    {
        foreach (var huCode in huCodes)
        {
            var result = service.Fill(huCode, "TSD-HU-LIFECYCLE", orderId: orderId, prdDocId: prdDocId);
            Assert.True(result.Success, result.Error);
            Assert.False(result.AlreadyFilled);
            Assert.True(result.PrdAutoClosed);
        }
    }

    private static IReadOnlyList<ProductionPallet> GetOrderPallets(CloseDocumentHarness harness, long orderId)
    {
        return harness.Store.GetProductionPalletsByOrder(orderId)
            .OrderBy(pallet => pallet.Id)
            .ToArray();
    }

    private static void AssertCustomerReservationState(CloseDocumentHarness harness, IReadOnlyList<string> reservedHus)
    {
        var plan = harness.GetOrderReceiptPlanLines(CustomerOrderId);
        Assert.Equal(2, plan.Count);
        Assert.Equal(1200, plan.Sum(line => line.QtyPlanned));
        Assert.Equal(reservedHus.OrderBy(code => code, StringComparer.OrdinalIgnoreCase), plan.Select(line => line.ToHu).OrderBy(code => code, StringComparer.OrdinalIgnoreCase));

        foreach (var huCode in reservedHus)
        {
            var pallet = harness.Store.GetProductionPalletByHu(huCode);
            Assert.NotNull(pallet);
            Assert.Equal(InternalOrderId, pallet.OrderId);
            Assert.Equal(ProductionPalletStatus.Filled, pallet.Status);
            Assert.Single(harness.LedgerEntries.Where(entry =>
                entry.ItemId == ItemId
                && entry.QtyDelta == PalletQty
                && string.Equals(entry.HuCode, huCode, StringComparison.OrdinalIgnoreCase)));
        }

        var customerRemaining = harness.Store.GetOrderReceiptRemaining(CustomerOrderId).Single();
        Assert.Equal(1200, customerRemaining.QtyReceived);
        Assert.Equal(1200, customerRemaining.QtyRemaining);

        var stock = harness.Store.GetStock(null).Single(row => row.ItemId == ItemId);
        Assert.Equal(1200, stock.Qty);
        Assert.Equal(1200, stock.ReservedCustomerOrderQty);
        Assert.Equal(0, stock.Qty - stock.ReservedCustomerOrderQty);
    }

    private static void AssertProductionNeed(
        CloseDocumentHarness harness,
        double toCloseOrdersQty,
        double openInternalOrderQty,
        double qtyToCreate)
    {
        var row = Assert.Single(new ProductionNeedService(harness.Store).GetRows(includeZeroNeed: true));
        Assert.Equal(ItemId, row.ItemId);
        Assert.Equal(toCloseOrdersQty, row.ToCloseOrdersQty);
        Assert.Equal(openInternalOrderQty, row.OpenInternalOrderQty);
        Assert.Equal(qtyToCreate, row.QtyToCreate);
    }

    private static void AssertNoDuplicateProductionPalletHus(CloseDocumentHarness harness, params long[] orderIds)
    {
        var duplicates = orderIds
            .SelectMany(orderId => GetOrderPallets(harness, orderId))
            .Where(pallet => !string.IsNullOrWhiteSpace(pallet.HuCode))
            .GroupBy(pallet => pallet.HuCode.Trim().ToUpperInvariant())
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(duplicates);
    }

    private static void AssertFilledPalletsHaveSinglePositiveLedgerReceipt(CloseDocumentHarness harness, long orderId)
    {
        foreach (var pallet in GetOrderPallets(harness, orderId)
                     .Where(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Single(harness.LedgerEntries.Where(entry =>
                entry.ItemId == pallet.ItemId
                && entry.QtyDelta > 0
                && string.Equals(entry.HuCode, pallet.HuCode, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private sealed record LifecycleScenario(
        CloseDocumentHarness Harness,
        ProductionPalletService PalletService,
        IReadOnlyList<string> ReservedHus);
}

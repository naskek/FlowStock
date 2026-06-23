using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Orders;

public sealed class ReplaceOrderReceiptPlanLinesBatchTests
{
    private const long ItemId = 6;
    private const long OrderA = 101;
    private const long LineA = 1001;
    private const long OrderB = 102;
    private const long LineB = 1002;
    private const long OrderC = 103;
    private const long LineC = 1003;
    private const long LineA2 = 1004;

    [Fact]
    public void ReplaceBatch_NormalReplacement_DeletesScopeAndInsertsFinal()
    {
        var harness = new CloseDocumentHarness();
        SeedCustomerOrder(harness, OrderA, "SO-A");
        harness.SeedOrderReceiptPlanLines(OrderA, PlanLine(OrderA, LineA, "HU-1", 600));

        harness.Store.ReplaceOrderReceiptPlanLinesBatch(
            [new OrderReceiptPlanLineKey(OrderA, LineA)],
            [PlanLine(OrderA, LineA, "HU-2", 600)]);

        var plan = harness.GetOrderReceiptPlanLines(OrderA);
        Assert.Equal("HU-2", Assert.Single(plan).ToHu);
    }

    [Fact]
    public void ReplaceBatch_CyclicSwapBetweenTwoOrders_SwapsWithoutConflict()
    {
        var harness = new CloseDocumentHarness();
        SeedCustomerOrder(harness, OrderA, "SO-A");
        SeedCustomerOrder(harness, OrderB, "SO-B");
        harness.SeedOrderReceiptPlanLines(OrderA, PlanLine(OrderA, LineA, "HU-1", 600));
        harness.SeedOrderReceiptPlanLines(OrderB, PlanLine(OrderB, LineB, "HU-2", 600));

        harness.Store.ReplaceOrderReceiptPlanLinesBatch(
            [new OrderReceiptPlanLineKey(OrderA, LineA), new OrderReceiptPlanLineKey(OrderB, LineB)],
            [PlanLine(OrderA, LineA, "HU-2", 600), PlanLine(OrderB, LineB, "HU-1", 600)]);

        Assert.Equal("HU-2", Assert.Single(harness.GetOrderReceiptPlanLines(OrderA)).ToHu);
        Assert.Equal("HU-1", Assert.Single(harness.GetOrderReceiptPlanLines(OrderB)).ToHu);
    }

    [Fact]
    public void ReplaceBatch_ConflictWithOrderOutsideBatch_Throws()
    {
        var harness = new CloseDocumentHarness();
        SeedCustomerOrder(harness, OrderA, "SO-A");
        SeedCustomerOrder(harness, OrderC, "SO-C");
        harness.SeedOrderReceiptPlanLines(OrderA, PlanLine(OrderA, LineA, "HU-1", 600));
        harness.SeedOrderReceiptPlanLines(OrderC, PlanLine(OrderC, LineC, "HU-9", 600));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            harness.Store.ReplaceOrderReceiptPlanLinesBatch(
                [new OrderReceiptPlanLineKey(OrderA, LineA)],
                [PlanLine(OrderA, LineA, "HU-9", 600)]));

        Assert.Contains("SO-C", ex.Message);
    }

    [Fact]
    public void ReplaceBatch_ConflictWithUnaffectedLineOfSameOrder_Throws()
    {
        var harness = new CloseDocumentHarness();
        SeedCustomerOrder(harness, OrderA, "SO-A");
        harness.SeedOrderReceiptPlanLines(
            OrderA,
            PlanLine(OrderA, LineA, "HU-1", 600),
            PlanLine(OrderA, LineA2, "HU-7", 600));

        // scope содержит только LineA; LineA2 того же заказа не входит в scope,
        // поэтому занятый ею HU-7 должен дать конфликт.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            harness.Store.ReplaceOrderReceiptPlanLinesBatch(
                [new OrderReceiptPlanLineKey(OrderA, LineA)],
                [PlanLine(OrderA, LineA, "HU-7", 600)]));

        Assert.Contains("HU-7", ex.Message);
    }

    [Fact]
    public void ReplaceBatch_ConflictLeavesStateUnchanged()
    {
        var harness = new CloseDocumentHarness();
        SeedCustomerOrder(harness, OrderA, "SO-A");
        SeedCustomerOrder(harness, OrderC, "SO-C");
        harness.SeedOrderReceiptPlanLines(OrderA, PlanLine(OrderA, LineA, "HU-1", 600));
        harness.SeedOrderReceiptPlanLines(OrderC, PlanLine(OrderC, LineC, "HU-9", 600));

        Assert.Throws<InvalidOperationException>(() =>
            harness.Store.ReplaceOrderReceiptPlanLinesBatch(
                [new OrderReceiptPlanLineKey(OrderA, LineA)],
                [PlanLine(OrderA, LineA, "HU-9", 600)]));

        // Конфликт проверяется до удаления/вставки — scope-строка не должна быть удалена.
        Assert.Equal("HU-1", Assert.Single(harness.GetOrderReceiptPlanLines(OrderA)).ToHu);
        Assert.Equal("HU-9", Assert.Single(harness.GetOrderReceiptPlanLines(OrderC)).ToHu);
    }

    private static void SeedCustomerOrder(CloseDocumentHarness harness, long orderId, string orderRef)
    {
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = orderRef,
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
    }

    private static OrderReceiptPlanLine PlanLine(long orderId, long orderLineId, string huCode, double qty, int sortOrder = 0) =>
        new()
        {
            OrderId = orderId,
            OrderLineId = orderLineId,
            ItemId = ItemId,
            QtyPlanned = qty,
            ToHu = huCode,
            SortOrder = sortOrder
        };
}

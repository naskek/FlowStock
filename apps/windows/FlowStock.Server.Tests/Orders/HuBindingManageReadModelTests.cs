using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Orders;

public sealed class HuBindingManageReadModelTests
{
    private const long ItemTypeId = 1;
    private const long Loc1 = 1;
    private const long Loc2 = 2;
    private const long ItemA = 6;
    private const long ItemB = 7;
    private const long PartnerId = 200;
    private const long OrderA = 50;
    private const long LineA = 5000;

    [Fact]
    public void GetHuRows_FreeHu_IsListedAsFree()
    {
        var harness = NewHarness();
        harness.SeedBalance(ItemA, Loc1, 100, "HU-FREE");

        var row = Assert.Single(Service(harness).GetHuRows(ItemA, AllFilter()).HuRows);

        Assert.Equal("HU-FREE", row.HuCode);
        Assert.Equal("FREE", row.State);
        Assert.Null(row.CurrentAssignment);
        Assert.Equal(100, row.Qty, 3);
    }

    [Fact]
    public void GetHuRows_BoundHu_ShowsOrderPartnerAndLine()
    {
        var harness = NewHarness();
        SeedCustomerOrder(harness, OrderA, "SO-A", OrderStatus.InProgress);
        SeedLine(harness, LineA, OrderA, ItemA, 600);
        harness.SeedBalance(ItemA, Loc1, 600, "HU-B");
        harness.SeedOrderReceiptPlanLines(OrderA, Plan(OrderA, LineA, ItemA, "HU-B", 600));

        var row = Assert.Single(Service(harness).GetHuRows(ItemA, AllFilter()).HuRows);

        Assert.Equal("BOUND", row.State);
        Assert.NotNull(row.CurrentAssignment);
        Assert.Equal(OrderA, row.CurrentAssignment!.OrderId);
        Assert.Equal("SO-A", row.CurrentAssignment.OrderRef);
        Assert.Equal("Клиент", row.CurrentAssignment.PartnerName);
        Assert.Equal(LineA, row.CurrentAssignment.OrderLineId);
        Assert.Equal(600, row.CurrentAssignment.ReservedQty, 3);
    }

    [Fact]
    public void GetHuRows_HuWithoutPositiveStock_IsHidden()
    {
        var harness = NewHarness();
        harness.SeedBalance(ItemA, Loc1, 100, "HU-POS");
        harness.SeedBalance(ItemA, Loc1, 0, "HU-ZERO");

        var codes = Service(harness).GetHuRows(ItemA, AllFilter()).HuRows.Select(row => row.HuCode).ToArray();

        Assert.Contains("HU-POS", codes);
        Assert.DoesNotContain("HU-ZERO", codes);
    }

    [Fact]
    public void GetHuRows_MultipleLocationRows_AreAggregated()
    {
        var harness = NewHarness();
        harness.SeedBalance(ItemA, Loc1, 100, "HU-AGG");
        harness.SeedBalance(ItemA, Loc2, 50, "HU-AGG");

        var row = Assert.Single(Service(harness).GetHuRows(ItemA, AllFilter()).HuRows);

        Assert.Equal(150, row.Qty, 3);
        Assert.Contains("MAIN", row.LocationDisplay);
        Assert.Contains("SECOND", row.LocationDisplay);
    }

    [Fact]
    public void GetHuRows_MixedHu_IsDetected()
    {
        var harness = NewHarness();
        harness.SeedBalance(ItemA, Loc1, 100, "HU-MIX");
        harness.SeedBalance(ItemB, Loc1, 80, "HU-MIX");

        var row = Assert.Single(Service(harness).GetHuRows(ItemA, AllFilter()).HuRows);

        Assert.True(row.IsMixed);
    }

    [Fact]
    public void GetItems_FiltersByName()
    {
        var harness = NewHarness();
        harness.SeedBalance(ItemA, Loc1, 100, "HU-A");
        harness.SeedBalance(ItemB, Loc1, 100, "HU-B");

        var items = Service(harness).GetItems("Горч", 100);

        var item = Assert.Single(items);
        Assert.Equal(ItemA, item.ItemId);
        Assert.Equal(1, item.HuCount);
    }

    [Fact]
    public void GetHuRows_StateAndSearchFilters_Apply()
    {
        var harness = NewHarness();
        SeedCustomerOrder(harness, OrderA, "SO-A", OrderStatus.InProgress);
        SeedLine(harness, LineA, OrderA, ItemA, 600);
        harness.SeedBalance(ItemA, Loc1, 100, "HU-FREE");
        harness.SeedBalance(ItemA, Loc1, 600, "HU-BOUND");
        harness.SeedOrderReceiptPlanLines(OrderA, Plan(OrderA, LineA, ItemA, "HU-BOUND", 600));

        var free = Service(harness).GetHuRows(ItemA, Filter(state: HuBindingManageStateFilter.Free)).HuRows;
        Assert.Equal("HU-FREE", Assert.Single(free).HuCode);

        var bound = Service(harness).GetHuRows(ItemA, Filter(state: HuBindingManageStateFilter.Bound)).HuRows;
        Assert.Equal("HU-BOUND", Assert.Single(bound).HuCode);

        var byHu = Service(harness).GetHuRows(ItemA, Filter(huSearch: "FREE")).HuRows;
        Assert.Equal("HU-FREE", Assert.Single(byHu).HuCode);

        var byOrder = Service(harness).GetHuRows(ItemA, Filter(orderSearch: "SO-A")).HuRows;
        Assert.Equal("HU-BOUND", Assert.Single(byOrder).HuCode);

        var byPartner = Service(harness).GetHuRows(ItemA, Filter(partnerSearch: "Клиент")).HuRows;
        Assert.Equal("HU-BOUND", Assert.Single(byPartner).HuCode);
    }

    [Fact]
    public void GetHuRows_Pagination_ReportsTotalLimitOffset()
    {
        var harness = NewHarness();
        harness.SeedBalance(ItemA, Loc1, 10, "HU-1");
        harness.SeedBalance(ItemA, Loc1, 10, "HU-2");
        harness.SeedBalance(ItemA, Loc1, 10, "HU-3");

        var firstPage = Service(harness).GetHuRows(ItemA, Filter(limit: 2, offset: 0));
        Assert.Equal(3, firstPage.Total);
        Assert.Equal(2, firstPage.Limit);
        Assert.Equal(0, firstPage.Offset);
        Assert.Equal(2, firstPage.HuRows.Count);

        var secondPage = Service(harness).GetHuRows(ItemA, Filter(limit: 2, offset: 2));
        Assert.Equal(3, secondPage.Total);
        Assert.Single(secondPage.HuRows);
    }

    [Fact]
    public void GetTargets_ReturnsFullCurrentBoundHuCodes()
    {
        var harness = NewHarness();
        SeedCustomerOrder(harness, OrderA, "SO-A", OrderStatus.InProgress);
        SeedLine(harness, LineA, OrderA, ItemA, 600);
        harness.SeedBalance(ItemA, Loc1, 200, "HU-10");
        harness.SeedBalance(ItemA, Loc1, 200, "HU-11");
        harness.SeedOrderReceiptPlanLines(
            OrderA,
            Plan(OrderA, LineA, ItemA, "HU-10", 200, 0),
            Plan(OrderA, LineA, ItemA, "HU-11", 200, 1));

        var line = Assert.Single(Service(harness).GetTargets(ItemA));

        Assert.Equal(new[] { "HU-10", "HU-11" }, line.CurrentBoundHuCodes.OrderBy(c => c).ToArray());
        Assert.Equal(400, line.CurrentBoundQty, 3);
    }

    [Fact]
    public void GetTargets_ExcludesClosedShippedCancelledAndOtherItemLines()
    {
        var harness = NewHarness();
        SeedCustomerOrder(harness, 60, "SO-ACTIVE", OrderStatus.InProgress);
        SeedLine(harness, 6000, 60, ItemA, 600);
        SeedCustomerOrder(harness, 61, "SO-SHIPPED", OrderStatus.Shipped);
        SeedLine(harness, 6100, 61, ItemA, 600);
        SeedCustomerOrder(harness, 62, "SO-CANCELLED", OrderStatus.Cancelled);
        SeedLine(harness, 6200, 62, ItemA, 600);
        SeedCustomerOrder(harness, 63, "SO-OTHER-ITEM", OrderStatus.InProgress);
        SeedLine(harness, 6300, 63, ItemB, 600);

        var orderIds = Service(harness).GetTargets(ItemA).Select(line => line.OrderId).ToArray();

        Assert.Equal(new[] { 60L }, orderIds);
    }

    [Fact]
    public void GetTargets_ComputesMaxAdditionalBindQty()
    {
        var harness = NewHarness();
        SeedCustomerOrder(harness, OrderA, "SO-A", OrderStatus.InProgress);
        SeedLine(harness, LineA, OrderA, ItemA, 600);
        harness.SeedBalance(ItemA, Loc1, 200, "HU-BOUND");
        harness.SeedOrderReceiptPlanLines(OrderA, Plan(OrderA, LineA, ItemA, "HU-BOUND", 200));

        var line = Assert.Single(Service(harness).GetTargets(ItemA));

        // qty_ordered(600) - produced(0) - open_pallet(0) - current_bound(200) = 400
        Assert.Equal(400, line.MaxAdditionalBindQty, 3);
    }

    private static HuBindingManageReadModelService Service(CloseDocumentHarness harness) =>
        new(harness.Store);

    private static HuBindingManageHuFilter AllFilter() => new();

    private static HuBindingManageHuFilter Filter(
        HuBindingManageStateFilter state = HuBindingManageStateFilter.All,
        string? huSearch = null,
        string? orderSearch = null,
        string? partnerSearch = null,
        int limit = 100,
        int offset = 0) =>
        new()
        {
            State = state,
            HuSearch = huSearch,
            OrderSearch = orderSearch,
            PartnerSearch = partnerSearch,
            Limit = limit,
            Offset = offset
        };

    private static CloseDocumentHarness NewHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = Loc1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedLocation(new Location { Id = Loc2, Code = "SECOND", Name = "Второй склад" });
        harness.SeedItemType(new ItemType { Id = ItemTypeId, Name = "Готовая продукция", EnableOrderReservation = true });
        harness.SeedItem(new Item { Id = ItemA, Name = "Горчица", BaseUom = "шт", ItemTypeId = ItemTypeId, MaxQtyPerHu = 600 });
        harness.SeedItem(new Item { Id = ItemB, Name = "Хрен", BaseUom = "шт", ItemTypeId = ItemTypeId, MaxQtyPerHu = 600 });
        harness.SeedPartner(new Partner { Id = PartnerId, Code = "CUST", Name = "Клиент" });
        return harness;
    }

    private static void SeedCustomerOrder(CloseDocumentHarness harness, long orderId, string orderRef, OrderStatus status)
    {
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = orderRef,
            Type = OrderType.Customer,
            Status = status,
            PartnerId = PartnerId,
            PartnerName = "Клиент",
            CreatedAt = DateTime.UtcNow
        });
    }

    private static void SeedLine(CloseDocumentHarness harness, long lineId, long orderId, long itemId, double qty)
    {
        harness.SeedOrderLine(new OrderLine { Id = lineId, OrderId = orderId, ItemId = itemId, QtyOrdered = qty });
    }

    private static OrderReceiptPlanLine Plan(long orderId, long lineId, long itemId, string huCode, double qty, int sortOrder = 0) =>
        new()
        {
            OrderId = orderId,
            OrderLineId = lineId,
            ItemId = itemId,
            QtyPlanned = qty,
            ToHu = huCode,
            ToLocationId = Loc1,
            SortOrder = sortOrder
        };
}

using System.Reflection;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Moq;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class OrderStatusRefreshTests
{
    [Fact]
    public void CollectAffectedOrderIds_UsesBatchOrderLineLookup_WithoutScanningAllOrders()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetOrderIdsByOrderLineIds(It.Is<IReadOnlyCollection<long>>(ids =>
                ids.Count == 2 && ids.Contains(101) && ids.Contains(202))))
            .Returns(new Dictionary<long, long>
            {
                [101] = 10,
                [202] = 20
            });

        var method = typeof(DocumentService).GetMethod(
            "CollectAffectedOrderIds",
            BindingFlags.NonPublic | BindingFlags.Static);

        var result = Assert.IsType<HashSet<long>>(method!.Invoke(null, new object[]
        {
            store.Object,
            new Doc { Id = 1 },
            new[]
            {
                new DocLine { Id = 1, OrderLineId = 101, ItemId = 1001, Qty = 1 },
                new DocLine { Id = 2, OrderLineId = 202, ItemId = 1002, Qty = 1 },
                new DocLine { Id = 3, OrderLineId = 101, ItemId = 1001, Qty = 1 }
            }
        })!);

        Assert.Equal([10, 20], result.OrderBy(id => id).ToArray());
        store.Verify(s => s.GetOrderIdsByOrderLineIds(It.IsAny<IReadOnlyCollection<long>>()), Times.Once);
        store.Verify(s => s.GetOrders(), Times.Never);
        store.Verify(s => s.GetOrderLines(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public void CloseProductionReceipt_ByOrderLineId_RefreshesOnlyAffectedCustomerReceiptPlan()
    {
        var harness = CreateCustomerOrderHarness();
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "SO-001",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            PartnerId = 200,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 5, 8, 9, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrder(new Order
        {
            Id = 20,
            OrderRef = "SO-020",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            PartnerId = 200,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 5, 8, 9, 30, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 201,
            OrderId = 20,
            ItemId = 1001,
            QtyOrdered = 5,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedOrderReceiptPlanLines(10, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1001,
            QtyPlanned = 1,
            ToLocationId = 1,
            ToHu = "HU-AFFECTED",
            SortOrder = 1
        });
        harness.SeedOrderReceiptPlanLines(20, new OrderReceiptPlanLine
        {
            Id = 2,
            OrderId = 20,
            OrderLineId = 201,
            ItemId = 1001,
            QtyPlanned = 5,
            ToLocationId = 1,
            ToHu = "HU-UNRELATED",
            SortOrder = 1
        });
        harness.SeedDoc(new Doc
        {
            Id = 400,
            DocRef = "PRD-2026-000400",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            OrderRef = "SO-001",
            CreatedAt = new DateTime(2026, 5, 8, 15, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 401,
            DocId = 400,
            OrderLineId = 101,
            ItemId = 1001,
            Qty = 4,
            ToLocationId = 1,
            ToHu = "HU-PRD-401"
        });

        var result = harness.CreateService().TryCloseDoc(400, allowNegative: false);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.NotNull(result.Timing);
        Assert.InRange(result.Timing!.ValidateBuildCheckMs ?? -1, 0, long.MaxValue);
        Assert.InRange(result.Timing.LedgerTransactionMs ?? -1, 0, long.MaxValue);
        Assert.InRange(result.Timing.CollectAffectedOrdersMs ?? -1, 0, long.MaxValue);
        Assert.InRange(result.Timing.RefreshStatusMs ?? -1, 0, long.MaxValue);
        Assert.InRange(result.Timing.RefreshReceiptPlansMs ?? -1, 0, long.MaxValue);
        Assert.Empty(harness.GetOrderReceiptPlanLines(10));
        var unrelatedPlan = Assert.Single(harness.GetOrderReceiptPlanLines(20));
        Assert.Equal("HU-UNRELATED", unrelatedPlan.ToHu);
        Mock.Get(harness.Store).Verify(store => store.GetOrders(), Times.Never);
    }

    [Fact]
    public void CloseProductionReceipt_FullDraftInternalOrder_BecomesShipped()
    {
        // Regression: INTERNAL orders created from production needs start as DRAFT;
        // auto-status refresh must still complete them after full production receipt.
        var harness = CreateInternalOrderHarness(status: OrderStatus.Draft);
        harness.SeedDoc(new Doc
        {
            Id = 100,
            DocRef = "PRD-2026-000100",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 50,
            OrderRef = "INT-001",
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 101,
            DocId = 100,
            OrderLineId = 501,
            ItemId = 1001,
            Qty = 5,
            ToLocationId = 1,
            ToHu = "HU-INT-001"
        });

        var result = harness.CreateService().TryCloseDoc(100, allowNegative: false);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(50).Status);
    }

    [Fact]
    public void CloseProductionReceipt_PartialDraftInternalOrder_BecomesInProgress()
    {
        var harness = CreateInternalOrderHarness(status: OrderStatus.Draft);
        harness.SeedDoc(new Doc
        {
            Id = 110,
            DocRef = "PRD-2026-000110",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 50,
            OrderRef = "INT-001",
            CreatedAt = new DateTime(2026, 5, 8, 10, 30, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 111,
            DocId = 110,
            OrderLineId = 501,
            ItemId = 1001,
            Qty = 3,
            ToLocationId = 1,
            ToHu = "HU-INT-001A"
        });

        var result = harness.CreateService().TryCloseDoc(110, allowNegative: false);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(OrderStatus.InProgress, harness.GetOrder(50).Status);
    }

    [Fact]
    public void CloseProductionReceipt_PartialInternalOrder_RemainsInProgress_UntilLastLineClosed()
    {
        var harness = CreateInternalOrderHarness(status: OrderStatus.Draft);
        harness.SeedOrderLine(new OrderLine
        {
            Id = 502,
            OrderId = 50,
            ItemId = 1002,
            QtyOrdered = 7,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
        harness.SeedItem(new Item
        {
            Id = 1002,
            Name = "Кетчуп",
            Gtin = "04607186951521",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMarking = false
        });

        harness.SeedDoc(new Doc
        {
            Id = 100,
            DocRef = "PRD-2026-000100",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 50,
            OrderRef = "INT-001",
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 101,
            DocId = 100,
            OrderLineId = 501,
            ItemId = 1001,
            Qty = 5,
            ToLocationId = 1,
            ToHu = "HU-INT-001"
        });

        var firstResult = harness.CreateService().TryCloseDoc(100, allowNegative: false);

        Assert.True(firstResult.Success, string.Join("; ", firstResult.Errors));
        Assert.Equal(OrderStatus.InProgress, harness.GetOrder(50).Status);

        harness.SeedDoc(new Doc
        {
            Id = 200,
            DocRef = "PRD-2026-000200",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 50,
            OrderRef = "INT-001",
            CreatedAt = new DateTime(2026, 5, 8, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 201,
            DocId = 200,
            OrderLineId = 502,
            ItemId = 1002,
            Qty = 7,
            ToLocationId = 1,
            ToHu = "HU-INT-002"
        });

        var secondResult = harness.CreateService().TryCloseDoc(200, allowNegative: false);

        Assert.True(secondResult.Success, string.Join("; ", secondResult.Errors));
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(50).Status);
    }

    [Fact]
    public void CloseProductionReceipt_FullInternalOrder_WithDocOrderBindingOnly_BecomesShipped()
    {
        var harness = CreateInternalOrderHarness(status: OrderStatus.Draft);
        harness.SeedDoc(new Doc
        {
            Id = 300,
            DocRef = "PRD-2026-000300",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 50,
            OrderRef = "INT-001",
            CreatedAt = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 301,
            DocId = 300,
            ItemId = 1001,
            Qty = 5,
            ToLocationId = 1,
            ToHu = "HU-INT-003"
        });

        var result = harness.CreateService().TryCloseDoc(300, allowNegative: false);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(50).Status);
        var remaining = Assert.Single(new DocumentService(harness.Store).GetOrderReceiptRemaining(50));
        Assert.Equal(0, remaining.QtyRemaining);
    }

    [Fact]
    public void CloseProductionReceipt_PartialInternalOrder_WithDocOrderBindingOnly_RemainsInProgress()
    {
        var harness = CreateInternalOrderHarness(status: OrderStatus.Draft);
        harness.SeedDoc(new Doc
        {
            Id = 310,
            DocRef = "PRD-2026-000310",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 50,
            OrderRef = "INT-001",
            CreatedAt = new DateTime(2026, 5, 8, 12, 30, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 311,
            DocId = 310,
            ItemId = 1001,
            Qty = 4,
            ToLocationId = 1,
            ToHu = "HU-INT-004"
        });

        var result = harness.CreateService().TryCloseDoc(310, allowNegative: false);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(OrderStatus.InProgress, harness.GetOrder(50).Status);
        var remaining = Assert.Single(new DocumentService(harness.Store).GetOrderReceiptRemaining(50));
        Assert.Equal(1, remaining.QtyRemaining);
    }

    [Fact]
    public void CloseProductionReceipt_DraftInternalOrder_WithHuSplitLines_CompletesAfterFullProducedQty()
    {
        var harness = CreateInternalOrderHarness(status: OrderStatus.Draft);
        harness.SeedOrderLine(new OrderLine
        {
            Id = 502,
            OrderId = 50,
            ItemId = 1002,
            QtyOrdered = 7,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
        harness.SeedItem(new Item
        {
            Id = 1002,
            Name = "Кетчуп",
            Gtin = "04607186951521",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMarking = false
        });
        harness.SeedDoc(new Doc
        {
            Id = 315,
            DocRef = "PRD-2026-000315",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 50,
            OrderRef = "INT-001",
            CreatedAt = new DateTime(2026, 5, 8, 12, 45, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 3151,
            DocId = 315,
            OrderLineId = 501,
            ItemId = 1001,
            Qty = 2,
            ToLocationId = 1,
            ToHu = "HU-INT-005A"
        });
        harness.SeedLine(new DocLine
        {
            Id = 3152,
            DocId = 315,
            OrderLineId = 501,
            ItemId = 1001,
            Qty = 3,
            ToLocationId = 1,
            ToHu = "HU-INT-005B"
        });
        harness.SeedLine(new DocLine
        {
            Id = 3153,
            DocId = 315,
            OrderLineId = 502,
            ItemId = 1002,
            Qty = 4,
            ToLocationId = 1,
            ToHu = "HU-INT-006A"
        });
        harness.SeedLine(new DocLine
        {
            Id = 3154,
            DocId = 315,
            OrderLineId = 502,
            ItemId = 1002,
            Qty = 3,
            ToLocationId = 1,
            ToHu = "HU-INT-006B"
        });

        var result = harness.CreateService().TryCloseDoc(315, allowNegative: false);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(50).Status);
    }

    [Fact]
    public void CloseProductionReceipt_InProgressInternalOrder_BecomesShipped_WhenFullProduced()
    {
        var harness = CreateInternalOrderHarness();
        harness.SeedOrderLine(new OrderLine
        {
            Id = 502,
            OrderId = 50,
            ItemId = 1002,
            QtyOrdered = 7,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });
        harness.SeedItem(new Item
        {
            Id = 1002,
            Name = "Кетчуп",
            Gtin = "04607186951521",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMarking = false
        });

        harness.SeedDoc(new Doc
        {
            Id = 320,
            DocRef = "PRD-2026-000320",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 50,
            OrderRef = "INT-001",
            CreatedAt = new DateTime(2026, 5, 8, 13, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 321,
            DocId = 320,
            ItemId = 1001,
            Qty = 5,
            ToLocationId = 1,
            ToHu = "HU-INT-005"
        });

        var firstResult = harness.CreateService().TryCloseDoc(320, allowNegative: false);

        Assert.True(firstResult.Success, string.Join("; ", firstResult.Errors));
        Assert.Equal(OrderStatus.InProgress, harness.GetOrder(50).Status);

        harness.SeedDoc(new Doc
        {
            Id = 330,
            DocRef = "PRD-2026-000330",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 50,
            OrderRef = "INT-001",
            CreatedAt = new DateTime(2026, 5, 8, 14, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 331,
            DocId = 330,
            ItemId = 1002,
            Qty = 7,
            ToLocationId = 1,
            ToHu = "HU-INT-006"
        });

        var secondResult = harness.CreateService().TryCloseDoc(330, allowNegative: false);

        Assert.True(secondResult.Success, string.Join("; ", secondResult.Errors));
        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(50).Status);
    }

    [Fact]
    public void CloseProductionReceipt_FullCustomerOrder_BecomesAccepted()
    {
        var harness = CreateCustomerOrderHarness();
        harness.SeedDoc(new Doc
        {
            Id = 1,
            DocRef = "PRD-2026-000001",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            OrderRef = "SO-001",
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 11,
            DocId = 1,
            OrderLineId = 101,
            ItemId = 1001,
            Qty = 5,
            ToLocationId = 1,
            ToHu = "HU-PRD-001"
        });

        var result = harness.CreateService().TryCloseDoc(1, allowNegative: false);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(OrderStatus.Accepted, harness.GetOrder(10).Status);
    }

    [Fact]
    public void CloseProductionReceipt_PartialCustomerOrder_RemainsInProgress_UntilLastLineClosed()
    {
        var harness = CreateCustomerOrderHarness();
        harness.SeedOrderLine(new OrderLine
        {
            Id = 102,
            OrderId = 10,
            ItemId = 1002,
            QtyOrdered = 7,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedItem(new Item
        {
            Id = 1002,
            Name = "Кетчуп",
            Gtin = "04607186951521",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMarking = false
        });

        harness.SeedDoc(new Doc
        {
            Id = 1,
            DocRef = "PRD-2026-000001",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            OrderRef = "SO-001",
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 11,
            DocId = 1,
            OrderLineId = 101,
            ItemId = 1001,
            Qty = 5,
            ToLocationId = 1,
            ToHu = "HU-PRD-001"
        });

        var firstResult = harness.CreateService().TryCloseDoc(1, allowNegative: false);

        Assert.True(firstResult.Success);
        Assert.Equal(OrderStatus.InProgress, harness.GetOrder(10).Status);

        harness.SeedDoc(new Doc
        {
            Id = 2,
            DocRef = "PRD-2026-000002",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            OrderRef = "SO-001",
            CreatedAt = new DateTime(2026, 5, 8, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 21,
            DocId = 2,
            OrderLineId = 102,
            ItemId = 1002,
            Qty = 7,
            ToLocationId = 1,
            ToHu = "HU-PRD-002"
        });

        var secondResult = harness.CreateService().TryCloseDoc(2, allowNegative: false);

        Assert.True(secondResult.Success);
        Assert.Equal(OrderStatus.Accepted, harness.GetOrder(10).Status);
    }

    [Fact]
    public void CloseOutbound_FullCustomerOrder_BecomesShipped()
    {
        var harness = CreateCustomerOrderHarness();
        harness.SeedOrder(new Order
        {
            Id = 20,
            OrderRef = "SO-020",
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            PartnerId = 200,
            CreatedAt = new DateTime(2026, 5, 8, 9, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 201,
            OrderId = 20,
            ItemId = 1001,
            QtyOrdered = 5,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedOrderReceiptPlanLines(20, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 20,
            OrderLineId = 201,
            ItemId = 1001,
            QtyPlanned = 5,
            ToLocationId = 1,
            ToHu = "HU-OUT-001",
            SortOrder = 1
        });
        harness.SeedBalance(itemId: 1001, locationId: 1, qty: 5, huCode: "HU-OUT-001");
        harness.SeedDoc(new Doc
        {
            Id = 3,
            DocRef = "OUT-2026-000003",
            Type = DocType.Outbound,
            Status = DocStatus.Draft,
            OrderId = 20,
            OrderRef = "SO-020",
            PartnerId = 200,
            CreatedAt = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 31,
            DocId = 3,
            OrderLineId = 201,
            ItemId = 1001,
            Qty = 5,
            FromLocationId = 1,
            FromHu = "HU-OUT-001"
        });

        var result = harness.CreateService().TryCloseDoc(3, allowNegative: false);

        if (!result.Success)
        {
            throw new Xunit.Sdk.XunitException(string.Join("; ", result.Errors));
        }

        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(20).Status);
    }

    [Fact]
    public void RefreshPersistedStatus_CustomerQtyAboveProtectedCoverage_BecomesInProgress()
    {
        var harness = CreateCustomerOrderHarness(OrderStatus.Accepted, qtyOrdered: 1800);
        harness.SeedOrderReceiptPlanLines(10, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1001,
            QtyPlanned = 600,
            ToLocationId = 1,
            ToHu = "HU-READY-600",
            SortOrder = 1
        });
        harness.SeedBalance(1001, 1, 600, "HU-READY-600");

        var status = new OrderService(harness.Store).RefreshPersistedStatus(10);

        Assert.Equal(OrderStatus.InProgress, status);
        Assert.Equal(OrderStatus.InProgress, harness.GetOrder(10).Status);
    }

    [Fact]
    public void RefreshPersistedStatus_CustomerQtyCoveredByProtectedHu_BecomesAccepted()
    {
        var harness = CreateCustomerOrderHarness(OrderStatus.InProgress, qtyOrdered: 600);
        harness.SeedOrderReceiptPlanLines(10, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1001,
            QtyPlanned = 600,
            ToLocationId = 1,
            ToHu = "HU-READY-600",
            SortOrder = 1
        });
        harness.SeedBalance(1001, 1, 600, "HU-READY-600");

        var status = new OrderService(harness.Store).RefreshPersistedStatus(10);

        Assert.Equal(OrderStatus.Accepted, status);
        Assert.Equal(OrderStatus.Accepted, harness.GetOrder(10).Status);
    }

    [Fact]
    public void RefreshPersistedStatus_CustomerMultiLineWithMissingLine_RemainsInProgress()
    {
        var harness = CreateCustomerOrderHarness(OrderStatus.Accepted, qtyOrdered: 600);
        harness.SeedItem(new Item
        {
            Id = 1002,
            Name = "Кетчуп",
            Gtin = "04607186951521",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMarking = false
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 102,
            OrderId = 10,
            ItemId = 1002,
            QtyOrdered = 378,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedOrderReceiptPlanLines(10, new OrderReceiptPlanLine
        {
            Id = 1,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 1001,
            QtyPlanned = 600,
            ToLocationId = 1,
            ToHu = "HU-READY-600",
            SortOrder = 1
        });
        harness.SeedBalance(1001, 1, 600, "HU-READY-600");

        var status = new OrderService(harness.Store).RefreshPersistedStatus(10);

        Assert.Equal(OrderStatus.InProgress, status);
        Assert.Equal(OrderStatus.InProgress, harness.GetOrder(10).Status);
    }

    [Fact]
    public void RefreshPersistedStatus_ComputedShippedCustomerOrder_PersistsStatus()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetOrder(55))
            .Returns(new Order
            {
                Id = 55,
                OrderRef = "055",
                Type = OrderType.Customer,
                Status = OrderStatus.Shipped,
                PartnerId = 200,
                CreatedAt = new DateTime(2026, 5, 14, 8, 28, 23, DateTimeKind.Utc)
            });
        store.Setup(s => s.GetOrderLines(55))
            .Returns([
                new OrderLine
                {
                    Id = 143,
                    OrderId = 55,
                    ItemId = 18,
                    QtyOrdered = 1134,
                    ProductionPurpose = ProductionLinePurpose.CustomerOrder
                }
            ]);
        store.Setup(s => s.GetShippedTotalsByOrderLine(55))
            .Returns(new Dictionary<long, double> { [143] = 1134 });
        store.Setup(s => s.GetOrderReceiptRemaining(55))
            .Returns([
                new OrderReceiptLine
                {
                    OrderLineId = 143,
                    OrderId = 55,
                    ItemId = 18,
                    QtyOrdered = 1134,
                    QtyReceived = 1134,
                    QtyRemaining = 0,
                    ProductionPurpose = ProductionLinePurpose.CustomerOrder
                }
            ]);
        store.Setup(s => s.UpdateOrderStatus(55, OrderStatus.Shipped));

        var service = new OrderService(store.Object);

        var status = service.RefreshPersistedStatus(55);

        Assert.Equal(OrderStatus.Shipped, status);
        store.Verify(s => s.UpdateOrderStatus(55, OrderStatus.Shipped), Times.Once);
    }

    private static CloseDocumentHarness CreateCustomerOrderHarness(
        OrderStatus status = OrderStatus.InProgress,
        double qtyOrdered = 5)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location
        {
            Id = 1,
            Code = "FG-01",
            Name = "Готовая продукция"
        });
        harness.SeedPartner(new Partner
        {
            Id = 200,
            Code = "CUST-200",
            Name = "Тестовый клиент",
            CreatedAt = new DateTime(2026, 5, 8, 8, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Gtin = "04607186951520",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMarking = false
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "SO-001",
            Type = OrderType.Customer,
            Status = status,
            PartnerId = 200,
            CreatedAt = new DateTime(2026, 5, 8, 9, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 1001,
            QtyOrdered = qtyOrdered,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });

        return harness;
    }

    private static CloseDocumentHarness CreateInternalOrderHarness(OrderStatus status = OrderStatus.InProgress)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location
        {
            Id = 1,
            Code = "FG-01",
            Name = "Готовая продукция"
        });
        harness.SeedItem(new Item
        {
            Id = 1001,
            Name = "Горчица",
            Gtin = "04607186951520",
            ItemTypeName = "Готовая продукция",
            ItemTypeEnableMarking = false
        });
        harness.SeedOrder(new Order
        {
            Id = 50,
            OrderRef = "INT-001",
            Type = OrderType.Internal,
            Status = status,
            CreatedAt = new DateTime(2026, 5, 8, 9, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 501,
            OrderId = 50,
            ItemId = 1001,
            QtyOrdered = 5,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });

        return harness;
    }
}

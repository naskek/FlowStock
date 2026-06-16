using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using System.Reflection;
using System.Text.Json;

namespace FlowStock.Server.Tests.Tsd;

public sealed class TsdListReadModelTests
{
    [Fact]
    public void OutboundGetOrders_DoesNotCallGetDetails()
    {
        var source = File.ReadAllText(GetRepoPath("apps", "windows", "FlowStock.Core", "Services", "OutboundPickingService.cs"));

        var getOrdersStart = source.IndexOf("public IReadOnlyList<OutboundPickingOrderRow> GetOrders()", StringComparison.Ordinal);
        var getDetailsStart = source.IndexOf("public OutboundPickingOrderDetails GetDetails(", StringComparison.Ordinal);
        Assert.True(getOrdersStart >= 0);
        Assert.True(getDetailsStart > getOrdersStart);

        var getOrdersBody = source[getOrdersStart..getDetailsStart];
        Assert.DoesNotContain("GetDetails(", getOrdersBody, StringComparison.Ordinal);
        Assert.DoesNotContain(".Hus", getOrdersBody, StringComparison.Ordinal);
    }

    [Fact]
    public void OutboundListRows_DoNotExposeHuCollection()
    {
        var harness = CreateBasicPickingHarness();
        var rows = CreatePickingService(harness).GetOrders();

        var row = Assert.Single(rows);
        Assert.Equal(20, row.OrderId);
        Assert.Equal(1, row.ExpectedHuCount);
        Assert.False(HasProperty(typeof(OutboundPickingOrderRow), "Hus"));
    }

    [Fact]
    public void OutboundListEndpointResponse_ContainsTsdFieldsWithoutHus()
    {
        var harness = CreateBasicPickingHarness();
        var payload = TsdOutboundPickingEndpointsTestHelper.MapListRows(CreatePickingService(harness).GetOrders());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));

        var row = Assert.Single(json.RootElement.EnumerateArray());
        Assert.True(row.TryGetProperty("order_id", out _));
        Assert.True(row.TryGetProperty("order_ref", out _));
        Assert.True(row.TryGetProperty("partner_name", out _));
        Assert.True(row.TryGetProperty("status", out _));
        Assert.True(row.TryGetProperty("expected_hu_count", out _));
        Assert.True(row.TryGetProperty("picked_hu_count", out _));
        Assert.True(row.TryGetProperty("ordered_qty", out _));
        Assert.True(row.TryGetProperty("shipped_qty", out _));
        Assert.True(row.TryGetProperty("remaining_qty", out _));
        Assert.True(row.TryGetProperty("scanned_qty", out _));
        Assert.True(row.TryGetProperty("can_close", out _));
        Assert.True(row.TryGetProperty("is_closed", out _));
        Assert.True(row.TryGetProperty("operation_fingerprint", out _));
        Assert.False(row.TryGetProperty("hus", out _));
    }

    [Fact]
    public void FillingList_ExcludesInternalShippedWithZeroRemainingQty()
    {
        var harness = CreateInternalOrderHarness(orderQty: 1200, maxQtyPerHu: 600);
        var service = CreateAutoClosePalletService(harness);
        var plan = service.PlanOrder(10);
        foreach (var pallet in harness.Store.GetProductionPalletsByDoc(plan.PrdDocId))
        {
            Assert.True(service.Fill(pallet.HuCode, "TSD-01", orderId: 10).Success);
        }

        Assert.Equal(OrderStatus.Shipped, harness.GetOrder(10).Status);
        Assert.DoesNotContain(service.GetFillingOrders(), order => order.OrderId == 10);
    }

    [Fact]
    public void FillingList_KeepsInProgressOrderWithRemainingQty()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 2);
        var service = new ProductionPalletService(harness.Store);

        var order = Assert.Single(service.GetFillingOrders());

        Assert.Equal(10, order.OrderId);
        Assert.Equal(OrderStatusMapper.StatusToString(OrderStatus.InProgress), order.OrderStatus);
        Assert.True(order.Summary.RemainingQty > 0);
        Assert.True(order.Progress.RemainingPallets > 0);
    }

    [Fact]
    public void FillingListEndpointResponse_ContainsTsdFields()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 2);
        var payload = ProductionPalletEndpointsTestHelper.MapFillingOrders(new ProductionPalletService(harness.Store).GetFillingOrders());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));

        var row = Assert.Single(json.RootElement.EnumerateArray());
        Assert.True(row.TryGetProperty("order_id", out _));
        Assert.True(row.TryGetProperty("order_ref", out _));
        Assert.True(row.TryGetProperty("order_type", out _));
        Assert.True(row.TryGetProperty("order_status", out _));
        Assert.True(row.TryGetProperty("partner_name", out _));
        Assert.True(row.TryGetProperty("prd_doc_id", out _));
        Assert.True(row.TryGetProperty("prd_doc_ref", out _));
        Assert.True(row.TryGetProperty("summary", out _));
        Assert.True(row.TryGetProperty("required_pallets", out _));
        Assert.True(row.TryGetProperty("scanned_pallets", out _));
        Assert.True(row.TryGetProperty("remaining_pallets", out _));
        Assert.True(row.TryGetProperty("can_close", out _));
        Assert.True(row.TryGetProperty("is_closed", out _));
        Assert.True(row.TryGetProperty("operation_fingerprint", out _));
        Assert.False(row.TryGetProperty("document", out _));
    }

    private static bool HasProperty(Type type, string propertyName)
    {
        return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Any(property => string.Equals(property.Name, propertyName, StringComparison.Ordinal));
    }

    private static OutboundPickingService CreatePickingService(CloseDocumentHarness harness)
    {
        return new OutboundPickingService(harness.Store, harness.CreateService());
    }

    private static ProductionPalletService CreateAutoClosePalletService(CloseDocumentHarness harness)
    {
        var documents = harness.CreateService();
        var options = new FlowStockLedgerFlowOptions { ProductionAutoCloseOnFill = true };
        var fillClose = new ProductionFillCloseService(harness.Store, documents, options);
        return new ProductionPalletService(harness.Store, fillClose);
    }

    private static CloseDocumentHarness CreateBasicPickingHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "FG-01", Name = "Готовая продукция" });
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
        SeedCustomerOrder(harness, 20, 201, "SO-020", OrderStatus.Accepted, "HU-000001", 5);
        return harness;
    }

    private static CloseDocumentHarness CreateInternalOrderHarness(double orderQty, double maxQtyPerHu)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар",
            IsActive = true,
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = maxQtyPerHu
        });
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
        return harness;
    }

    private static CloseDocumentHarness CreateHarnessWithSixPallets(int filledCount)
    {
        var harness = new CloseDocumentHarness();
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
            QtyOrdered = 3600
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
            Qty = 600,
            ToLocationId = 1,
            ToHu = "HU-000001"
        });
        for (var i = 1; i <= 6; i++)
        {
            harness.SeedProductionPallet(new ProductionPallet
            {
                Id = i,
                PrdDocId = 20,
                DocLineId = 201,
                OrderId = 10,
                OrderLineId = 101,
                ItemId = 100,
                ItemName = "Товар",
                HuCode = $"HU-00000{i}",
                PlannedQty = 600,
                ToLocationId = 1,
                ToLocationCode = "MAIN",
                Status = i <= filledCount ? ProductionPalletStatus.Filled : ProductionPalletStatus.Planned,
                PalletNo = i,
                PalletCount = 6
            });
        }

        return harness;
    }

    private static void SeedCustomerOrder(
        CloseDocumentHarness harness,
        long orderId,
        long orderLineId,
        string orderRef,
        OrderStatus status,
        string huCode,
        double qty)
    {
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = orderRef,
            Type = OrderType.Customer,
            Status = status,
            PartnerId = 200,
            CreatedAt = new DateTime(2026, 5, 8, 9, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = orderLineId,
            OrderId = orderId,
            ItemId = 1001,
            QtyOrdered = qty,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder
        });
        harness.SeedHu(new HuRecord
        {
            Id = orderId,
            Code = huCode,
            Status = "ACTIVE",
            CreatedAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedBalance(1001, 1, qty, huCode);
        harness.SeedOrderReceiptPlanLines(orderId, new OrderReceiptPlanLine
        {
            Id = orderId,
            OrderId = orderId,
            OrderLineId = orderLineId,
            ItemId = 1001,
            ItemName = "Горчица",
            QtyPlanned = qty,
            ToLocationId = 1,
            ToLocationCode = "FG-01",
            ToHu = huCode
        });
    }

    private static string GetRepoPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}

internal static class TsdOutboundPickingEndpointsTestHelper
{
    public static object MapListRows(IReadOnlyList<OutboundPickingOrderRow> rows)
    {
        return rows.Select(row => new
        {
            order_id = row.OrderId,
            order_ref = row.OrderRef,
            partner_name = row.PartnerName,
            status = row.Status,
            expected_hu_count = row.ExpectedHuCount,
            picked_hu_count = row.PickedHuCount,
            ordered_qty = row.OrderedQty,
            shipped_qty = row.ShippedQty,
            remaining_qty = row.RemainingQty,
            scanned_qty = row.ScannedQty,
            is_complete = row.IsComplete,
            required_pallets = row.RequiredPallets,
            scanned_pallets = row.ScannedPallets,
            remaining_pallets = row.RemainingPallets,
            can_close = row.CanClose,
            is_closed = row.IsClosed,
            operation_fingerprint = row.OperationFingerprint
        }).ToArray();
    }
}

internal static class ProductionPalletEndpointsTestHelper
{
    public static object MapFillingOrders(IReadOnlyList<ProductionFillingOrder> orders)
    {
        return orders.Select(order => new
        {
            order_id = order.OrderId,
            order_ref = order.OrderRef,
            order_type = order.OrderType,
            order_type_display = order.OrderTypeDisplay,
            order_status = order.OrderStatus,
            order_status_display = order.OrderStatusDisplay,
            partner_name = order.PartnerName,
            prd_doc_id = order.PrdDocId,
            prd_doc_ref = order.PrdDocRef,
            summary = new
            {
                planned_pallet_count = order.Summary.PlannedPalletCount,
                planned_qty = order.Summary.PlannedQty,
                filled_pallet_count = order.Summary.FilledPalletCount,
                filled_qty = order.Summary.FilledQty,
                remaining_pallet_count = order.Summary.RemainingPalletCount,
                remaining_qty = order.Summary.RemainingQty
            },
            required_pallets = order.Progress.RequiredPallets,
            scanned_pallets = order.Progress.ScannedPallets,
            remaining_pallets = order.Progress.RemainingPallets,
            can_close = order.Progress.CanClose,
            is_closed = order.Progress.IsClosed,
            operation_fingerprint = order.Progress.OperationFingerprint
        }).ToArray();
    }
}

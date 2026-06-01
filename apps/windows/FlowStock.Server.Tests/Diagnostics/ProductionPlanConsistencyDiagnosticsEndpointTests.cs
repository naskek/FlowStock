using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.Diagnostics;

public sealed class ProductionPlanConsistencyDiagnosticsEndpointTests
{
    [Fact]
    public async Task ProductionPlanConsistency_OrderQtyZeroWithPallets_IsDiagnosed()
    {
        var harness = CreateHarness(orderId: 67, orderRef: "067", orderQty: 0);
        SeedProductionPalletPrd(harness, orderId: 67, orderLineId: 6701, prdDocId: 670, palletCount: 2, palletQty: 600);

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.GetAsync("/api/diagnostics/production-plan-consistency");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single((await ReadItems(response)).EnumerateArray());
        Assert.Equal(67, item.GetProperty("order_id").GetInt64());
        Assert.Equal(6, item.GetProperty("item_id").GetInt64());
        Assert.Equal(0, item.GetProperty("order_qty").GetDouble());
        Assert.Equal(1200, item.GetProperty("pallet_planned_qty").GetDouble());
        Assert.Equal(ProductionPlanConsistencyProblemCode.OrderZeroButPalletsExist, item.GetProperty("problem_code").GetString());
        Assert.Equal(ProductionPlanConsistencySeverity.Error, item.GetProperty("severity").GetString());
        Assert.Equal(2, item.GetProperty("pallets").GetArrayLength());
        Assert.Equal(2, item.GetProperty("prd_docs").GetArrayLength());
    }

    [Fact]
    public async Task ProductionPlanConsistency_PalletsExceedOrderQty_IsDiagnosed()
    {
        var harness = CreateHarness(orderId: 72, orderRef: "072", orderQty: 1200);
        SeedProductionPalletPrd(harness, orderId: 72, orderLineId: 7201, prdDocId: 720, palletCount: 4, palletQty: 600);

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.GetAsync("/api/diagnostics/production-plan-consistency");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single((await ReadItems(response)).EnumerateArray());
        Assert.Equal(1200, item.GetProperty("order_qty").GetDouble());
        Assert.Equal(2400, item.GetProperty("pallet_planned_qty").GetDouble());
        Assert.Equal(ProductionPlanConsistencyProblemCode.PalletsExceedOrderQty, item.GetProperty("problem_code").GetString());
    }

    [Fact]
    public async Task ProductionPlanConsistency_MergedOrderWithPalletPlan_IsDiagnosed()
    {
        var harness = CreateHarness(orderId: 66, orderRef: "066", orderQty: 1200, status: OrderStatus.Merged);
        SeedProductionPalletPrd(harness, orderId: 66, orderLineId: 6601, prdDocId: 660, palletCount: 2, palletQty: 600);

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.GetAsync("/api/diagnostics/production-plan-consistency");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single((await ReadItems(response)).EnumerateArray());
        Assert.Equal(ProductionPlanConsistencyProblemCode.MergedOrderWithPalletPlan, item.GetProperty("problem_code").GetString());
    }

    [Fact]
    public async Task ProductionPlanConsistency_FilledMixedPalletDraftPrdWithoutLedger_IsDiagnosedPerComponent()
    {
        var harness = CreateHarness(
            orderId: 102,
            orderRef: "102",
            orderQty: 900,
            status: OrderStatus.InProgress,
            orderType: OrderType.Customer);
        harness.SeedItem(new Item
        {
            Id = 7,
            Name = "Хрен ядреный, Мирный, 200 гр",
            BaseUom = "шт",
            MaxQtyPerHu = 900
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 10202,
            OrderId = 102,
            ItemId = 7,
            QtyOrdered = 900,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ProductionPalletGroup = "MIX-1"
        });
        harness.SeedDoc(new Doc
        {
            Id = 337,
            DocRef = "PRD-2026-000305",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 102,
            OrderRef = "102",
            CreatedAt = new DateTime(2026, 5, 30, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = 33701,
            DocId = 337,
            OrderLineId = 10201,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ItemId = 6,
            Qty = 900,
            ToLocationId = 10,
            ToHu = "HU-0000696",
            PackSingleHu = true
        });
        harness.SeedLine(new DocLine
        {
            Id = 33702,
            DocId = 337,
            OrderLineId = 10202,
            ProductionPurpose = ProductionLinePurpose.CustomerOrder,
            ItemId = 7,
            Qty = 900,
            ToLocationId = 10,
            ToHu = "HU-0000696",
            PackSingleHu = true
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 269,
            PrdDocId = 337,
            DocLineId = 33701,
            OrderId = 102,
            OrderLineId = null,
            ItemId = 6,
            ItemName = "Хрен столовый, Мирный, 200 гр",
            HuCode = "HU-0000696",
            PlannedQty = 1800,
            ToLocationId = 10,
            Status = ProductionPalletStatus.Filled,
            CreatedAt = new DateTime(2026, 5, 30, 11, 5, 0, DateTimeKind.Utc),
            Lines =
            [
                new ProductionPalletComponentLine
                {
                    Id = 26901,
                    ProductionPalletId = 269,
                    DocLineId = 33701,
                    OrderLineId = 10201,
                    ItemId = 6,
                    ItemName = "Хрен столовый, Мирный, 200 гр",
                    PlannedQty = 900,
                    FilledQty = 900,
                    CreatedAt = new DateTime(2026, 5, 30, 11, 5, 0, DateTimeKind.Utc)
                },
                new ProductionPalletComponentLine
                {
                    Id = 26902,
                    ProductionPalletId = 269,
                    DocLineId = 33702,
                    OrderLineId = 10202,
                    ItemId = 7,
                    ItemName = "Хрен ядреный, Мирный, 200 гр",
                    PlannedQty = 900,
                    FilledQty = 900,
                    CreatedAt = new DateTime(2026, 5, 30, 11, 5, 0, DateTimeKind.Utc)
                }
            ]
        });

        await using var host = await CloseDocumentHttpHost.StartAsync(harness, new InMemoryApiDocStore());

        using var response = await host.Client.GetAsync("/api/diagnostics/production-plan-consistency");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await ReadItems(response)).EnumerateArray()
            .Where(item => item.GetProperty("order_id").GetInt64() == 102)
            .OrderBy(item => item.GetProperty("item_id").GetInt64())
            .ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal([6, 7], items.Select(item => item.GetProperty("item_id").GetInt64()).ToArray());
        Assert.All(items, item =>
        {
            Assert.Equal(ProductionPlanConsistencyProblemCode.FilledPalletMissingLedger, item.GetProperty("problem_code").GetString());
            Assert.Equal(ProductionPlanConsistencySeverity.Warning, item.GetProperty("severity").GetString());
            Assert.Equal(900, item.GetProperty("pallet_filled_qty").GetDouble());
            Assert.Equal(0, item.GetProperty("ledger_open_prd_qty").GetDouble());
        });
    }

    private static async Task<JsonElement> ReadItems(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("ok").GetBoolean());
        return payload.GetProperty("items");
    }

    internal static CloseDocumentHarness CreateHarness(
        long orderId,
        string orderRef,
        double orderQty,
        OrderStatus status = OrderStatus.InProgress,
        OrderType orderType = OrderType.Internal)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item
        {
            Id = 6,
            Name = "Горчица, Печагин, 1 кг",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedLocation(new Location
        {
            Id = 10,
            Code = "A1",
            Name = "Производство"
        });
        harness.SeedOrder(new Order
        {
            Id = orderId,
            OrderRef = orderRef,
            Type = orderType,
            Status = status,
            CreatedAt = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = orderId * 100 + 1,
            OrderId = orderId,
            ItemId = 6,
            QtyOrdered = orderQty,
            ProductionPurpose = ProductionLinePurpose.InternalStock
        });

        return harness;
    }

    internal static void SeedProductionPalletPrd(
        CloseDocumentHarness harness,
        long orderId,
        long orderLineId,
        long prdDocId,
        int palletCount,
        double palletQty,
        string palletStatus = ProductionPalletStatus.Printed,
        bool seedLedger = false)
    {
        harness.SeedDoc(new Doc
        {
            Id = prdDocId,
            DocRef = $"PRD-{prdDocId:000}",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = orderId,
            OrderRef = orderId.ToString("000"),
            CreatedAt = new DateTime(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc)
        });

        for (var index = 0; index < palletCount; index++)
        {
            var docLineId = prdDocId * 100 + index + 1;
            var palletId = prdDocId * 10 + index + 1;
            var huCode = $"HU-{prdDocId:000}{index + 1:000}";
            harness.SeedLine(new DocLine
            {
                Id = docLineId,
                DocId = prdDocId,
                OrderLineId = orderLineId,
                ProductionPurpose = ProductionLinePurpose.InternalStock,
                ItemId = 6,
                Qty = palletQty,
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
                ItemName = "Горчица, Печагин, 1 кг",
                HuCode = huCode,
                PlannedQty = palletQty,
                ToLocationId = 10,
                Status = palletStatus,
                PalletNo = index + 1,
                PalletCount = palletCount,
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
                        ItemName = "Горчица, Печагин, 1 кг",
                        PlannedQty = palletQty,
                        FilledQty = string.Equals(palletStatus, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase) ? palletQty : 0d,
                        CreatedAt = new DateTime(2026, 5, 1, 11, 5, 0, DateTimeKind.Utc)
                    }
                }
            });

            if (seedLedger)
            {
                harness.SeedLedgerEntry(prdDocId, 6, 10, palletQty, huCode);
            }
        }
    }

    internal static void SeedStaleOpenPrdDraft(
        CloseDocumentHarness harness,
        long orderId,
        long orderLineId,
        long prdDocId,
        double prdDocQty)
    {
        harness.SeedDoc(new Doc
        {
            Id = prdDocId,
            DocRef = $"PRD-{prdDocId:000}",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = orderId,
            OrderRef = orderId.ToString("000"),
            CreatedAt = new DateTime(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = prdDocId * 100,
            DocId = prdDocId,
            OrderLineId = orderLineId,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ItemId = 6,
            Qty = prdDocQty,
            ToLocationId = 10,
            PackSingleHu = false
        });
    }

    internal static void SeedLegacyClosedPrdWithPartialPalletPlan(
        CloseDocumentHarness harness,
        long orderId,
        long orderLineId,
        long closedPrdDocId,
        double closedPrdQty,
        double palletPlannedQty,
        bool seedLedger)
    {
        var legacyQty = closedPrdQty - palletPlannedQty;
        harness.SeedDoc(new Doc
        {
            Id = closedPrdDocId,
            DocRef = $"PRD-{closedPrdDocId:000}",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = orderId,
            OrderRef = orderId.ToString("000"),
            ClosedAt = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 4, 1, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLine(new DocLine
        {
            Id = closedPrdDocId * 100,
            DocId = closedPrdDocId,
            OrderLineId = orderLineId,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ItemId = 6,
            Qty = legacyQty,
            ToLocationId = 10,
            ToHu = "LEGACY-HU",
            PackSingleHu = false
        });

        var palletCount = (int)(palletPlannedQty / 600);
        for (var index = 0; index < palletCount; index++)
        {
            var palletId = closedPrdDocId * 10 + index + 1;
            var docLineId = closedPrdDocId * 100 + index + 1;
            var huCode = $"HU-{closedPrdDocId:000}{index + 1:000}";
            harness.SeedLine(new DocLine
            {
                Id = docLineId,
                DocId = closedPrdDocId,
                OrderLineId = orderLineId,
                ProductionPurpose = ProductionLinePurpose.InternalStock,
                ItemId = 6,
                Qty = 600,
                ToLocationId = 10,
                ToHu = huCode,
                PackSingleHu = true
            });
            harness.SeedProductionPallet(new ProductionPallet
            {
                Id = palletId,
                PrdDocId = closedPrdDocId,
                DocLineId = docLineId,
                OrderId = orderId,
                OrderLineId = orderLineId,
                ItemId = 6,
                ItemName = "Горчица, Печагин, 1 кг",
                HuCode = huCode,
                PlannedQty = 600,
                ToLocationId = 10,
                Status = ProductionPalletStatus.Filled,
                PalletNo = index + 1,
                PalletCount = palletCount,
                CreatedAt = new DateTime(2026, 4, 1, 11, 5, 0, DateTimeKind.Utc),
                Lines = new[]
                {
                    new ProductionPalletComponentLine
                    {
                        Id = palletId * 100,
                        ProductionPalletId = palletId,
                        DocLineId = docLineId,
                        OrderLineId = orderLineId,
                        ItemId = 6,
                        ItemName = "Горчица, Печагин, 1 кг",
                        PlannedQty = 600,
                        FilledQty = 600,
                        CreatedAt = new DateTime(2026, 4, 1, 11, 5, 0, DateTimeKind.Utc)
                    }
                }
            });

            if (seedLedger)
            {
                harness.SeedLedgerEntry(closedPrdDocId, 6, 10, 600, huCode);
            }
        }

        if (seedLedger)
        {
            harness.SeedLedgerEntry(closedPrdDocId, 6, 10, legacyQty, "LEGACY-HU");
        }
    }

    internal static void SeedReplacedPrdLine(
        CloseDocumentHarness harness,
        long prdDocId,
        long orderLineId,
        long supersededLineId,
        long activeLineId,
        double supersededQty,
        double activeQty)
    {
        harness.SeedLine(new DocLine
        {
            Id = supersededLineId,
            DocId = prdDocId,
            OrderLineId = orderLineId,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ItemId = 6,
            Qty = supersededQty,
            ToLocationId = 10,
            ToHu = $"HU-{supersededLineId:000}",
            PackSingleHu = true
        });
        harness.SeedLine(new DocLine
        {
            Id = activeLineId,
            DocId = prdDocId,
            OrderLineId = orderLineId,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ItemId = 6,
            Qty = activeQty,
            ToLocationId = 10,
            ToHu = $"HU-{activeLineId:000}",
            PackSingleHu = true,
            ReplacesLineId = supersededLineId
        });
    }
}

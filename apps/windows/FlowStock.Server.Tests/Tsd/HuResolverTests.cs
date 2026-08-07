using System.Net;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowStock.Server.Tests.Tsd;

public sealed class HuResolverTests
{
    [Fact]
    public void UnknownHu_ReturnsUnknownWithoutActions()
    {
        var result = Resolve(new TsdHuFacts { HuCode = "HU-999999" });

        Assert.False(result.Known);
        Assert.Equal(TsdHuState.Unknown, result.State);
        Assert.Null(result.CardAction);
        Assert.Empty(result.DocumentActions);
    }

    [Fact]
    public void WarehouseFreeHu_ReturnsCardAction()
    {
        var result = Resolve(new TsdHuFacts
        {
            HuCode = "HU-000321",
            Stock = new[]
            {
                new TsdHuStockFact { ItemId = 1, ItemName = "Товар", LocationId = 2, LocationCode = "MAIN", Qty = 600 }
            }
        });

        Assert.True(result.Known);
        Assert.Equal(TsdHuState.WarehouseFree, result.State);
        Assert.Equal(TsdHuActionType.OpenHuCard, result.CardAction?.Type);
        Assert.Empty(result.DocumentActions);
    }

    [Fact]
    public void CustomerOwnedFilledProductionHuWithLedgerStock_ReturnsAwaitingShipment()
    {
        var facts = new TsdHuFacts
        {
            HuCode = "HU-0001303",
            Stock =
            [
                new TsdHuStockFact
                {
                    ItemId = 10,
                    ItemName = "Товар",
                    LocationId = 20,
                    LocationCode = "MAIN",
                    Qty = 600
                }
            ],
            ProductionPallets =
            [
                new TsdHuProductionPalletFact
                {
                    PalletId = 30,
                    Status = ProductionPalletStatus.Filled,
                    PrdDocId = 40,
                    PrdDocRef = "PRD-040",
                    OrderId = 217,
                    OrderRef = "217",
                    OrderType = "CUSTOMER",
                    OrderStatus = "IN_PROGRESS",
                    Components =
                    [
                        new TsdHuComponentFact
                        {
                            ItemId = 10,
                            ItemName = "Товар",
                            PlannedQty = 600,
                            FilledQty = 600
                        }
                    ]
                }
            ]
        };
        var result = Resolve(
            facts,
            new ProductionHuAwaitingShipmentEligibilityFacts
            {
                PalletId = 30,
                PersistedPalletStatus = ProductionPalletStatus.Filled,
                OwnerOrderId = 217,
                OwnerOrderRef = "217",
                OwnerOrderType = "CUSTOMER",
                OwnerOrderStatus = "IN_PROGRESS",
                EvaluatedOrderId = 217,
                Components =
                [
                    new ProductionHuAwaitingShipmentComponentFact
                    {
                        OrderLineId = 50,
                        OrderLineOrderId = 217,
                        ItemId = 10,
                        HuCode = "HU-0001303",
                        PlannedQty = 600,
                        FilledQty = 600
                    }
                ],
                ComponentKeys =
                [
                    new ProductionHuAwaitingShipmentComponentKeyFact
                    {
                        ItemId = 10,
                        HuCode = "HU-0001303",
                        LedgerBalance = 600
                    }
                ]
            });

        Assert.Equal("AWAITING_SHIPMENT", result.State);
        Assert.Equal("Ожидает отгрузки", result.Title);
        Assert.Equal(
            "HU наполнена и ожидает отгрузки по клиентскому заказу. Заказ 217.",
            result.Description);
        Assert.Contains(
            result.DocumentActions,
            action => action.Type == TsdHuActionType.OpenOrder && action.OrderId == 217);
        Assert.DoesNotContain(
            result.DocumentActions,
            action => action.Type == TsdHuActionType.OpenOutbound);
    }

    [Fact]
    public void PlannedPallet_ReturnsOpenFilling()
    {
        var result = Resolve(new TsdHuFacts
        {
            HuCode = "HU-000123",
            ProductionPallets = new[]
            {
                new TsdHuProductionPalletFact
                {
                    PalletId = 1,
                    Status = ProductionPalletStatus.Planned,
                    PrdDocId = 10,
                    PrdDocRef = "PRD-010",
                    OrderId = 20,
                    OrderRef = "005"
                }
            }
        });

        Assert.Equal(TsdHuState.PlannedProduction, result.State);
        Assert.Contains(result.DocumentActions, action => action.Type == TsdHuActionType.OpenFilling && action.OrderId == 20);
        Assert.Null(result.OperatorReadModel.OperatorPresentation.ProductionTask);
        Assert.Null(result.OperatorReadModel.OperatorPresentation.OperationalHu);
    }

    [Fact]
    public void CompleteMixedProductionHuWithoutBlockers_ReturnsAwaitingShipment()
    {
        var facts = MixedFilledFacts(includeStock: true);

        var result = Resolve(facts, MixedAwaitingCandidate());

        Assert.Equal(TsdHuState.AwaitingShipment, result.State);
    }

    [Fact]
    public void MixedProductionHuReservationOnOneComponent_ReturnsOutboundExpected()
    {
        var facts = MixedFilledFacts(includeStock: true);
        facts = CopyFacts(
            facts,
            reservations:
            [
                new TsdHuReservationFact
                {
                    OrderId = 218,
                    OrderRef = "218",
                    OrderType = "CUSTOMER",
                    OrderStatus = "ACCEPTED",
                    ItemId = 10,
                    ItemName = "Товар 1",
                    Qty = 300
                }
            ]);

        var result = Resolve(facts, MixedAwaitingCandidate(reservationItemId: 10));

        Assert.Equal(TsdHuState.OutboundExpected, result.State);
        Assert.NotEqual(TsdHuState.AwaitingShipment, result.State);
    }

    [Fact]
    public void MixedProductionHuDraftOutboundOnOneComponent_ReturnsOutboundPicked()
    {
        var facts = MixedFilledFacts(includeStock: true);
        facts = CopyFacts(
            facts,
            documents:
            [
                OutboundDocument(itemId: 10, status: "DRAFT")
            ]);

        var result = Resolve(facts, MixedAwaitingCandidate());

        Assert.Equal(TsdHuState.OutboundPicked, result.State);
        Assert.NotEqual(TsdHuState.AwaitingShipment, result.State);
    }

    [Fact]
    public void MixedProductionHuPartialClosedShipmentWithRemainingStock_UsesFilledFallback()
    {
        var facts = MixedFilledFacts(includeStock: true);
        facts = CopyFacts(
            facts,
            documents:
            [
                OutboundDocument(itemId: 10, status: "CLOSED")
            ]);

        var result = Resolve(facts, MixedAwaitingCandidate(shipmentItemId: 10));

        Assert.Equal(TsdHuState.FilledProductionPallet, result.State);
        Assert.NotEqual(TsdHuState.AwaitingShipment, result.State);
        Assert.NotEqual(TsdHuState.Shipped, result.State);
        var operational = Assert.IsType<OperationalHuPresentation>(
            result.OperatorReadModel.OperatorPresentation.OperationalHu);
        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.State.Code);
        Assert.Contains(
            operational.Diagnostics ?? [],
            reason => reason.Code == HuOperatorDiagnosticCode.CorrectionLineageUncertain);
    }

    [Fact]
    public void MixedProductionHuClosedShipmentWithoutAnyStock_ReturnsShipped()
    {
        var facts = MixedFilledFacts(includeStock: false);
        facts = CopyFacts(
            facts,
            documents:
            [
                OutboundDocument(itemId: 10, status: "CLOSED")
            ]);

        var result = Resolve(facts, MixedAwaitingCandidate(shipmentItemId: 10, ledgerBalance: 0));

        Assert.Equal(TsdHuState.Shipped, result.State);
        var operational = Assert.IsType<OperationalHuPresentation>(
            result.OperatorReadModel.OperatorPresentation.OperationalHu);
        Assert.Equal(OperationalHuSemanticCode.Inconsistent, operational.State.Code);
        Assert.Contains(
            operational.Diagnostics ?? [],
            reason => reason.Code == HuOperatorDiagnosticCode.CorrectionLineageUncertain);
    }

    [Fact]
    public void MixedProductionHuShipmentBlockerOnOneComponentSuppressesAwaitingForWholeHu()
    {
        var result = Resolve(
            MixedFilledFacts(includeStock: true),
            MixedAwaitingCandidate(shipmentItemId: 11));

        Assert.Equal(TsdHuState.FilledProductionPallet, result.State);
    }

    [Fact]
    public void SeveralCurrentFilledPalletCandidatesFailClosed()
    {
        var first = MixedAwaitingCandidate();
        var second = MixedAwaitingCandidate(palletId: 31);

        var result = Resolve(MixedFilledFacts(includeStock: true), first, second);

        Assert.Equal(TsdHuState.FilledProductionPallet, result.State);
    }

    [Fact]
    public void ActiveCustomerReservation_ReturnsOpenOutbound()
    {
        var result = Resolve(new TsdHuFacts
        {
            HuCode = "HU-000124",
            Reservations = new[]
            {
                new TsdHuReservationFact
                {
                    OrderId = 21,
                    OrderRef = "006",
                    OrderType = "CUSTOMER",
                    OrderStatus = "ACCEPTED",
                    ItemId = 1,
                    ItemName = "Товар",
                    Qty = 600
                }
            }
        });

        Assert.Equal(TsdHuState.OutboundExpected, result.State);
        Assert.Contains(result.DocumentActions, action => action.Type == TsdHuActionType.OpenOutbound && action.OrderId == 21);
    }

    [Fact]
    public void ClosedOutboundWithoutStock_ReturnsShipped()
    {
        var result = Resolve(new TsdHuFacts
        {
            HuCode = "HU-000125",
            Documents = new[]
            {
                new TsdHuDocumentFact
                {
                    DocId = 30,
                    DocRef = "OUT-030",
                    DocType = "OUTBOUND",
                    DocStatus = "CLOSED",
                    OrderId = 22,
                    OrderRef = "007",
                    ItemId = 1,
                    ItemName = "Товар",
                    Qty = 600
                }
            }
        });

        Assert.Equal(TsdHuState.Shipped, result.State);
        Assert.Contains(result.DocumentActions, action => action.Type == TsdHuActionType.OpenDocument && action.DocId == 30);
        Assert.Contains(result.DocumentActions, action => action.Type == TsdHuActionType.OpenOrder && action.OrderId == 22);
    }

    [Fact]
    public void MultipleActiveOperations_ReturnAmbiguousWithSeveralActions()
    {
        var result = Resolve(new TsdHuFacts
        {
            HuCode = "HU-000126",
            Reservations = new[]
            {
                new TsdHuReservationFact
                {
                    OrderId = 23,
                    OrderRef = "008",
                    OrderType = "CUSTOMER",
                    OrderStatus = "ACCEPTED",
                    ItemId = 1,
                    ItemName = "Товар",
                    Qty = 600
                }
            },
            ProductionPallets = new[]
            {
                new TsdHuProductionPalletFact
                {
                    PalletId = 2,
                    Status = ProductionPalletStatus.Planned,
                    PrdDocId = 31,
                    PrdDocRef = "PRD-031",
                    OrderId = 24,
                    OrderRef = "009"
                }
            }
        });

        Assert.Equal(TsdHuState.Ambiguous, result.State);
        Assert.Contains(result.DocumentActions, action => action.Type == TsdHuActionType.OpenOutbound);
        Assert.Contains(result.DocumentActions, action => action.Type == TsdHuActionType.OpenFilling);
    }

    [Fact]
    public void DraftDocument_RemainsReadOnlyRelationWithoutOpenDocumentAction()
    {
        var result = Resolve(new TsdHuFacts
        {
            HuCode = "HU-000127",
            Documents = new[]
            {
                new TsdHuDocumentFact
                {
                    DocId = 32,
                    DocRef = "MOV-032",
                    DocType = "MOVE",
                    DocStatus = "DRAFT",
                    ItemId = 1,
                    ItemName = "Товар",
                    Qty = 600
                }
            }
        });

        Assert.Single(result.Documents);
        Assert.DoesNotContain(result.DocumentActions, action => action.Type == TsdHuActionType.OpenDocument);
    }

    [Fact]
    public async Task ResolveAndCardEndpoints_AreReadOnlyGetEndpoints()
    {
        var store = new FakeStore(new TsdHuFacts
        {
            HuCode = "HU-000321",
            Stock = new[]
            {
                new TsdHuStockFact { ItemId = 1, ItemName = "Товар", LocationId = 2, LocationCode = "MAIN", Qty = 600 }
            }
        });
        await using var host = await HuResolverHost.StartAsync(store);

        using var resolveResponse = await host.Client.GetAsync("/api/tsd/hu/resolve?code=HU-000321");
        using var cardResponse = await host.Client.GetAsync("/api/tsd/hu/card?code=HU-000321");

        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cardResponse.StatusCode);
        using var resolveJson = JsonDocument.Parse(await resolveResponse.Content.ReadAsStringAsync());
        using var cardJson = JsonDocument.Parse(await cardResponse.Content.ReadAsStringAsync());
        Assert.Equal("WAREHOUSE_FREE", resolveJson.RootElement.GetProperty("state").GetString());
        var operatorPresentation = resolveJson.RootElement.GetProperty("operator_presentation");
        Assert.Equal(
            "ON_STOCK",
            operatorPresentation.GetProperty("operational_hu").GetProperty("state").GetProperty("code").GetString());
        var component = Assert.Single(
            operatorPresentation.GetProperty("operational_hu").GetProperty("components").EnumerateArray());
        Assert.Equal(600, component.GetProperty("qty").GetDouble(), 3);
        Assert.False(component.TryGetProperty("planned_qty", out _));
        Assert.False(component.TryGetProperty("filled_qty", out _));
        Assert.False(component.TryGetProperty("order_line_id", out _));
        Assert.False(component.TryGetProperty("order_line_order_id", out _));
        Assert.Equal(JsonValueKind.Null, operatorPresentation.GetProperty("production_task").ValueKind);
        Assert.Equal(JsonValueKind.Null, resolveJson.RootElement.GetProperty("stock").ValueKind);
        Assert.Equal(1, cardJson.RootElement.GetProperty("stock").GetArrayLength());
        Assert.Equal(2, store.Calls.Count);
        Assert.All(store.Calls, code => Assert.Equal("HU-000321", code));
    }

    [Fact]
    public async Task ResolveEndpoint_ProductionComponentsExposeOnlyPresentationShape()
    {
        var store = new FakeStore(new TsdHuFacts
        {
            HuCode = "HU-000654",
            ProductionPallets =
            [
                new TsdHuProductionPalletFact
                {
                    PalletId = 1,
                    Status = ProductionPalletStatus.Printed,
                    Components =
                    [
                        new TsdHuComponentFact
                        {
                            ItemId = 7,
                            ItemName = "Товар",
                            Uom = "шт",
                            PlannedQty = 25,
                            FilledQty = 0
                        }
                    ]
                }
            ]
        });
        await using var host = await HuResolverHost.StartAsync(store);

        using var response = await host.Client.GetAsync("/api/tsd/hu/resolve?code=HU-000654");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var production = json.RootElement
            .GetProperty("operator_presentation")
            .GetProperty("production_task");
        Assert.Equal("AWAITING_FILL", production.GetProperty("state").GetProperty("code").GetString());
        var component = Assert.Single(production.GetProperty("components").EnumerateArray());
        Assert.Equal(25, component.GetProperty("qty").GetDouble(), 3);
        Assert.False(component.TryGetProperty("planned_qty", out _));
        Assert.False(component.TryGetProperty("filled_qty", out _));
        Assert.False(component.TryGetProperty("order_line_id", out _));
        Assert.False(component.TryGetProperty("order_line_order_id", out _));
    }

    [Fact]
    public async Task AwaitingShipmentResolveAndCardEndpoints_ReturnSameSummaryAndOnlyOrderAction()
    {
        var facts = MixedFilledFacts(includeStock: true);
        var store = new FakeStore(new TsdHuResolverStoreResult
        {
            PresentationFacts = facts,
            AwaitingShipmentCandidates = [MixedAwaitingCandidate()]
        });
        await using var host = await HuResolverHost.StartAsync(store);

        using var resolveResponse = await host.Client.GetAsync("/api/tsd/hu/resolve?code=HU-0001303");
        using var cardResponse = await host.Client.GetAsync("/api/tsd/hu/card?code=HU-0001303");
        using var resolveJson = JsonDocument.Parse(await resolveResponse.Content.ReadAsStringAsync());
        using var cardJson = JsonDocument.Parse(await cardResponse.Content.ReadAsStringAsync());

        Assert.Equal("AWAITING_SHIPMENT", resolveJson.RootElement.GetProperty("state").GetString());
        Assert.Equal("Ожидает отгрузки", resolveJson.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "HU наполнена и ожидает отгрузки по клиентскому заказу. Заказ 217.",
            resolveJson.RootElement.GetProperty("description").GetString());
        Assert.Equal(
            resolveJson.RootElement.GetProperty("state").GetString(),
            cardJson.RootElement.GetProperty("state").GetString());
        Assert.Equal(
            resolveJson.RootElement.GetProperty("title").GetString(),
            cardJson.RootElement.GetProperty("title").GetString());
        Assert.False(resolveJson.RootElement.TryGetProperty("awaiting_shipment_candidates", out _));
        Assert.False(cardJson.RootElement.TryGetProperty("awaiting_shipment_candidates", out _));
        var actions = resolveJson.RootElement.GetProperty("document_actions").EnumerateArray().ToArray();
        Assert.Contains(actions, action => action.GetProperty("type").GetString() == "OPEN_ORDER");
        Assert.DoesNotContain(actions, action => action.GetProperty("type").GetString() == "OPEN_OUTBOUND");
        Assert.Equal(2, store.Calls.Count);
    }

    [Fact]
    public void PostgresResolver_UsesSingleScopedCommandWithoutGlobalStoreWalks()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");
        var start = source.IndexOf("public TsdHuResolverStoreResult GetTsdHuFacts", StringComparison.Ordinal);
        var end = source.IndexOf("public IReadOnlyList<ScopedOrderLineHuFateCandidate>", start, StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains("using var command = CreateCommand(connection", method);
        Assert.Contains("WHERE UPPER(BTRIM(COALESCE(l.hu_code, l.hu))) = @hu_code", method);
        Assert.Contains("WHERE UPPER(BTRIM(pp.hu_code)) = @hu_code", method);
        Assert.Contains("WHERE UPPER(BTRIM(p.to_hu)) = @hu_code", method);
        Assert.Contains("WITH target_pallets AS", method);
        Assert.Contains("FROM production_pallet_lines line", method);
        Assert.Contains("COALESCE(SUM(ledger_row.qty_delta), 0)", method);
        Assert.Contains("newer.replaces_line_id = shipment_line.id", method);
        Assert.DoesNotContain("EffectiveStatus", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDocs(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetOrders(", method, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(method, "CreateCommand(connection"));
    }

    [Fact]
    public void PostgresOrderOperatorFacts_UsesOneOrderScopedBatchCommand()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");
        var start = source.IndexOf("private IReadOnlyList<HuOperatorFacts> LoadHuOperatorFacts", StringComparison.Ordinal);
        var end = source.IndexOf("public TsdHuResolverStoreResult GetTsdHuFacts", start, StringComparison.Ordinal);
        var method = source[start..end];
        var globalFactsStart = method.IndexOf("stock AS (", StringComparison.Ordinal);
        var globalFactsEnd = method.IndexOf("ORDER BY target.hu_code;", globalFactsStart, StringComparison.Ordinal);
        var globalFactsCtes = method[globalFactsStart..globalFactsEnd];

        Assert.Contains("WITH target_hus AS", method);
        Assert.Contains("SELECT @hu_code::text AS hu_code", method);
        Assert.Contains("INNER JOIN target_hus target", method);
        Assert.Contains("FROM stock row", method);
        Assert.Contains("FROM production row", method);
        Assert.Contains("FROM reservations row", method);
        Assert.Contains("FROM outbound row", method);
        Assert.Contains("FROM movements row", method);
        Assert.DoesNotContain("@order_id", globalFactsCtes, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(method, "CreateCommand(connection"));
        Assert.DoesNotContain("HuOperatorClassifier", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetTsdHuFacts", method, StringComparison.Ordinal);
    }

    private static TsdHuView Resolve(
        TsdHuFacts facts,
        params ProductionHuAwaitingShipmentEligibilityFacts[] awaitingShipmentCandidates)
    {
        var store = new FakeStore(new TsdHuResolverStoreResult
        {
            PresentationFacts = facts,
            AwaitingShipmentCandidates = awaitingShipmentCandidates
        });
        return new TsdHuResolverService(store, new HuOperatorReadModelService(store)).Resolve(facts.HuCode);
    }

    private static TsdHuFacts MixedFilledFacts(bool includeStock)
    {
        return new TsdHuFacts
        {
            HuCode = "HU-0001303",
            Stock = includeStock
                ?
                [
                    new TsdHuStockFact
                    {
                        ItemId = 10,
                        ItemName = "Товар 1",
                        LocationId = 20,
                        LocationCode = "MAIN",
                        Qty = 300
                    },
                    new TsdHuStockFact
                    {
                        ItemId = 11,
                        ItemName = "Товар 2",
                        LocationId = 20,
                        LocationCode = "MAIN",
                        Qty = 300
                    }
                ]
                : Array.Empty<TsdHuStockFact>(),
            ProductionPallets =
            [
                new TsdHuProductionPalletFact
                {
                    PalletId = 30,
                    Status = ProductionPalletStatus.Filled,
                    PrdDocId = 40,
                    PrdDocRef = "PRD-040",
                    OrderId = 217,
                    OrderRef = "217",
                    OrderType = "CUSTOMER",
                    OrderStatus = "IN_PROGRESS",
                    Components =
                    [
                        new TsdHuComponentFact
                        {
                            ItemId = 10,
                            ItemName = "Товар 1",
                            PlannedQty = 300,
                            FilledQty = 300
                        },
                        new TsdHuComponentFact
                        {
                            ItemId = 11,
                            ItemName = "Товар 2",
                            PlannedQty = 300,
                            FilledQty = 300
                        }
                    ]
                }
            ]
        };
    }

    private static ProductionHuAwaitingShipmentEligibilityFacts MixedAwaitingCandidate(
        long palletId = 30,
        long? reservationItemId = null,
        long? shipmentItemId = null,
        double ledgerBalance = 300)
    {
        return new ProductionHuAwaitingShipmentEligibilityFacts
        {
            PalletId = palletId,
            PersistedPalletStatus = ProductionPalletStatus.Filled,
            OwnerOrderId = 217,
            OwnerOrderRef = "217",
            OwnerOrderType = "CUSTOMER",
            OwnerOrderStatus = "IN_PROGRESS",
            EvaluatedOrderId = 217,
            Components =
            [
                new ProductionHuAwaitingShipmentComponentFact
                {
                    OrderLineId = 50,
                    OrderLineOrderId = 217,
                    ItemId = 10,
                    HuCode = "HU-0001303",
                    PlannedQty = 300,
                    FilledQty = 300
                },
                new ProductionHuAwaitingShipmentComponentFact
                {
                    OrderLineId = 51,
                    OrderLineOrderId = 217,
                    ItemId = 11,
                    HuCode = "HU-0001303",
                    PlannedQty = 300,
                    FilledQty = 300
                }
            ],
            ComponentKeys = new[] { 10L, 11L }
                .Select(itemId => new ProductionHuAwaitingShipmentComponentKeyFact
                {
                    ItemId = itemId,
                    HuCode = "HU-0001303",
                    LedgerBalance = ledgerBalance,
                    HasActiveReservation = reservationItemId == itemId,
                    HasActiveShipment = shipmentItemId == itemId
                })
                .ToArray()
        };
    }

    private static TsdHuDocumentFact OutboundDocument(long itemId, string status) =>
        new()
        {
            DocId = 60,
            DocRef = "OUT-060",
            DocType = "OUTBOUND",
            DocStatus = status,
            OrderId = 217,
            OrderRef = "217",
            OrderType = "CUSTOMER",
            OrderStatus = "IN_PROGRESS",
            Direction = "FROM",
            ItemId = itemId,
            ItemName = "Товар",
            Qty = 300
        };

    private static TsdHuFacts CopyFacts(
        TsdHuFacts facts,
        IReadOnlyList<TsdHuReservationFact>? reservations = null,
        IReadOnlyList<TsdHuDocumentFact>? documents = null) =>
        new()
        {
            HuCode = facts.HuCode,
            Registry = facts.Registry,
            Stock = facts.Stock,
            ProductionPallets = facts.ProductionPallets,
            Reservations = reservations ?? facts.Reservations,
            Documents = documents ?? facts.Documents,
            LatestMovement = facts.LatestMovement
        };

    private static int CountOccurrences(string value, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }

    private sealed class FakeStore : ITsdHuResolverStore, IHuOperatorFactsStore
    {
        private readonly TsdHuResolverStoreResult _result;

        public FakeStore(TsdHuFacts facts)
            : this(new TsdHuResolverStoreResult { PresentationFacts = facts })
        {
        }

        public FakeStore(TsdHuResolverStoreResult result)
        {
            _result = result;
        }

        public List<string> Calls { get; } = new();

        public TsdHuResolverStoreResult GetTsdHuFacts(string huCode)
        {
            Calls.Add(huCode);
            return _result;
        }

        public IReadOnlyList<HuOperatorFacts> GetForOrder(long orderId) => Array.Empty<HuOperatorFacts>();

        public HuOperatorFacts? GetForHu(string huCode)
        {
            var facts = _result.PresentationFacts;
            return new HuOperatorFacts
            {
                HuCode = facts.HuCode,
                RegistryKnown = facts.Registry != null,
                Stock = facts.Stock.Select(row => new HuOperatorStockFact
                {
                    ItemId = row.ItemId,
                    ItemName = row.ItemName,
                    Uom = row.Uom,
                    LocationId = row.LocationId,
                    LocationCode = row.LocationCode,
                    Qty = row.Qty
                }).ToArray(),
                ProductionPallets = facts.ProductionPallets.Select(pallet => new HuOperatorProductionPalletFact
                {
                    PalletId = pallet.PalletId,
                    Status = pallet.Status,
                    OwnerOrderId = pallet.OrderId,
                    OwnerOrderRef = pallet.OrderRef,
                    OwnerOrderType = pallet.OrderType,
                    OwnerOrderStatus = pallet.OrderStatus,
                    Components = pallet.Components.Select(component => new HuOperatorComponentFact
                    {
                        ItemId = component.ItemId,
                        ItemName = component.ItemName,
                        Uom = component.Uom,
                        PlannedQty = component.PlannedQty,
                        FilledQty = component.FilledQty
                    }).ToArray()
                }).ToArray(),
                Reservations = facts.Reservations.Select(row => new HuOperatorReservationFact
                {
                    OrderId = row.OrderId,
                    OrderRef = row.OrderRef,
                    OrderType = row.OrderType,
                    OrderStatus = row.OrderStatus,
                    ItemId = row.ItemId,
                    Qty = row.Qty
                }).ToArray(),
                Outbound = facts.Documents
                    .Where(row => string.Equals(row.DocType, "OUTBOUND", StringComparison.OrdinalIgnoreCase))
                    .Select(row => new HuOperatorOutboundFact
                    {
                        DocumentId = row.DocId,
                        DocumentRef = row.DocRef,
                        DocumentStatus = row.DocStatus,
                        OrderId = row.OrderId,
                        OrderRef = row.OrderRef,
                        OrderType = row.OrderType,
                        OrderStatus = row.OrderStatus,
                        ItemId = row.ItemId,
                        ItemName = row.ItemName,
                        Uom = row.Uom,
                        Qty = row.Qty,
                        ClosedAt = row.ClosedAt
                    }).ToArray()
            };
        }
    }

    private sealed class HuResolverHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private HuResolverHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<HuResolverHost> StartAsync(ITsdHuResolverStore store)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(TsdHuResolverEndpoints).Assembly.FullName,
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton<IHuOperatorFactsStore>((IHuOperatorFactsStore)store);
            builder.Services.AddSingleton<HuOperatorReadModelService>();
            builder.Services.AddSingleton<TsdHuResolverService>();
            var app = builder.Build();
            TsdHuResolverEndpoints.Map(app);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single();
            return new HuResolverHost(app, new HttpClient { BaseAddress = new Uri(address!, UriKind.Absolute) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

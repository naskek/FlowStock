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
        Assert.Equal(JsonValueKind.Null, resolveJson.RootElement.GetProperty("stock").ValueKind);
        Assert.Equal(1, cardJson.RootElement.GetProperty("stock").GetArrayLength());
        Assert.Equal(2, store.Calls.Count);
        Assert.All(store.Calls, code => Assert.Equal("HU-000321", code));
    }

    [Fact]
    public void PostgresResolver_UsesSingleScopedCommandWithoutGlobalStoreWalks()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");
        var start = source.IndexOf("public TsdHuFacts GetTsdHuFacts", StringComparison.Ordinal);
        var end = source.IndexOf("public IReadOnlyList<ScopedOrderLineHuFateCandidate>", start, StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains("using var command = CreateCommand(connection", method);
        Assert.Contains("WHERE UPPER(BTRIM(COALESCE(l.hu_code, l.hu))) = @hu_code", method);
        Assert.Contains("WHERE UPPER(BTRIM(pp.hu_code)) = @hu_code", method);
        Assert.Contains("WHERE UPPER(BTRIM(p.to_hu)) = @hu_code", method);
        Assert.DoesNotContain("GetDocs(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetOrders(", method, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(method, "CreateCommand(connection"));
    }

    private static TsdHuView Resolve(TsdHuFacts facts)
        => new TsdHuResolverService(new FakeStore(facts)).Resolve(facts.HuCode);

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

    private sealed class FakeStore : ITsdHuResolverStore
    {
        private readonly TsdHuFacts _facts;

        public FakeStore(TsdHuFacts facts)
        {
            _facts = facts;
        }

        public List<string> Calls { get; } = new();

        public TsdHuFacts GetTsdHuFacts(string huCode)
        {
            Calls.Add(huCode);
            return _facts;
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

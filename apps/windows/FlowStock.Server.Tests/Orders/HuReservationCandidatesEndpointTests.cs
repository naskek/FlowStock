using System.Net;
using System.Net.Http.Json;
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
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class HuReservationCandidatesEndpointTests
{
    [Fact]
    public async Task ReturnsLedgerStockCandidates()
    {
        var store = CreateStore(
        [
            Source("LEDGER_STOCK", "HU-LEDGER", itemId: 6, qty: 600, shipReady: true)
        ]);
        await using var host = await HuReservationCandidatesHost.StartAsync(store.Object);

        using var document = await PostAsync(host.Client, new
        {
            order_id = 78L,
            lines = new[]
            {
                new { client_line_key = "line-1", order_line_id = (long?)null, item_id = 6L, qty_ordered = 600d }
            },
            exclude_hu_codes = Array.Empty<string>()
        });

        var candidate = Assert.Single(GetCandidates(document, "line-1"));
        Assert.Equal("LEDGER_STOCK", candidate.GetProperty("source").GetString());
        Assert.True(candidate.GetProperty("ship_ready").GetBoolean());
        Assert.Equal(600, candidate.GetProperty("qty").GetDouble(), 3);
    }

    [Fact]
    public async Task ReturnsInternalFilledCandidates()
    {
        var store = CreateStore(
        [
            Source(
                "INTERNAL_FILLED",
                "HU-FILLED",
                itemId: 6,
                qty: 600,
                shipReady: false,
                sourceOrderId: 72,
                sourceOrderRef: "072",
                sourcePrdDocId: 181,
                sourcePrdRef: "PRD-2026-000172",
                note: "FILLED, PRD не закрыт")
        ]);
        await using var host = await HuReservationCandidatesHost.StartAsync(store.Object);

        using var document = await PostAsync(host.Client, new
        {
            order_id = 78L,
            lines = new[]
            {
                new { client_line_key = "line-1", order_line_id = (long?)null, item_id = 6L, qty_ordered = 600d }
            },
            exclude_hu_codes = Array.Empty<string>()
        });

        var candidate = Assert.Single(GetCandidates(document, "line-1"));
        Assert.Equal("INTERNAL_FILLED", candidate.GetProperty("source").GetString());
        Assert.False(candidate.GetProperty("ship_ready").GetBoolean());
        Assert.Equal(72, candidate.GetProperty("source_order_id").GetInt64());
        Assert.Equal("FILLED, PRD не закрыт", candidate.GetProperty("note").GetString());
    }

    [Fact]
    public async Task ExcludesPlannedPrintedCancelled()
    {
        var store = CreateStore(
        [
            Source("LEDGER_STOCK", "HU-OK", itemId: 6, qty: 100, shipReady: true)
        ]);
        await using var host = await HuReservationCandidatesHost.StartAsync(store.Object);

        using var document = await PostAsync(host.Client, new
        {
            order_id = 78L,
            lines = new[] { new { client_line_key = "line-1", order_line_id = (long?)null, item_id = 6L, qty_ordered = 100d } },
            exclude_hu_codes = new[] { "HU-PLANNED", "HU-PRINTED", "HU-CANCELLED" }
        });

        var candidate = Assert.Single(GetCandidates(document, "line-1"));
        Assert.Equal("HU-OK", candidate.GetProperty("hu_code").GetString());
    }

    [Fact]
    public async Task ExcludesReservedByOtherCustomer()
    {
        var store = CreateStore(
        [
            Source("LEDGER_STOCK", "HU-FREE", itemId: 6, qty: 100, shipReady: true)
        ]);
        await using var host = await HuReservationCandidatesHost.StartAsync(store.Object);

        using var document = await PostAsync(host.Client, new
        {
            order_id = 78L,
            lines = new[] { new { client_line_key = "line-1", order_line_id = (long?)null, item_id = 6L, qty_ordered = 500d } },
            exclude_hu_codes = Array.Empty<string>()
        });

        var candidates = GetCandidates(document, "line-1").ToArray();
        var candidate = Assert.Single(candidates);
        Assert.Equal("HU-FREE", candidate.GetProperty("hu_code").GetString());
        Assert.DoesNotContain(candidates, row => row.GetProperty("hu_code").GetString() == "HU-RESERVED");
    }

    [Fact]
    public async Task DoesNotDoubleSelectSameHuAcrossRequestLines()
    {
        var store = CreateStore(
        [
            Source("LEDGER_STOCK", "HU-SHARED", itemId: 6, qty: 600, shipReady: true)
        ]);
        await using var host = await HuReservationCandidatesHost.StartAsync(store.Object);

        using var document = await PostAsync(host.Client, new
        {
            order_id = 78L,
            lines = new[]
            {
                new { client_line_key = "line-a", order_line_id = (long?)null, item_id = 6L, qty_ordered = 400d },
                new { client_line_key = "line-b", order_line_id = (long?)null, item_id = 6L, qty_ordered = 400d }
            },
            exclude_hu_codes = Array.Empty<string>()
        });

        var autoSelectedCount = document.RootElement
            .GetProperty("lines")
            .EnumerateArray()
            .SelectMany(line => line.GetProperty("candidates").EnumerateArray())
            .Count(candidate => candidate.GetProperty("auto_selected").GetBoolean());

        Assert.Equal(1, autoSelectedCount);
    }

    [Fact]
    public async Task AutoSelectsLedgerBeforeInternalFilled()
    {
        var store = CreateStore(
        [
            Source("INTERNAL_FILLED", "HU-B", itemId: 6, qty: 600, shipReady: false, sourceOrderId: 72),
            Source("LEDGER_STOCK", "HU-A", itemId: 6, qty: 600, shipReady: true)
        ]);
        await using var host = await HuReservationCandidatesHost.StartAsync(store.Object);

        using var document = await PostAsync(host.Client, new
        {
            order_id = 78L,
            lines = new[] { new { client_line_key = "line-1", order_line_id = (long?)null, item_id = 6L, qty_ordered = 600d } },
            exclude_hu_codes = Array.Empty<string>()
        });

        var candidates = GetCandidates(document, "line-1").ToArray();
        Assert.Equal(2, candidates.Length);
        Assert.Equal("LEDGER_STOCK", candidates[0].GetProperty("source").GetString());
        Assert.True(candidates[0].GetProperty("auto_selected").GetBoolean());
        Assert.False(candidates[1].GetProperty("auto_selected").GetBoolean());
    }

    [Fact]
    public async Task MixedHuReturnsItemSpecificCandidates()
    {
        var store = CreateStore(
        [
            Source("INTERNAL_FILLED", "HU-MIXED", itemId: 6, qty: 600, shipReady: false, sourceOrderId: 72),
            Source("INTERNAL_FILLED", "HU-MIXED", itemId: 7, qty: 300, shipReady: false, sourceOrderId: 72)
        ]);
        await using var host = await HuReservationCandidatesHost.StartAsync(store.Object);

        using var document = await PostAsync(host.Client, new
        {
            order_id = 78L,
            lines = new[]
            {
                new { client_line_key = "line-a", order_line_id = (long?)null, item_id = 6L, qty_ordered = 600d },
                new { client_line_key = "line-b", order_line_id = (long?)null, item_id = 7L, qty_ordered = 300d }
            },
            exclude_hu_codes = Array.Empty<string>()
        });

        var lineA = Assert.Single(GetCandidates(document, "line-a"));
        var lineB = Assert.Single(GetCandidates(document, "line-b"));
        Assert.Equal(600, lineA.GetProperty("qty").GetDouble(), 3);
        Assert.Equal(300, lineB.GetProperty("qty").GetDouble(), 3);
        Assert.Equal("HU-MIXED", lineA.GetProperty("hu_code").GetString());
        Assert.Equal("HU-MIXED", lineB.GetProperty("hu_code").GetString());
    }

    [Fact]
    public async Task EmptyLines_ReturnsEmptyResponse()
    {
        var store = CreateStore([]);
        await using var host = await HuReservationCandidatesHost.StartAsync(store.Object);

        using var document = await PostAsync(host.Client, new
        {
            order_id = 78L,
            lines = Array.Empty<object>(),
            exclude_hu_codes = Array.Empty<string>()
        });

        Assert.Empty(document.RootElement.GetProperty("lines").EnumerateArray());
    }

    [Fact]
    public void PostgresHuReservationCandidatesReadModel_UsesBatchSqlByItemIds()
    {
        var sql = File.ReadAllText(GetHuReservationCandidateSqlPath());
        var storeSource = File.ReadAllText(GetPostgresDataStorePath());
        var methodStart = storeSource.IndexOf("GetHuReservationCandidateSources", StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        var methodSlice = storeSource[methodStart..Math.Min(methodStart + 2500, storeSource.Length)];

        Assert.Contains("HuReservationCandidateSql.SelectSources", methodSlice);
        Assert.Contains("UNNEST(@item_ids::bigint[])", sql);
        Assert.Contains("ledger_candidates", sql);
        Assert.Contains("internal_candidates", sql);
        Assert.Contains("reserved_map", sql);
        Assert.Contains("order_receipt_plan_lines", sql);
        Assert.Contains("pp.status = @filled_status", sql);
        Assert.DoesNotContain("PRINTED", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("PLANNED", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GetOrders(", methodSlice);
        Assert.DoesNotContain("GetOrderLines(", methodSlice);
        Assert.DoesNotContain("GetProductionPalletsByDoc(", methodSlice);
    }

    [Fact]
    public void HuReservationCandidatesService_InvalidItemId_ReturnsEmptyCandidates()
    {
        var store = CreateStore(
        [
            Source("LEDGER_STOCK", "HU-1", itemId: 6, qty: 100, shipReady: true)
        ]);
        var service = new HuReservationCandidatesService(store.Object);
        var result = service.Build(new HuReservationCandidatesQuery
        {
            OrderId = 78,
            Lines =
            [
                new HuReservationCandidatesLineQuery
                {
                    ClientLineKey = "missing",
                    ItemId = 999,
                    QtyOrdered = 10
                }
            ]
        });

        var line = Assert.Single(result.Lines);
        Assert.Empty(line.Candidates);
        Assert.Equal(0, line.AvailableQty, 3);
    }

    private static Mock<IDataStore> CreateStore(IReadOnlyList<HuReservationCandidateSourceRow> sources)
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        var optimized = store.As<IOptimizedHuReservationCandidatesStore>();
        optimized.Setup(data => data.GetHuReservationCandidateSources(
                It.IsAny<long?>(),
                It.IsAny<IReadOnlyCollection<long>>(),
                It.IsAny<IReadOnlyCollection<string>>()))
            .Returns<long?, IReadOnlyCollection<long>, IReadOnlyCollection<string>>((orderId, itemIds, excludeHuCodes) =>
            {
                var excluded = (excludeHuCodes ?? Array.Empty<string>())
                    .Select(code => code.Trim().ToUpperInvariant())
                    .Where(code => code.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                return sources
                    .Where(row => itemIds.Contains(row.ItemId))
                    .Where(row => !excluded.Contains(row.HuCode))
                    .ToArray();
            });
        return store;
    }

    private static HuReservationCandidateSourceRow Source(
        string source,
        string huCode,
        long itemId,
        double qty,
        bool shipReady,
        long? sourceOrderId = null,
        string? sourceOrderRef = null,
        long? sourcePrdDocId = null,
        string? sourcePrdRef = null,
        string note = "")
    {
        return new HuReservationCandidateSourceRow
        {
            Source = source,
            HuCode = huCode,
            ItemId = itemId,
            Qty = qty,
            ShipReady = shipReady,
            SourceOrderId = sourceOrderId,
            SourceOrderRef = sourceOrderRef,
            SourcePrdDocId = sourcePrdDocId,
            SourcePrdRef = sourcePrdRef,
            Note = note
        };
    }

    private static IEnumerable<JsonElement> GetCandidates(JsonDocument document, string clientLineKey)
    {
        var line = document.RootElement
            .GetProperty("lines")
            .EnumerateArray()
            .Single(element => element.GetProperty("client_line_key").GetString() == clientLineKey);
        return line.GetProperty("candidates").EnumerateArray();
    }

    private static async Task<JsonDocument> PostAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync("/api/orders/hu-reservation-candidates", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static string GetPostgresDataStorePath() => GetRepoFilePath("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");

    private static string GetHuReservationCandidateSqlPath() => GetRepoFilePath("apps", "windows", "FlowStock.Data", "HuReservationCandidateSql.cs");

    private static string GetRepoFilePath(params string[] parts)
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

    private sealed class HuReservationCandidatesHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private HuReservationCandidatesHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<HuReservationCandidatesHost> StartAsync(IDataStore store)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(OrderHuReservationCandidatesEndpoint).Assembly.FullName,
                EnvironmentName = Environments.Production
            });

            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store);

            var app = builder.Build();
            OrderHuReservationCandidatesEndpoint.Map(app);
            await app.StartAsync();

            var addresses = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>();
            var address = addresses?.Addresses.SingleOrDefault();
            if (string.IsNullOrWhiteSpace(address))
            {
                await app.StopAsync();
                await app.DisposeAsync();
                throw new InvalidOperationException("HTTP test host did not expose a listening address.");
            }

            return new HuReservationCandidatesHost(app, new HttpClient
            {
                BaseAddress = new Uri(address, UriKind.Absolute)
            });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

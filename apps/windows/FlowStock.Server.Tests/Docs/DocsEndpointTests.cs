using System.Net;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Server;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace FlowStock.Server.Tests.Docs;

public sealed class DocsEndpointTests
{
    [Fact]
    public async Task DocsList_PreservesJsonShapeAndUsesBatchPalletSummaries()
    {
        var harness = CreateHarness();
        SeedDocsAndPallets(harness);
        var ledgerCountBefore = harness.LedgerEntries.Count;
        await using var host = await DocsHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync("/api/docs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var rows = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);

        var prd = rows.Single(row => row.GetProperty("id").GetInt64() == 10);
        foreach (var propertyName in ExpectedDocProperties)
        {
            Assert.True(prd.TryGetProperty(propertyName, out _), $"Missing property '{propertyName}'.");
        }

        Assert.Equal("PRD-10", prd.GetProperty("doc_ref").GetString());
        Assert.Equal("PRODUCTION_RECEIPT", prd.GetProperty("op").GetString());
        Assert.Equal("DRAFT", prd.GetProperty("status").GetString());
        Assert.True(prd.GetProperty("production_pallet_filling_started").GetBoolean());
        Assert.True(prd.GetProperty("has_production_pallet_plan").GetBoolean());
        Assert.True(prd.GetProperty("is_palletized").GetBoolean());
        Assert.Equal(2, prd.GetProperty("planned_pallet_count").GetInt32());
        Assert.Equal(1, prd.GetProperty("filled_pallet_count").GetInt32());
        Assert.Equal(15, prd.GetProperty("planned_qty").GetDouble(), 3);
        Assert.Equal(5, prd.GetProperty("filled_qty").GetDouble(), 3);
        Assert.Equal("Наполнено 1 / 2 паллет", prd.GetProperty("pallet_filling_status").GetString());

        var inbound = rows.Single(row => row.GetProperty("id").GetInt64() == 11);
        Assert.False(inbound.GetProperty("has_production_pallet_plan").GetBoolean());
        Assert.Equal(0, inbound.GetProperty("planned_pallet_count").GetInt32());
        Assert.Equal(string.Empty, inbound.GetProperty("pallet_filling_status").GetString());

        Assert.Equal(ledgerCountBefore, harness.LedgerEntries.Count);
        harness.VerifyProductionPalletSummaryBatchPathUsed(Times.Once());
        harness.VerifyProductionPalletDetailPathNotUsed();
    }

    [Fact]
    public async Task DocsList_HandlesEmptyListWithBatchSummaryPath()
    {
        var harness = CreateHarness();
        var ledgerCountBefore = harness.LedgerEntries.Count;
        await using var host = await DocsHost.StartAsync(harness.Store);

        using var response = await host.Client.GetAsync("/api/docs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Empty(document.RootElement.EnumerateArray());
        Assert.Equal(ledgerCountBefore, harness.LedgerEntries.Count);
        harness.VerifyProductionPalletSummaryBatchPathUsed(Times.Once());
        harness.VerifyProductionPalletDetailPathNotUsed();
    }

    [Fact]
    public void BatchPalletSummaryStore_ReturnsSummariesForMultipleDocIdsAndEmptyInput()
    {
        var harness = CreateHarness();
        SeedDocsAndPallets(harness);
        var summaryStore = Assert.IsAssignableFrom<IProductionPalletSummaryBatchStore>(harness.Store);

        var empty = summaryStore.GetProductionPalletSummariesByDocIds(Array.Empty<long>());
        var summaries = summaryStore.GetProductionPalletSummariesByDocIds(new[] { 10L, 12L, 999L, 10L });

        Assert.Empty(empty);
        Assert.Equal(2, summaries.Count);
        Assert.Equal(2, summaries[10].PlannedPalletCount);
        Assert.Equal(15, summaries[10].PlannedQty, 3);
        Assert.Equal(1, summaries[10].FilledPalletCount);
        Assert.Equal(5, summaries[10].FilledQty, 3);
        Assert.Equal(1, summaries[10].RemainingPalletCount);
        Assert.Equal(10, summaries[10].RemainingQty, 3);
        Assert.Equal(1, summaries[12].PlannedPalletCount);
        Assert.Equal(1, summaries[12].RemainingPalletCount);
    }

    [Fact]
    public void DocsListEndpoint_DoesNotUsePerDocProductionPalletDetails()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Server", "DocsEndpoint.cs");

        Assert.Contains("IProductionPalletSummaryBatchStore", source, StringComparison.Ordinal);
        Assert.Contains("GetProductionPalletSummariesByDocIds(docIds)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProductionPalletsByDoc", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildProductionPalletSummary", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramDocsList_DelegatesToDedicatedEndpointBeforeDetailRoutes()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Server", "Program.cs");
        var start = source.IndexOf("DocsEndpoint.Map(app);", StringComparison.Ordinal);
        var end = source.IndexOf("app.MapGet(\"/api/docs/{docId:long}\"", start, StringComparison.Ordinal);
        var docsRouteSection = source[start..end];

        Assert.Contains("DocsEndpoint.Map(app);", docsRouteSection);
        Assert.DoesNotContain("BuildProductionPalletSummary(store, doc)", docsRouteSection, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProductionPalletsByDoc", docsRouteSection, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgresBatchPalletSummary_UsesDocIdsAndComponentLineState()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");
        var start = source.IndexOf("public IReadOnlyDictionary<long, ProductionPalletSummary> GetProductionPalletSummariesByDocIds", StringComparison.Ordinal);
        var end = source.IndexOf("public ProductionPallet? GetProductionPalletByHu", start, StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains("WHERE pp.prd_doc_id = ANY(@doc_ids)", method, StringComparison.Ordinal);
        Assert.Contains("production_pallet_lines", method, StringComparison.Ordinal);
        Assert.Contains("completed_line_count", method, StringComparison.Ordinal);
        Assert.Contains("ps.status IN (@planned_status, @printed_status)", method, StringComparison.Ordinal);
        Assert.DoesNotContain(ProductionPalletSelectSqlMarker, method, StringComparison.Ordinal);
    }

    private static CloseDocumentHarness CreateHarness()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedItem(new Item { Id = 1, Name = "Item A", BaseUom = "шт" });
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        return harness;
    }

    private static void SeedDocsAndPallets(CloseDocumentHarness harness)
    {
        harness.SeedDoc(new Doc
        {
            Id = 10,
            DocRef = "PRD-10",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            PartnerId = 100,
            PartnerName = "Client",
            PartnerCode = "C001",
            OrderId = 200,
            OrderRef = "SO-200",
            ShippingRef = "SHIP-1",
            ReasonCode = "PLAN",
            Comment = "production",
            ProductionBatchNo = "B-1",
            SourceDeviceId = "TSD-1",
            ApiDocUid = "uid-10",
            LineCount = 2
        });
        harness.SeedDoc(new Doc
        {
            Id = 11,
            DocRef = "IN-11",
            Type = DocType.Inbound,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 5, 1, 12, 5, 0, DateTimeKind.Utc)
        });
        harness.SeedDoc(new Doc
        {
            Id = 12,
            DocRef = "PRD-12",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = new DateTime(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 100,
            PrdDocId = 10,
            DocLineId = 1000,
            ItemId = 1,
            ItemName = "Item A",
            HuCode = "HU-100",
            PlannedQty = 10,
            Status = ProductionPalletStatus.Planned,
            CreatedAt = new DateTime(2026, 5, 1, 12, 1, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 101,
            PrdDocId = 10,
            DocLineId = 1001,
            ItemId = 1,
            ItemName = "Item A",
            HuCode = "HU-101",
            PlannedQty = 5,
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 1, 12, 2, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 5, 1, 12, 1, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 102,
            PrdDocId = 10,
            DocLineId = 1002,
            ItemId = 1,
            ItemName = "Item A",
            HuCode = "HU-102",
            PlannedQty = 99,
            Status = ProductionPalletStatus.Cancelled,
            CreatedAt = new DateTime(2026, 5, 1, 12, 1, 0, DateTimeKind.Utc)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 120,
            PrdDocId = 12,
            DocLineId = 1200,
            ItemId = 1,
            ItemName = "Item A",
            HuCode = "HU-120",
            PlannedQty = 3,
            Status = ProductionPalletStatus.Printed,
            CreatedAt = new DateTime(2026, 5, 1, 11, 1, 0, DateTimeKind.Utc)
        });
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

    private static readonly string[] ExpectedDocProperties =
    [
        "id",
        "doc_ref",
        "doc_uid",
        "op",
        "status",
        "created_at",
        "closed_at",
        "partner_id",
        "partner_name",
        "partner_code",
        "order_id",
        "order_ref",
        "shipping_ref",
        "reason_code",
        "comment",
        "production_batch_no",
        "source_device_id",
        "line_count",
        "production_pallet_filling_started",
        "has_production_pallet_plan",
        "is_palletized",
        "planned_pallet_count",
        "filled_pallet_count",
        "planned_qty",
        "filled_qty",
        "pallet_filling_status"
    ];

    private const string ProductionPalletSelectSqlMarker = "{ProductionPalletSelectSql}";

    private sealed class DocsHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private DocsHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<DocsHost> StartAsync(IDataStore store)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(DocsEndpoint).Assembly.FullName,
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(store);
            var app = builder.Build();
            DocsEndpoint.Map(app);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single();
            return new DocsHost(app, new HttpClient { BaseAddress = new Uri(address!, UriKind.Absolute) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

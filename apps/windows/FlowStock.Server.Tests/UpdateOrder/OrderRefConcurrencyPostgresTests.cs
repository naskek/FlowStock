using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using FlowStock.Server.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FlowStock.Server.Tests.UpdateOrder;

[Collection(PostgresLocationIntegrationTestCollection.Name)]
public sealed class OrderRefConcurrencyPostgresTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData("put", "put", false, false)]
    [InlineData("put", "create", false, false)]
    [InlineData("create", "put", false, false)]
    [InlineData("confirm", "create", false, false)]
    [InlineData("create", "confirm", false, false)]
    [InlineData("put", "put", true, false)]
    [InlineData("create", "create", true, false)]
    [InlineData("need", "create", true, false)]
    [InlineData("put", "put", true, true)]
    [InlineData("confirm", "create", true, false)]
    public async Task Writers_serialize_before_business_locks_and_recheck_after_commit(
        string firstWriter, string secondWriter, bool fallback, bool differentConflicts)
    {
        await using var fixture = new Fixture();
        await using var firstHost = await Host.Start(fixture.ConnectionString, fixture.Token + "-first");
        await using var secondHost = await Host.Start(fixture.ConnectionString, fixture.Token + "-second");
        var requested = fallback ? fixture.Token + "-taken" : fixture.Token + "-Shared";
        if (fallback) fixture.SeedOrder(requested, OrderStatus.Cancelled);
        if (differentConflicts) fixture.SeedOrder(requested + "-other", OrderStatus.Merged);
        var next = fixture.NextNumber();
        var before = fixture.PhysicalFingerprint();

        // The first writer owns the namespace but cannot finish its business mutation.
        // Observe actual PostgreSQL waits rather than relying on task scheduling/delays.
        await using var blocker = new NpgsqlConnection(fixture.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = firstWriter == "put"
                ? "SELECT id FROM orders WHERE id = @id FOR UPDATE"
                : "SELECT id FROM items WHERE id = @id FOR UPDATE";
            command.Parameters.AddWithValue("id", firstWriter == "put" ? fixture.FirstId : fixture.ItemId);
            await command.ExecuteScalarAsync();
        }

        var first = Send(firstHost.Client, fixture, firstWriter, fixture.FirstId, requested, fallback);
        Task<JsonElement>? second = null;
        try
        {
            await fixture.WaitForLock(fixture.Token + "-first", advisory: false);
            Assert.False(first.IsCompleted);
            second = Send(secondHost.Client, fixture, secondWriter, fixture.SecondId,
                differentConflicts ? requested + "-other" : requested.ToLowerInvariant(), fallback);
            await fixture.WaitForLock(fixture.Token + "-second", advisory: true);
            Assert.False(second.IsCompleted);
            Assert.Equal(0L, fixture.Scalar("""
SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid
WHERE a.application_name=@app AND l.granted AND l.locktype='relation'
  AND l.mode='RowShareLock' AND l.relation IN ('orders'::regclass, 'items'::regclass, 'order_requests'::regclass)
""", ("app", fixture.Token + "-second")));
        }
        finally
        {
            await transaction.RollbackAsync();
            // Drain HTTP work before disposing fixtures, including assertion failures.
            await first.WaitAsync(Timeout);
            if (second != null) await second.WaitAsync(Timeout);
        }

        var firstResult = await first;
        var secondResult = await second!;
        var firstId = ResolveId(firstResult, fixture, firstWriter);
        var secondId = ResolveId(secondResult, fixture, secondWriter);
        var firstRef = fixture.Store.GetOrder(firstId)!.OrderRef;
        var secondRef = fixture.Store.GetOrder(secondId)!.OrderRef;
        Assert.False(string.Equals(firstRef, secondRef, StringComparison.OrdinalIgnoreCase));
        if (fallback)
        {
            Assert.Equal(next.ToString("D3", CultureInfo.InvariantCulture), firstRef);
            Assert.Equal((next + 1).ToString("D3", CultureInfo.InvariantCulture), secondRef);
        }
        else
        {
            Assert.Equal(requested, firstRef);
            Assert.Equal(next.ToString("D3", CultureInfo.InvariantCulture), secondRef);
        }
        foreach (var (writer, result, isSecond) in new[] { (firstWriter, firstResult, false), (secondWriter, secondResult, true) })
            if (writer is "put" or "create")
                Assert.Equal(fallback && writer == "create" ? false : isSecond || fallback,
                    result.GetProperty("order_ref_changed").GetBoolean());
        Assert.Equal(before, fixture.PhysicalFingerprint());
        Assert.All(new[] { firstId, secondId }, id => Assert.Single(fixture.Store.GetOrderLines(id)));
        if (firstWriter == "need") Assert.Equal(OrderStatus.Draft, fixture.Store.GetOrder(firstId)!.Status);
    }

    [Fact]
    public void Allocator_requires_transaction_and_releases_lock_on_rollback()
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidOperationException>(() => fixture.Store.LockOrderReferences());
        var requested = fixture.Token + "-rollback";
        Assert.Throws<InvalidOperationException>(() => fixture.Store.ExecuteInTransaction(store =>
        {
            OrderService.CreateOrderInTransaction(store, requested, null, null, fixture.Token,
                [new OrderLineView { ItemId = fixture.ItemId, QtyOrdered = 1 }], OrderType.Internal);
            throw new InvalidOperationException("rollback");
        }));
        var id = new OrderService(fixture.Store).CreateOrder(requested, null, null, fixture.Token,
            [new OrderLineView { ItemId = fixture.ItemId, QtyOrdered = 1 }], OrderType.Internal);
        Assert.Equal(requested, fixture.Store.GetOrder(id)!.OrderRef);
    }

    [Theory]
    [InlineData("i", "İ")]
    [InlineData("Σ", "ς")]
    [InlineData("Mixed", "mixed")]
    public void Canonical_update_keeps_ordinal_ignore_case_semantics(string existingSuffix, string requestedSuffix)
    {
        using var fixture = new Fixture();
        var existingRef = fixture.Token + existingSuffix;
        var requestedRef = fixture.Token + requestedSuffix;
        fixture.SeedOrder(existingRef, OrderStatus.Merged);
        new OrderService(fixture.Store).UpdateOrder(fixture.FirstId, requestedRef, null, null, fixture.Token,
            [new OrderLineView { ItemId = fixture.ItemId, QtyOrdered = 10 }], OrderType.Internal);
        var actual = fixture.Store.GetOrder(fixture.FirstId)!.OrderRef;
        Assert.Equal(string.Equals(existingRef, requestedRef, StringComparison.OrdinalIgnoreCase), actual != requestedRef);
    }

    [Fact]
    public async Task Put_without_reference_keeps_historical_duplicates_unchanged()
    {
        await using var fixture = new Fixture();
        var original = fixture.Store.GetOrder(fixture.FirstId)!.OrderRef;
        var duplicateId = fixture.SeedOrder(original);
        await using var host = await Host.Start(fixture.ConnectionString, fixture.Token);
        using var response = await host.Client.PutAsJsonAsync($"/api/orders/{fixture.FirstId}", new
        {
            type = "INTERNAL", comment = fixture.Token,
            lines = new[] { new { item_id = fixture.ItemId, qty_ordered = 10d } }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("order_ref_changed").GetBoolean());
        Assert.Equal(original, fixture.Store.GetOrder(fixture.FirstId)!.OrderRef);
        Assert.Equal(original, fixture.Store.GetOrder(duplicateId)!.OrderRef);
    }

    [Fact]
    public void Exhausted_numeric_range_rolls_back_instead_of_wrapping()
    {
        using var fixture = new Fixture();
        fixture.SeedOrder(long.MaxValue.ToString(CultureInfo.InvariantCulture), OrderStatus.Cancelled);
        Assert.Throws<OverflowException>(() => fixture.Store.ExecuteInTransaction(store =>
            OrderReferenceAllocator.Resolve(store, null)));
    }

    private static long ResolveId(JsonElement result, Fixture fixture, string writer) => writer switch
    {
        "confirm" => result.GetProperty("applied_order_id").GetInt64(),
        "need" => fixture.Scalar("SELECT id FROM orders WHERE comment='Автосформировано из потребности производства.' AND id IN (SELECT order_id FROM order_lines WHERE item_id=@item) ORDER BY id DESC LIMIT 1", ("item", fixture.ItemId)),
        _ => result.GetProperty("order_id").GetInt64()
    };

    private static async Task<JsonElement> Send(HttpClient client, Fixture fixture, string writer,
        long orderId, string requested, bool fallback)
    {
        var payload = new
        {
            order_ref = fallback && writer == "create" ? null : requested,
            type = "INTERNAL", comment = fixture.Token,
            lines = new[] { new { item_id = fixture.ItemId, qty_ordered = 10d, production_purpose = "INTERNAL_STOCK" } }
        };
        HttpResponseMessage response;
        if (writer == "put") response = await client.PutAsJsonAsync($"/api/orders/{orderId}", payload);
        else if (writer == "create") response = await client.PostAsJsonAsync("/api/orders", payload);
        else if (writer == "need") response = await client.PostAsJsonAsync("/api/production-needs/create-orders",
            new { rows = new[] { new { item_id = fixture.ItemId, qty_ordered = 1d } } });
        else
        {
            var requestId = fixture.Store.AddOrderRequest(new OrderRequest
            {
                RequestType = OrderRequestType.CreateOrder, Status = OrderRequestStatus.Pending,
                CreatedAt = DateTime.Now,
                PayloadJson = JsonSerializer.Serialize(new { order_ref = requested, order_type = "INTERNAL", comment = fixture.Token, payload.lines })
            });
            response = await client.PostAsync($"/api/orders/requests/{requestId}/confirm", null);
        }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{writer}: {response.StatusCode}: {body}");
            var result = JsonSerializer.Deserialize<JsonElement>(body);
            Assert.True(result.GetProperty("ok").GetBoolean());
            return result;
        }
    }

    private sealed class Host(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public static async Task<Host> Start(string connection, string name)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IDataStore>(new PostgresDataStore(
                new NpgsqlConnectionStringBuilder(connection) { ApplicationName = name }.ConnectionString));
            builder.Services.AddSingleton<PartnerRoleResolver>();
            builder.Services.AddSingleton(new WpfMachineAuthorization(CloseDocumentHttpHost.WpfAdminApiKey));
            builder.Services.AddSingleton<IPcWebSessionResolver>(new TestPcWebSessionResolver(null));
            var app = builder.Build();
            OrderUpdateEndpoint.Map(app);
            OrderCreateEndpoint.Map(app);
            OrderRequestManagementEndpoint.Map(app);
            ProductionNeedCreateOrdersEndpoint.Map(app);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var client = new HttpClient { BaseAddress = new Uri(address), Timeout = Timeout };
            client.DefaultRequestHeaders.Add(WpfMachineAuthorization.KeyHeader, CloseDocumentHttpHost.WpfAdminApiKey);
            return new Host(app, client);
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }

    private sealed class Fixture : IDisposable, IAsyncDisposable
    {
        public string ConnectionString { get; } = Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION")
            ?? throw new InvalidOperationException("FLOWSTOCK_POSTGRES_TEST_CONNECTION is required.");
        public string Token { get; } = "REF-" + Guid.NewGuid().ToString("N")[..16];
        public PostgresDataStore Store { get; }
        public long ItemId { get; }
        public long FirstId { get; }
        public long SecondId { get; }
        private readonly long _typeId;
        private readonly long _locationId;
        private readonly long _stockDocId;
        public Fixture()
        {
            Store = new PostgresDataStore(ConnectionString);
            _typeId = Store.AddItemType(new ItemType { Name = Token, EnableMinStockControl = true });
            ItemId = Store.AddItem(new Item { Name = Token, BaseUom = "шт", ItemTypeId = _typeId, MinStockQty = 100, MaxQtyPerHu = 10 });
            FirstId = SeedOrder(Token + "-one");
            SecondId = SeedOrder(Token + "-two");
            _locationId = Store.AddLocation(new Location { Code = Token, Name = Token, AutoHuDistributionEnabled = true });
            _stockDocId = Store.AddDoc(new Doc { DocRef = Token + "-stock", Type = DocType.InventoryCorrection,
                Status = DocStatus.Closed, CreatedAt = DateTime.Now, ClosedAt = DateTime.Now });
            Store.AddLedgerEntry(new LedgerEntry { DocId = _stockDocId, ItemId = ItemId, LocationId = _locationId,
                HuCode = Token + "-stock-hu", QtyDelta = 1, Timestamp = DateTime.Now });
            var guardId = SeedOrder(Token + "-guard");
            new ProductionPalletService(Store).PlanOrder(guardId);
        }
        public long SeedOrder(string reference, OrderStatus status = OrderStatus.InProgress)
        {
            var id = Store.AddOrder(new Order { OrderRef = reference, Type = OrderType.Internal, Status = status, Comment = Token, CreatedAt = DateTime.Now });
            Store.AddOrderLine(new OrderLine { OrderId = id, ItemId = ItemId, QtyOrdered = 10, ProductionPurpose = ProductionLinePurpose.InternalStock });
            return id;
        }
        public long NextNumber() => Store.GetAllOrderRefsForNextNumber().Select(s => s.Trim())
            .Where(s => s.Length > 0 && s.All(char.IsDigit))
            .Select(s => long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .DefaultIfEmpty().Max() + 1;
        public long Scalar(string sql, params (string, object)[] parameters) => Convert.ToInt64(Query(sql, parameters));
        private object Query(string sql, params (string, object)[] parameters)
        {
            using var connection = new NpgsqlConnection(ConnectionString); connection.Open();
            using var command = new NpgsqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return command.ExecuteScalar()!;
        }
        public string PhysicalFingerprint() => string.Join("/", new[] { "docs", "doc_lines", "production_pallets", "production_pallet_lines", "ledger", "order_receipt_plan_lines" }
            .Select(table => Query($"SELECT md5(coalesce(string_agg(row_to_json(x)::text, '' ORDER BY row_to_json(x)::text), '')) FROM (SELECT *, xmin::text AS row_version FROM {table}) x")));
        public async Task WaitForLock(string app, bool advisory)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < Timeout)
            {
                if (Scalar("""
SELECT count(*) FROM pg_stat_activity a WHERE application_name=@app
AND wait_event_type='Lock' AND (wait_event='advisory')=@advisory
AND EXISTS (SELECT 1 FROM pg_locks l WHERE l.pid=a.pid AND l.locktype='advisory'
AND l.classid=73422 AND l.objid=1 AND l.granted=@granted)
""", ("app", app), ("advisory", advisory), ("granted", !advisory)) > 0) return;
                await Task.Delay(20);
            }
            Assert.Fail($"Session {app} did not reach the expected PostgreSQL lock wait.");
        }
        public void Dispose()
        {
            Query("""
DELETE FROM production_pallet_lines WHERE item_id=@item;
DELETE FROM production_pallets WHERE item_id=@item;
DELETE FROM doc_lines WHERE item_id=@item;
DELETE FROM order_requests WHERE payload_json LIKE @token;
DELETE FROM ledger WHERE item_id=@item;
DELETE FROM docs WHERE id=@stock;
DELETE FROM docs WHERE order_id IN (SELECT order_id FROM order_lines WHERE item_id=@item);
WITH removed AS (DELETE FROM order_lines WHERE item_id=@item RETURNING order_id)
DELETE FROM orders WHERE id IN (SELECT order_id FROM removed);
DELETE FROM items WHERE id=@item;
DELETE FROM item_types WHERE id=@type;
DELETE FROM locations WHERE id=@location;
SELECT 1;
""", ("item", ItemId), ("type", _typeId), ("stock", _stockDocId), ("location", _locationId), ("token", "%" + Token + "%"));
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

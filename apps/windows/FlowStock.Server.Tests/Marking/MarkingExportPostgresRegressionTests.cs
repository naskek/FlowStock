using System.Globalization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using FlowStock.Data;
using FlowStock.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FlowStock.Server.Tests.Marking;

[Collection("Postgres marking integration")]
public sealed class MarkingExportPostgresRegressionTests
{
    [Fact]
    public async Task AddMarkingCodes_BulkRoundTrips6000RowsAndPreservesDuplicateFailure()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var store = new PostgresDataStore(connectionString);
        var fixtureId = Guid.NewGuid();
        var markingOrderId = Guid.NewGuid();
        var importId = Guid.NewGuid();
        var generatedAt = new DateTime(2026, 7, 27, 10, 11, 12, DateTimeKind.Utc);
        try
        {
            store.AddMarkingOrder(CreateMarkingOrder(markingOrderId, fixtureId, requestedQuantity: 6000));
            store.AddMarkingCodeImport(CreateImport(importId, markingOrderId, fixtureId, 6000, generatedAt));
            var codes = Enumerable.Range(1, 6000)
                .Select(index => new MarkingCode
                {
                    Id = Guid.NewGuid(),
                    Code = $"TEST-BULK-{fixtureId:N}-{index:000000}",
                    CodeHash = $"HASH-{fixtureId:N}-{index:000000}",
                    Gtin = index == 1 ? null : "04601234567890",
                    MarkingOrderId = markingOrderId,
                    ImportId = importId,
                    Status = MarkingCodeStatus.Reserved,
                    Origin = index == 1 ? string.Empty : MarkingCodeOrigin.LegacySynthetic,
                    SourceRowNumber = index == 1 ? null : index,
                    PrintedAt = index == 2 ? generatedAt.AddMinutes(1) : null,
                    AppliedAt = index == 2 ? generatedAt.AddMinutes(2) : null,
                    ReportedAt = index == 2 ? generatedAt.AddMinutes(3) : null,
                    IntroducedAt = index == 2 ? generatedAt.AddMinutes(4) : null,
                    CreatedAt = generatedAt,
                    UpdatedAt = generatedAt.AddMinutes(5)
                })
                .ToArray();

            store.AddMarkingCodes(codes);
            store.AddMarkingCodes(Array.Empty<MarkingCode>());

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            Assert.Equal(
                6000,
                await ExecuteScalarIntAsync(
                    connection,
                    "SELECT COUNT(*) FROM marking_code WHERE marking_order_id = @id",
                    ("id", markingOrderId)));

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
SELECT gtin, origin, source_row_number, printed_at, applied_at, reported_at, introduced_at, created_at, updated_at
FROM marking_code
WHERE code = @code;
""";
                command.Parameters.AddWithValue("@code", $"TEST-BULK-{fixtureId:N}-000001");
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.IsDBNull(0));
                Assert.Equal(MarkingCodeOrigin.HistoricalUnknown, reader.GetString(1));
                Assert.True(reader.IsDBNull(2));
                Assert.True(reader.IsDBNull(3));
                Assert.True(reader.IsDBNull(4));
                Assert.True(reader.IsDBNull(5));
                Assert.True(reader.IsDBNull(6));
                Assert.Equal("2026-07-27T10:11:12", reader.GetString(7));
                Assert.Equal("2026-07-27T10:16:12", reader.GetString(8));
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
SELECT gtin, origin, source_row_number, printed_at, applied_at, reported_at, introduced_at, created_at, updated_at
FROM marking_code
WHERE code = @code;
""";
                command.Parameters.AddWithValue("@code", $"TEST-BULK-{fixtureId:N}-000002");
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("04601234567890", reader.GetString(0));
                Assert.Equal(MarkingCodeOrigin.LegacySynthetic, reader.GetString(1));
                Assert.Equal(2, reader.GetInt32(2));
                Assert.Equal("2026-07-27T10:12:12", reader.GetString(3));
                Assert.Equal("2026-07-27T10:13:12", reader.GetString(4));
                Assert.Equal("2026-07-27T10:14:12", reader.GetString(5));
                Assert.Equal("2026-07-27T10:15:12", reader.GetString(6));
                Assert.Equal("2026-07-27T10:11:12", reader.GetString(7));
                Assert.Equal("2026-07-27T10:16:12", reader.GetString(8));
            }

            var duplicate = new MarkingCode
            {
                Id = Guid.NewGuid(),
                Code = codes[0].Code,
                CodeHash = $"DUPLICATE-{fixtureId:N}",
                MarkingOrderId = markingOrderId,
                ImportId = importId,
                Status = MarkingCodeStatus.Reserved,
                Origin = MarkingCodeOrigin.LegacySynthetic,
                CreatedAt = generatedAt,
                UpdatedAt = generatedAt
            };
            Assert.Throws<PostgresException>(() => store.AddMarkingCodes(new[] { duplicate }));
            Assert.Equal(
                6000,
                await ExecuteScalarIntAsync(
                    connection,
                    "SELECT COUNT(*) FROM marking_code WHERE marking_order_id = @id",
                    ("id", markingOrderId)));
        }
        finally
        {
            await DeleteMarkingFixtureAsync(connectionString, markingOrderId);
        }
    }

    [Fact]
    public async Task ScopedNestedTransaction_DoesNotCommitIndependently()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var store = new PostgresDataStore(connectionString);
        var markingOrderId = Guid.NewGuid();
        var fixtureId = Guid.NewGuid();

        try
        {
            Assert.Throws<RollbackRequestedException>(() =>
                store.ExecuteInTransaction(scoped =>
                {
                    scoped.ExecuteInTransaction(inner =>
                        inner.AddMarkingOrder(CreateMarkingOrder(markingOrderId, fixtureId, requestedQuantity: 1)));
                    throw new RollbackRequestedException();
                }));

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            Assert.Equal(
                0,
                await ExecuteScalarIntAsync(
                    connection,
                    "SELECT COUNT(*) FROM marking_order WHERE id = @id",
                    ("id", markingOrderId)));
        }
        finally
        {
            await DeleteMarkingFixtureAsync(connectionString, markingOrderId);
        }
    }

    [Fact]
    public async Task OrderExport_FaultDuringCodeInsert_RollsBackEntireTransaction()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await OrderFixture.CreateAsync(connectionString, quantity: 7);
        var suffix = Guid.NewGuid().ToString("N");
        var triggerName = $"trg_marking_fault_{suffix}";
        var functionName = $"fn_marking_fault_{suffix}";
        await fixture.CreateFaultTriggerAsync(triggerName, functionName);
        try
        {
            var before = await fixture.ReadSnapshotAsync();
            var store = new PostgresDataStore(connectionString);

            Assert.Throws<PostgresException>(() =>
                new OrderMarkingExportService(store).Export(
                    fixture.OrderId,
                    new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc)));

            var after = await fixture.ReadSnapshotAsync();
            Assert.Equal(before, after);
            Assert.Equal(0, after.MarkingOrders);
            Assert.Equal(0, after.Imports);
            Assert.Equal(0, after.Codes);
            Assert.Equal("NOT_REQUIRED", after.MarkingStatus);
            Assert.Null(after.MarkingPrintedAt);
            Assert.Null(after.MarkingExcelGeneratedAt);
            Assert.Equal(0, after.Ledger);
            Assert.Equal(0, after.Docs);
            Assert.Equal(0, after.DocLines);
        }
        finally
        {
            await fixture.DropFaultTriggerAsync(triggerName, functionName);
        }
    }

    [Fact]
    public async Task ConcurrentHttpExports_WaitForOrderLockAndReturnIdempotentResults()
    {
        var baseConnectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await OrderFixture.CreateAsync(baseConnectionString, quantity: 6000);
        var applicationName = $"marking-export-{Guid.NewGuid():N}";
        var hostConnectionString = WithApplicationName(baseConnectionString, applicationName);
        await using var host = await MarkingApiHost.StartAsync(hostConnectionString);
        await using var gate = new NpgsqlConnection(WithApplicationName(baseConnectionString, $"gate-{applicationName}"));
        await gate.OpenAsync();
        await using var gateTransaction = await gate.BeginTransactionAsync();
        await using (var gateCommand = gate.CreateCommand())
        {
            gateCommand.Transaction = gateTransaction;
            gateCommand.CommandText = "SELECT id FROM orders WHERE id = @id FOR UPDATE";
            gateCommand.Parameters.AddWithValue("@id", fixture.OrderId);
            Assert.Equal(fixture.OrderId, Convert.ToInt64(await gateCommand.ExecuteScalarAsync()));
        }

        var firstTask = host.Client.PostAsync($"/api/orders/{fixture.OrderId}/marking/export", content: null);
        var secondTask = host.Client.PostAsync($"/api/orders/{fixture.OrderId}/marking/export", content: null);
        await WaitUntilSessionsWaitForLock(baseConnectionString, applicationName, expectedCount: 2);
        await gateTransaction.CommitAsync();

        using var first = await firstTask;
        using var second = await secondTask;
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());

        var created = new[]
        {
            ReadDoubleHeader(first, "X-FlowStock-Marking-Created-Qty"),
            ReadDoubleHeader(second, "X-FlowStock-Marking-Created-Qty")
        };
        var reused = new[]
        {
            ReadDoubleHeader(first, "X-FlowStock-Marking-Reused-Qty"),
            ReadDoubleHeader(second, "X-FlowStock-Marking-Reused-Qty")
        };
        Assert.Equal(new[] { 0d, 6000d }, created.Order().ToArray());
        Assert.Equal(new[] { 0d, 6000d }, reused.Order().ToArray());

        var snapshot = await fixture.ReadSnapshotAsync();
        Assert.Equal(1, snapshot.MarkingOrders);
        Assert.Equal(1, snapshot.Imports);
        Assert.Equal(6000, snapshot.Codes);
        Assert.Equal(6000, await fixture.CountDistinctCodesAsync());
        Assert.Equal(0, snapshot.Ledger);
        Assert.Equal(0, snapshot.Docs);
        Assert.Equal(0, snapshot.DocLines);
    }

    private static MarkingOrder CreateMarkingOrder(
        Guid markingOrderId,
        Guid fixtureId,
        int requestedQuantity)
    {
        var now = new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);
        return new MarkingOrder
        {
            Id = markingOrderId,
            RequestedQuantity = requestedQuantity,
            RequestNumber = $"TEST-{fixtureId:N}",
            Status = MarkingOrderStatus.WaitingForCodes,
            SourceType = "POSTGRES_TEST",
            RequestedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static MarkingCodeImport CreateImport(
        Guid importId,
        Guid markingOrderId,
        Guid fixtureId,
        int quantity,
        DateTime generatedAt)
    {
        return new MarkingCodeImport
        {
            Id = importId,
            OriginalFilename = $"TEST-{fixtureId:N}.xlsx",
            StoragePath = "<postgres-test>",
            FileHash = $"HASH-{fixtureId:N}",
            SourceType = "postgres-test",
            DetectedQuantity = quantity,
            MatchedMarkingOrderId = markingOrderId,
            Status = MarkingCodeImportStatus.Bound,
            ImportedRows = quantity,
            ValidCodeRows = quantity,
            CreatedAt = generatedAt,
            ProcessedAt = generatedAt
        };
    }

    private static async Task DeleteMarkingFixtureAsync(string connectionString, Guid markingOrderId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
DELETE FROM marking_code WHERE marking_order_id = @id;
DELETE FROM marking_code_import WHERE matched_marking_order_id = @id;
DELETE FROM marking_order WHERE id = @id;
""";
        command.Parameters.AddWithValue("@id", markingOrderId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ExecuteScalarIntAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue($"@{parameter.Name}", parameter.Value);
        }

        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static double ReadDoubleHeader(HttpResponseMessage response, string name)
    {
        Assert.True(response.Headers.TryGetValues(name, out var values));
        return double.Parse(values.Single(), CultureInfo.InvariantCulture);
    }

    private static async Task WaitUntilSessionsWaitForLock(
        string connectionString,
        string applicationName,
        int expectedCount)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT COUNT(*)
FROM pg_stat_activity
WHERE application_name = @application_name
  AND wait_event_type = 'Lock';
""";
            command.Parameters.AddWithValue("@application_name", applicationName);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) >= expectedCount)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Не дождались {expectedCount} PostgreSQL-сессий marking export в ожидании order lock.");
    }

    private static string WithApplicationName(string connectionString, string applicationName)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = applicationName,
            Pooling = false
        };
        return builder.ConnectionString;
    }

    private static string ResolveRequiredPostgresTestConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "PostgreSQL test connection is required. Set FLOWSTOCK_POSTGRES_TEST_CONNECTION.");
        }

        return connectionString.Trim();
    }

    private sealed class RollbackRequestedException : Exception;

    private sealed class MarkingApiHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private MarkingApiHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<MarkingApiHost> StartAsync(string connectionString)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<IDataStore>(new PostgresDataStore(connectionString));
            var app = builder.Build();
            OrderMarkingExportEndpoint.Map(app);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses
                .Single();
            return new MarkingApiHost(app, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class OrderFixture : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly long _itemTypeId;

        private OrderFixture(
            string connectionString,
            long itemTypeId,
            long itemId,
            long orderId,
            string gtin)
        {
            _connectionString = connectionString;
            _itemTypeId = itemTypeId;
            ItemId = itemId;
            OrderId = orderId;
            Gtin = gtin;
        }

        public long ItemId { get; }
        public long OrderId { get; }
        public string Gtin { get; }

        public static async Task<OrderFixture> CreateAsync(string connectionString, int quantity)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var gtin = $"046{new string(suffix.Where(char.IsDigit).DefaultIfEmpty('7').ToArray())}00000000000000"[..14];
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            var itemTypeId = await InsertReturningIdAsync(
                connection,
                """
INSERT INTO item_types(name, code, enable_marking)
VALUES (@name, @code, TRUE)
RETURNING id;
""",
                ("name", $"Marking PostgreSQL test {suffix}"),
                ("code", $"MARKING_TEST_{suffix}"));
            var itemId = await InsertReturningIdAsync(
                connection,
                """
INSERT INTO items(name, barcode, gtin, base_uom, item_type_id)
VALUES (@name, @barcode, @gtin, 'шт', @item_type_id)
RETURNING id;
""",
                ("name", $"Marking item {suffix}"),
                ("barcode", $"MARKING-{suffix}"),
                ("gtin", gtin),
                ("item_type_id", itemTypeId));
            var orderId = await InsertReturningIdAsync(
                connection,
                """
INSERT INTO orders(order_ref, order_type, status, created_at, marking_status)
VALUES (@order_ref, 'INTERNAL', 'DRAFT', @created_at, 'NOT_REQUIRED')
RETURNING id;
""",
                ("order_ref", $"MARKING-{suffix}"),
                ("created_at", "2026-07-27T10:00:00"));
            await using (var line = connection.CreateCommand())
            {
                line.CommandText = """
INSERT INTO order_lines(order_id, item_id, qty_ordered)
VALUES (@order_id, @item_id, @qty);
""";
                line.Parameters.AddWithValue("@order_id", orderId);
                line.Parameters.AddWithValue("@item_id", itemId);
                line.Parameters.AddWithValue("@qty", (double)quantity);
                await line.ExecuteNonQueryAsync();
            }

            return new OrderFixture(connectionString, itemTypeId, itemId, orderId, gtin);
        }

        public async Task CreateFaultTriggerAsync(string triggerName, string functionName)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
CREATE FUNCTION {functionName}() RETURNS trigger AS $$
BEGIN
    IF NEW.gtin = '{Gtin}' THEN
        RAISE EXCEPTION 'injected marking_code failure';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER {triggerName}
BEFORE INSERT ON marking_code
FOR EACH ROW EXECUTE FUNCTION {functionName}();
""";
            await command.ExecuteNonQueryAsync();
        }

        public async Task DropFaultTriggerAsync(string triggerName, string functionName)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
DROP TRIGGER IF EXISTS {triggerName} ON marking_code;
DROP FUNCTION IF EXISTS {functionName}();
""";
            await command.ExecuteNonQueryAsync();
        }

        public async Task<FixtureSnapshot> ReadSnapshotAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT
    (SELECT COUNT(*) FROM marking_order WHERE order_id = @order_id),
    (SELECT COUNT(*) FROM marking_code_import mci
        JOIN marking_order mo ON mo.id = mci.matched_marking_order_id
        WHERE mo.order_id = @order_id),
    (SELECT COUNT(*) FROM marking_code mc
        JOIN marking_order mo ON mo.id = mc.marking_order_id
        WHERE mo.order_id = @order_id),
    (SELECT marking_status FROM orders WHERE id = @order_id),
    (SELECT marking_printed_at FROM orders WHERE id = @order_id),
    (SELECT marking_excel_generated_at FROM orders WHERE id = @order_id),
    (SELECT COUNT(*) FROM ledger WHERE item_id = @item_id),
    (SELECT COUNT(*) FROM docs WHERE order_id = @order_id),
    (SELECT COUNT(*) FROM doc_lines WHERE item_id = @item_id);
""";
            command.Parameters.AddWithValue("@order_id", OrderId);
            command.Parameters.AddWithValue("@item_id", ItemId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return new FixtureSnapshot(
                Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(2), CultureInfo.InvariantCulture),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                Convert.ToInt32(reader.GetInt64(6), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(7), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(8), CultureInfo.InvariantCulture));
        }

        public async Task<int> CountDistinctCodesAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            return await ExecuteScalarIntAsync(
                connection,
                """
SELECT COUNT(DISTINCT mc.code)
FROM marking_code mc
JOIN marking_order mo ON mo.id = mc.marking_order_id
WHERE mo.order_id = @order_id
""",
                ("order_id", OrderId));
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
DELETE FROM marking_code
WHERE marking_order_id IN (SELECT id FROM marking_order WHERE order_id = @order_id);
DELETE FROM marking_code_import
WHERE matched_marking_order_id IN (SELECT id FROM marking_order WHERE order_id = @order_id);
DELETE FROM marking_order WHERE order_id = @order_id;
DELETE FROM order_lines WHERE order_id = @order_id;
DELETE FROM orders WHERE id = @order_id;
DELETE FROM items WHERE id = @item_id;
DELETE FROM item_types WHERE id = @item_type_id;
""";
            command.Parameters.AddWithValue("@order_id", OrderId);
            command.Parameters.AddWithValue("@item_id", ItemId);
            command.Parameters.AddWithValue("@item_type_id", _itemTypeId);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task<long> InsertReturningIdAsync(
            NpgsqlConnection connection,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue($"@{parameter.Name}", parameter.Value);
            }

            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
    }

    private sealed record FixtureSnapshot(
        int MarkingOrders,
        int Imports,
        int Codes,
        string MarkingStatus,
        string? MarkingPrintedAt,
        string? MarkingExcelGeneratedAt,
        int Ledger,
        int Docs,
        int DocLines);
}

[CollectionDefinition("Postgres marking integration", DisableParallelization = true)]
public sealed class PostgresMarkingIntegrationCollection;

using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using FlowStock.Core.Services.Marking;
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
public sealed class MarkingExportPostgresRegressionTests : IDisposable
{
    private readonly string? _cutoverConnectionString;
    private readonly string? _previousCutoverState;

    public MarkingExportPostgresRegressionTests()
    {
        var connectionString = Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            _cutoverConnectionString = connectionString;
            _previousCutoverState = ReadCutoverStateAsync(connectionString)
                .GetAwaiter()
                .GetResult();
            SetCutoverStateAsync(connectionString, MarkingCutoverState.Enforced)
                .GetAwaiter()
                .GetResult();
        }
    }

    public void Dispose()
    {
        if (_cutoverConnectionString == null || _previousCutoverState == null)
        {
            return;
        }

        SetCutoverStateAsync(_cutoverConnectionString, _previousCutoverState)
            .GetAwaiter()
            .GetResult();
    }

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
    public async Task OrderExport_FaultDuringScopeInsert_RollsBackEntireTransaction()
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

            var service = new OrderMarkingExportService(store);
            var snapshotHash = service.Preview(fixture.OrderId).SnapshotHash;
            Assert.Throws<PostgresException>(() =>
                service.Export(
                    fixture.OrderId,
                    new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc),
                    snapshotHash));

            var after = await fixture.ReadSnapshotAsync();
            Assert.Equal(before, after);
            Assert.Equal(0, after.MarkingOrders);
            Assert.Equal(0, after.Imports);
            Assert.Equal(0, after.Codes);
            Assert.Equal("NOT_REQUIRED", after.MarkingStatus);
            Assert.Null(after.MarkingPrintedAt);
            Assert.Null(after.MarkingExcelGeneratedAt);
            Assert.Equal(0, after.Ledger);
            Assert.Equal(1, after.Docs);
            Assert.Equal(1, after.DocLines);
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

        var previewPayload = await host.Client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/api/orders/{fixture.OrderId}/marking/preview");
        var snapshotHash = previewPayload.GetProperty("snapshot_hash").GetString();
        var firstTask = host.Client.PostAsJsonAsync($"/api/orders/{fixture.OrderId}/marking/export",
            new { expected_snapshot_hash = snapshotHash });
        var secondTask = host.Client.PostAsJsonAsync($"/api/orders/{fixture.OrderId}/marking/export",
            new { expected_snapshot_hash = snapshotHash });
        await WaitUntilSessionsWaitForLock(baseConnectionString, applicationName, expectedCount: 2);
        await gateTransaction.CommitAsync();

        using var first = await firstTask;
        using var second = await secondTask;
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());
        Assert.NotEmpty(await first.Content.ReadAsByteArrayAsync());
        Assert.NotEmpty(await second.Content.ReadAsByteArrayAsync());

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
        Assert.Equal(new[] { 0d, 0d }, created.Order().ToArray());
        Assert.Equal(new[] { 0d, 0d }, reused.Order().ToArray());

        var snapshot = await fixture.ReadSnapshotAsync();
        Assert.Equal(1, snapshot.MarkingOrders);
        Assert.Equal(0, snapshot.Imports);
        Assert.Equal(0, snapshot.Codes);
        Assert.Equal(0, await fixture.CountDistinctCodesAsync());
        Assert.Equal(1, await fixture.CountRequestScopesAsync());
        await using (var connection = new NpgsqlConnection(baseConnectionString))
        {
            await connection.OpenAsync();
            Assert.Equal(1, await ExecuteScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM marking_request_export_batch WHERE order_id = @id",
                ("id", fixture.OrderId)));
            Assert.Equal(1, await ExecuteScalarIntAsync(
                connection,
                """
SELECT COUNT(*)
FROM marking_request_export_batch_request request
INNER JOIN marking_request_export_batch batch ON batch.id = request.export_batch_id
WHERE batch.order_id = @id
""",
                ("id", fixture.OrderId)));
        }
        Assert.Equal(0, snapshot.Ledger);
        Assert.Equal(1, snapshot.Docs);
        Assert.Equal(1, snapshot.DocLines);
    }

    [Fact]
    public async Task ExistingExportBatchReplay_UsesImmutableItemNameSnapshotWithoutCreatingNewRows()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await OrderFixture.CreateAsync(connectionString, quantity: 7);
        var store = new PostgresDataStore(connectionString);
        var service = new OrderMarkingExportService(store);
        var preview = service.Preview(fixture.OrderId);
        Assert.True(preview.IsSuccess, preview.Message);

        var first = service.Export(fixture.OrderId, DateTime.UtcNow, preview.SnapshotHash);
        Assert.True(first.IsSuccess, first.Message);
        Assert.NotNull(first.FileBytes);
        Assert.Contains(fixture.ItemName, ReadWorksheetXml(first.FileBytes!), StringComparison.Ordinal);

        var currentPreview = service.Preview(fixture.OrderId);
        Assert.True(currentPreview.IsSuccess, currentPreview.Message);
        var currentRetry = service.Export(
            fixture.OrderId,
            DateTime.UtcNow.AddSeconds(30),
            currentPreview.SnapshotHash);
        Assert.True(currentRetry.IsSuccess, currentRetry.Message);
        Assert.NotNull(currentRetry.FileBytes);
        Assert.Equal((1, 1), await fixture.ReadExportBatchCountsAsync());

        var changedName = $"Changed live item {Guid.NewGuid():N}";
        await fixture.UpdateItemNameAsync(changedName);

        var replay = service.Export(fixture.OrderId, DateTime.UtcNow.AddMinutes(1), preview.SnapshotHash);
        Assert.True(replay.IsSuccess, replay.Message);
        Assert.NotNull(replay.FileBytes);
        var replayWorksheet = ReadWorksheetXml(replay.FileBytes!);
        Assert.Contains(fixture.ItemName, replayWorksheet, StringComparison.Ordinal);
        Assert.DoesNotContain(changedName, replayWorksheet, StringComparison.Ordinal);
        Assert.Equal((1, 1), await fixture.ReadExportBatchCountsAsync());
        Assert.Equal(1, (await fixture.ReadSnapshotAsync()).MarkingOrders);

        await fixture.SetPlannedQuantityAsync(8);
        var driftedReplay = service.Export(
            fixture.OrderId,
            DateTime.UtcNow.AddMinutes(2),
            preview.SnapshotHash);
        Assert.False(driftedReplay.IsSuccess);
        Assert.Equal("MARKING_EXPORT_SNAPSHOT_CHANGED", driftedReplay.Message);
        Assert.Equal((1, 1), await fixture.ReadExportBatchCountsAsync());
        Assert.Equal(1, (await fixture.ReadSnapshotAsync()).MarkingOrders);

        var archive = service.DownloadLatestHistoricalBatch(fixture.OrderId, DateTime.UtcNow.AddMinutes(3));
        Assert.True(archive.IsSuccess, archive.Message);
        Assert.NotNull(archive.FileBytes);
        Assert.StartsWith("ARCHIVE_", archive.FileName, StringComparison.Ordinal);
        var archiveWorksheet = ReadWorksheetXml(archive.FileBytes!);
        Assert.Contains(fixture.ItemName, archiveWorksheet, StringComparison.Ordinal);
        Assert.DoesNotContain(changedName, archiveWorksheet, StringComparison.Ordinal);
        Assert.Equal((1, 1), await fixture.ReadExportBatchCountsAsync());
        Assert.Equal(1, (await fixture.ReadSnapshotAsync()).MarkingOrders);
    }

    [Fact]
    public async Task RealImport_ActivatesAllScopeCoverageOnlyAfterRequiredQuantity()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await OrderFixture.CreateAsync(connectionString, quantity: 2);
        var store = new PostgresDataStore(connectionString);
        var export = ExportWithPreview(store, fixture.OrderId);
        Assert.True(export.IsSuccess, export.Message);
        var waitingProgress = Assert.Single(store.GetMarkingLineProgress(new[] { fixture.OrderId })).Value;
        Assert.Equal("WAITING_FOR_CODES", waitingProgress.State);
        Assert.Equal(2, waitingProgress.RealRequiredQuantity);
        Assert.Equal(0, waitingProgress.ValidRealCoveredQuantity);

        var markingOrderId = await fixture.GetMarkingOrderIdAsync();
        var firstImportId = Guid.NewGuid();
        store.AddMarkingCodeImport(CreateImport(firstImportId, markingOrderId, Guid.NewGuid(), 1, DateTime.UtcNow));
        store.AddMarkingCodes([CreateRealCode(markingOrderId, firstImportId, fixture.Gtin, "FIRST")]);
        store.RecordConfirmedRealImportAndActivateCoverage(
            markingOrderId, firstImportId, "first.tsv", Guid.NewGuid().ToString("N"), 100, 1, DateTime.UtcNow);
        Assert.Equal(0, await fixture.CountOperationalCoverageAsync());

        var secondImportId = Guid.NewGuid();
        store.AddMarkingCodeImport(CreateImport(secondImportId, markingOrderId, Guid.NewGuid(), 1, DateTime.UtcNow));
        store.AddMarkingCodes([CreateRealCode(markingOrderId, secondImportId, fixture.Gtin, "SECOND")]);
        store.RecordConfirmedRealImportAndActivateCoverage(
            markingOrderId, secondImportId, "second.tsv", Guid.NewGuid().ToString("N"), 100, 1, DateTime.UtcNow);

        Assert.Equal(1, await fixture.CountOperationalCoverageAsync());
        Assert.Equal(2, await fixture.SumOperationalCoverageAsync());
    }

    [Fact]
    public async Task OrderScopedConfirm_AtomicallyStoresBatchCodesAndActivatesAggregateCoverage()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await OrderFixture.CreateAsync(connectionString, quantity: 2);
        var store = new PostgresDataStore(connectionString);
        var export = ExportWithPreview(store, fixture.OrderId);
        Assert.True(export.IsSuccess, export.Message);

        var dm1 = $"01{fixture.Gtin}21ORDER-SCOPED-1\u001D93VERIFY";
        var dm2 = $"01{fixture.Gtin}21ORDER-SCOPED-2\u001D93VERIFY";
        var file = new MarkingImportUploadFile(
            "name-without-request-number.tsv",
            System.Text.Encoding.UTF8.GetBytes(
                $"\"{dm1}\"\t{fixture.Gtin}\tProduct\n\"{dm2}\"\t{fixture.Gtin}\tProduct"));
        var service = new OrderScopedMarkingImportService(store);
        var preview = service.Preview(fixture.OrderId, new[] { file });
        Assert.True(preview.IsValid, preview.Message);
        Assert.False(preview.RequiresRecoveryConfirmation);
        Assert.Single(preview.Requests);
        Assert.True(preview.Requests[0].ReserveShort);

        var batchId = Guid.NewGuid();
        var result = service.Confirm(
            fixture.OrderId,
            batchId,
            preview.SnapshotHash,
            $"confirm-{batchId:N}",
            confirmRecovery: false,
            new[] { file });

        Assert.False(result.WasAlreadyConfirmed);
        Assert.Equal(2, result.PersistedCodeCount);
        Assert.Single(result.ActivatedMarkingOrderIds);
        Assert.Equal(1, await fixture.CountOperationalCoverageAsync());
        Assert.Equal(2, await fixture.SumOperationalCoverageAsync());
        var completeProgress = Assert.Single(store.GetMarkingLineProgress(new[] { fixture.OrderId })).Value;
        Assert.Equal("COMPLETE", completeProgress.State);
        Assert.Equal(2, completeProgress.ValidRealCoveredQuantity);

        var repeated = service.Confirm(
            fixture.OrderId,
            batchId,
            preview.SnapshotHash,
            $"confirm-{batchId:N}",
            confirmRecovery: false,
            new[] { file });
        Assert.True(repeated.WasAlreadyConfirmed);
        Assert.Equal(2, repeated.PersistedCodeCount);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        Assert.Equal(1, await ExecuteScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM marking_import_batch_request WHERE import_batch_id = @id",
            ("id", batchId)));
        Assert.Equal(1, await ExecuteScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM marking_import_file WHERE import_batch_id = @id",
            ("id", batchId)));
        Assert.Equal(0, await ExecuteScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM marking_code WHERE marking_order_id = @id AND (receipt_doc_id IS NOT NULL OR receipt_line_id IS NOT NULL)",
            ("id", preview.Requests[0].MarkingOrderId)));
    }

    [Fact]
    public async Task OrderScopedConfirm_WhenSameGtinRequestAppearsAfterPreview_RejectsWithoutPartialWrites()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await OrderFixture.CreateAsync(connectionString, quantity: 1);
        var store = new PostgresDataStore(connectionString);
        Assert.True(ExportWithPreview(store, fixture.OrderId).IsSuccess);

        var importService = new OrderScopedMarkingImportService(store);
        var initialDm = $"01{fixture.Gtin}21RACE-INITIAL\u001D93VERIFY";
        var initialFile = CreateObservedMarkingFile(fixture.Gtin, initialDm, "race-initial.tsv");
        var initialPreview = importService.Preview(fixture.OrderId, new[] { initialFile });
        Assert.True(initialPreview.IsValid, initialPreview.Message);
        var initialBatchId = Guid.NewGuid();
        var initialConfirm = importService.Confirm(
            fixture.OrderId,
            initialBatchId,
            initialPreview.SnapshotHash,
            $"initial-{initialBatchId:N}",
            confirmRecovery: false,
            new[] { initialFile });
        Assert.Equal(1, initialConfirm.PersistedCodeCount);

        var reserveDm = $"01{fixture.Gtin}21RACE-RESERVE\u001D93VERIFY";
        var reserveFile = CreateObservedMarkingFile(fixture.Gtin, reserveDm, "race-reserve.tsv");
        var stalePreview = importService.Preview(fixture.OrderId, new[] { reserveFile });
        Assert.True(stalePreview.IsValid, stalePreview.Message);
        var staleRequest = Assert.Single(stalePreview.Requests);
        Assert.Equal(0, staleRequest.OperationalRequiredQuantity - staleRequest.ImportedBefore);
        Assert.Equal(1, staleRequest.ValidInBatch);

        await fixture.AddIndependentPlannedPalletAsync(1);
        Assert.True(ExportWithPreview(store, fixture.OrderId).IsSuccess);
        Assert.Equal(new[] { 1, 1 }, await fixture.ReadMarkingRequestRequiredQuantitiesAsync());
        var before = await fixture.ReadImportMutationCountsAsync();

        var staleBatchId = Guid.NewGuid();
        var staleCommand = CreateSingleCodeConfirmCommand(
            fixture.OrderId, fixture.Gtin, reserveDm, reserveFile, stalePreview, staleBatchId);

        var error = Assert.Throws<InvalidOperationException>(
            () => store.ConfirmOrderScopedMarkingImport(staleCommand));
        Assert.Equal("MARKING_IMPORT_SNAPSHOT_CHANGED", error.Message);
        Assert.Equal(before, await fixture.ReadImportMutationCountsAsync());
    }

    [Fact]
    public async Task ConcurrentSameOrderExportThenConfirm_SerializesAndRejectsStaleAllocation()
    {
        var baseConnectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await OrderFixture.CreateAsync(baseConnectionString, quantity: 1);
        var setupStore = new PostgresDataStore(baseConnectionString);
        Assert.True(ExportWithPreview(setupStore, fixture.OrderId).IsSuccess);

        var dm = $"01{fixture.Gtin}21RACE-CONCURRENT\u001D93VERIFY";
        var file = CreateObservedMarkingFile(fixture.Gtin, dm, "race-concurrent.tsv");
        var stalePreview = new OrderScopedMarkingImportService(setupStore)
            .Preview(fixture.OrderId, new[] { file });
        Assert.True(stalePreview.IsValid, stalePreview.Message);
        await fixture.AddIndependentPlannedPalletAsync(1);

        var exportApplication = $"marking-race-export-{Guid.NewGuid():N}";
        var confirmApplication = $"marking-race-confirm-{Guid.NewGuid():N}";
        var exportStore = new PostgresDataStore(WithApplicationName(baseConnectionString, exportApplication));
        var confirmStore = new PostgresDataStore(WithApplicationName(baseConnectionString, confirmApplication));
        var exportService = new OrderMarkingExportService(exportStore);
        var exportPreview = exportService.Preview(fixture.OrderId);
        Assert.True(exportPreview.IsSuccess, exportPreview.Message);
        var staleBatchId = Guid.NewGuid();
        var staleCommand = CreateSingleCodeConfirmCommand(
            fixture.OrderId, fixture.Gtin, dm, file, stalePreview, staleBatchId);

        await using var gate = new NpgsqlConnection(
            WithApplicationName(baseConnectionString, $"marking-race-gate-{Guid.NewGuid():N}"));
        await gate.OpenAsync();
        await using var gateTransaction = await gate.BeginTransactionAsync();
        await using (var gateCommand = gate.CreateCommand())
        {
            gateCommand.Transaction = gateTransaction;
            gateCommand.CommandText = "SELECT id FROM orders WHERE id = @order_id FOR UPDATE";
            gateCommand.Parameters.AddWithValue("@order_id", fixture.OrderId);
            Assert.Equal(fixture.OrderId, Convert.ToInt64(await gateCommand.ExecuteScalarAsync()));
        }

        var exportTask = Task.Run(() => exportService.Export(
            fixture.OrderId,
            DateTime.UtcNow,
            exportPreview.SnapshotHash));
        await WaitUntilSessionsWaitForLock(baseConnectionString, exportApplication, expectedCount: 1);

        var confirmTask = Task.Run(() => Record.Exception(
            () => confirmStore.ConfirmOrderScopedMarkingImport(staleCommand)));
        await WaitUntilSessionsWaitForLock(baseConnectionString, confirmApplication, expectedCount: 1);
        await gateTransaction.CommitAsync();

        var export = await exportTask;
        var confirmError = await confirmTask;
        Assert.True(export.IsSuccess, export.Message);
        Assert.IsType<InvalidOperationException>(confirmError);
        Assert.Equal("MARKING_IMPORT_SNAPSHOT_CHANGED", confirmError!.Message);
        Assert.Equal(new[] { 1, 1 }, await fixture.ReadMarkingRequestRequiredQuantitiesAsync());
        Assert.Equal(new ImportMutationCounts(0, 0, 0, 0), await fixture.ReadImportMutationCountsAsync());
    }

    [Fact]
    public async Task ProductionReplan_WaitsForCanonicalOrderSerializationPoint()
    {
        var baseConnectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await OrderFixture.CreateAsync(baseConnectionString, quantity: 1);
        await fixture.SetOrderQuantityOnlyAsync(2);
        var planApplication = $"marking-race-plan-{Guid.NewGuid():N}";
        var planStore = new PostgresDataStore(WithApplicationName(baseConnectionString, planApplication));

        await using var gate = new NpgsqlConnection(
            WithApplicationName(baseConnectionString, $"marking-race-plan-gate-{Guid.NewGuid():N}"));
        await gate.OpenAsync();
        await using var gateTransaction = await gate.BeginTransactionAsync();
        await using (var gateCommand = gate.CreateCommand())
        {
            gateCommand.Transaction = gateTransaction;
            gateCommand.CommandText = "SELECT id FROM orders WHERE id = @order_id FOR UPDATE";
            gateCommand.Parameters.AddWithValue("@order_id", fixture.OrderId);
            Assert.Equal(fixture.OrderId, Convert.ToInt64(await gateCommand.ExecuteScalarAsync()));
        }

        var planTask = Task.Run(() => new ProductionPalletService(planStore).PlanOrder(fixture.OrderId));
        var prematureCompletion = await Task.WhenAny(planTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.NotSame(planTask, prematureCompletion);
        await WaitUntilSessionsWaitForLock(baseConnectionString, planApplication, expectedCount: 1);

        await gateTransaction.CommitAsync();
        var plan = await planTask;
        Assert.True(plan.ProductionRequired);
    }

    [Fact]
    public async Task SameGtinTwoRequestConfirm_CoversBothOperationalDeficitsBeforeReserve()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await OrderFixture.CreateAsync(connectionString, quantity: 1);
        var store = new PostgresDataStore(connectionString);
        Assert.True(ExportWithPreview(store, fixture.OrderId).IsSuccess);
        await fixture.AddIndependentPlannedPalletAsync(1);
        Assert.True(ExportWithPreview(store, fixture.OrderId).IsSuccess);

        var firstDm = $"01{fixture.Gtin}21SAME-GTIN-FIRST\u001D93VERIFY";
        var secondDm = $"01{fixture.Gtin}21SAME-GTIN-SECOND\u001D93VERIFY";
        var file = new MarkingImportUploadFile(
            "same-gtin.tsv",
            Encoding.UTF8.GetBytes(
                $"\"{firstDm}\"\t{fixture.Gtin}\tProduct\n\"{secondDm}\"\t{fixture.Gtin}\tProduct"));
        var service = new OrderScopedMarkingImportService(store);
        var preview = service.Preview(fixture.OrderId, new[] { file });
        Assert.True(preview.IsValid, preview.Message);
        Assert.Equal(2, preview.Requests.Count);
        Assert.All(preview.Requests, request =>
        {
            Assert.Equal(1, request.OperationalRequiredQuantity);
            Assert.Equal(1, request.ValidInBatch);
        });

        var batchId = Guid.NewGuid();
        var confirmed = service.Confirm(
            fixture.OrderId,
            batchId,
            preview.SnapshotHash,
            $"same-gtin-{batchId:N}",
            confirmRecovery: false,
            new[] { file });

        Assert.Equal(2, confirmed.PersistedCodeCount);
        Assert.Equal(new[] { 1, 1 }, await fixture.ReadRealCodeCountsByRequestAsync());
        Assert.Equal(2, await fixture.CountOperationalCoverageAsync());
        Assert.Equal(2, await fixture.SumOperationalCoverageAsync());
    }

    [Fact]
    public async Task OperationalCoverage_DecreaseThenIncrease_DoesNotResurrectRetiredQuantity()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await SetCutoverStateAsync(connectionString, MarkingCutoverState.Enforced);
        await using var fixture = await OrderFixture.CreateAsync(connectionString, quantity: 3000);
        var store = new PostgresDataStore(connectionString);
        var firstExport = ExportWithPreview(store, fixture.OrderId);
        Assert.True(firstExport.IsSuccess, firstExport.Message);

        await fixture.SeedAggregateRealCoverageAsync(3000);
        await fixture.SetPlannedQuantityAsync(2000);

        var decreased = Assert.Single(store.GetAggregateMarkingCoverageByOrderLine(fixture.OrderId));
        Assert.Equal(2000, decreased.Value.OperationalQuantity);

        await fixture.SetPlannedQuantityAsync(2500);
        var increased = Assert.Single(store.GetAggregateMarkingCoverageByOrderLine(fixture.OrderId));
        Assert.Equal(2000, increased.Value.OperationalQuantity);

        var deltaExport = ExportWithPreview(store, fixture.OrderId);
        Assert.True(deltaExport.IsSuccess, deltaExport.Message);
        Assert.Equal(new[] { 500, 3000 }, await fixture.ReadMarkingRequestRequiredQuantitiesAsync());

        await fixture.CancelPalletAsync();
        var afterCancellation = store.GetAggregateMarkingCoverageByOrderLine(fixture.OrderId);
        Assert.True(afterCancellation.Count == 0
                    || afterCancellation.Values.All(value => value.OperationalQuantity == 0));
        var reactivation = await Assert.ThrowsAsync<PostgresException>(
            () => fixture.TryReactivateConsumedScopeAsync());
        Assert.Contains("MARKING_REQUEST_SCOPE_CAPACITY_CANNOT_INCREASE", reactivation.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shadow_FailsClosedForNewExportImportFillAndMarkedProductionClose()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await SetCutoverStateAsync(connectionString, MarkingCutoverState.Shadow);
        await using var fixture = await OrderFixture.CreateAsync(connectionString, quantity: 100);
        var store = new PostgresDataStore(connectionString);

        var export = new OrderMarkingExportService(store).Export(fixture.OrderId, DateTime.UtcNow);
        Assert.False(export.IsSuccess);
        Assert.Contains(MarkingCutoverRuntimeErrors.MaintenanceRequired, export.Message, StringComparison.Ordinal);
        Assert.Equal(0, (await fixture.ReadSnapshotAsync()).MarkingOrders);

        var import = new OrderScopedMarkingImportService(store).Preview(
            fixture.OrderId,
            Array.Empty<MarkingImportUploadFile>());
        Assert.False(import.IsValid);
        Assert.Equal(MarkingCutoverRuntimeErrors.MaintenanceRequired, import.ErrorCode);
        var confirm = Assert.Throws<InvalidOperationException>(() =>
            new OrderScopedMarkingImportService(store).Confirm(
                fixture.OrderId,
                Guid.NewGuid(),
                "snapshot",
                "idempotency",
                confirmRecovery: false,
                Array.Empty<MarkingImportUploadFile>()));
        Assert.Contains(MarkingCutoverRuntimeErrors.MaintenanceRequired, confirm.Message, StringComparison.Ordinal);

        var fill = new ProductionPalletService(store).Fill(fixture.Hu, "shadow-test", fixture.OrderId);
        Assert.False(fill.Success);
        Assert.Equal(MarkingCutoverRuntimeErrors.MaintenanceRequired, fill.Error);
        Assert.Equal("PLANNED", await fixture.ReadPalletStatusAsync());

        var close = Assert.Throws<InvalidOperationException>(() =>
            store.ValidateAndCreateReadyHuFacts(fixture.PrdDocId, DateTime.UtcNow));
        Assert.Contains(MarkingCutoverRuntimeErrors.MaintenanceRequired, close.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrandfatherAllowance_IsBounded_FollowsAdoption_AndRetiresWithoutReuse()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await OrderFixture.CreateAsync(connectionString, quantity: 3000);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var suffix = Guid.NewGuid().ToString("N");

        await using var seed = connection.CreateCommand();
        seed.Transaction = transaction;
        seed.CommandText = """
WITH source AS (
    SELECT pll.id AS component_id, pll.marking_subject_id AS subject_id,
           pll.order_line_id, pll.item_id, i.gtin,
           pp.id AS pallet_id, subject.revision AS subject_revision
    FROM production_pallet_lines pll
    INNER JOIN production_pallets pp ON pp.id = pll.production_pallet_id
    INNER JOIN items i ON i.id = pll.item_id
    INNER JOIN marking_production_subject subject ON subject.id = pll.marking_subject_id
    WHERE pp.order_id = @order_id
    LIMIT 1
), parent AS (
    INSERT INTO marking_synthetic_legacy_allowlist(
        order_line_id, allowed_synthetic_qty, target_qty_at_cutover,
        approved_at, approved_by, preflight_hash)
    SELECT order_line_id, 3000, 3000, @now, 'test', @hash FROM source
    RETURNING id
), approval AS (
    INSERT INTO marking_synthetic_legacy_allowlist_subject(
        id, allowlist_id, marking_subject_id, component_id_snapshot,
        item_id_snapshot, gtin_snapshot, subject_revision_at_cutover, subject_quantity_at_cutover,
        approved_quantity, preflight_hash, approved_at, approved_by)
    SELECT @approval_id, parent.id, source.subject_id, source.component_id,
           source.item_id, source.gtin, source.subject_revision, 3000, 3000, @hash, @now, 'test'
    FROM source CROSS JOIN parent
    RETURNING marking_subject_id
), allowance AS (
    INSERT INTO marking_grandfather_operational_allowance(
        id, allowlist_subject_id, marking_subject_id, approved_quantity,
        usable_quantity_cap, cutover_subject_revision, preflight_hash, created_at)
    SELECT @allowance_id, @approval_id, approval.marking_subject_id, 3000, 3000,
           subject.revision, @hash, @now
    FROM approval
    INNER JOIN marking_production_subject subject ON subject.id = approval.marking_subject_id
    RETURNING marking_subject_id
)
INSERT INTO marking_operational_coverage(
    id, marking_subject_id, source_type, covered_quantity,
    grandfather_allowance_id, created_at)
SELECT @coverage_id, marking_subject_id, 'GRANDFATHER_ALLOWANCE', 3000,
       @allowance_id, @now
FROM allowance;
""";
        var approvalId = Guid.NewGuid();
        var allowanceId = Guid.NewGuid();
        var coverageId = Guid.NewGuid();
        seed.Parameters.AddWithValue("@order_id", fixture.OrderId);
        seed.Parameters.AddWithValue("@now", "2026-08-21T10:00:00Z");
        seed.Parameters.AddWithValue("@hash", $"bounded-{suffix}");
        seed.Parameters.AddWithValue("@approval_id", approvalId);
        seed.Parameters.AddWithValue("@allowance_id", allowanceId);
        seed.Parameters.AddWithValue("@coverage_id", coverageId);
        await seed.ExecuteNonQueryAsync();

        await using (var increase = connection.CreateCommand())
        {
            increase.Transaction = transaction;
            increase.CommandText = """
UPDATE production_pallet_lines
SET planned_qty = 3500
WHERE marking_subject_id = (
    SELECT marking_subject_id FROM marking_grandfather_operational_allowance WHERE id = @allowance_id);
""";
            increase.Parameters.AddWithValue("@allowance_id", allowanceId);
            await increase.ExecuteNonQueryAsync();
        }

        await using (var bounded = connection.CreateCommand())
        {
            bounded.Transaction = transaction;
            bounded.CommandText = """
SELECT subject.subject_quantity, allowance.approved_quantity, coverage.covered_quantity
FROM marking_grandfather_operational_allowance allowance
INNER JOIN marking_production_subject subject ON subject.id = allowance.marking_subject_id
INNER JOIN marking_operational_coverage coverage ON coverage.grandfather_allowance_id = allowance.id
WHERE allowance.id = @allowance_id;
""";
            bounded.Parameters.AddWithValue("@allowance_id", allowanceId);
            await using var reader = await bounded.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(3500m, reader.GetDecimal(0));
            Assert.Equal(3000m, reader.GetDecimal(1));
            Assert.Equal(3000m, reader.GetDecimal(2));
        }

        long adoptedOrderLineId;
        await using (var adopt = connection.CreateCommand())
        {
            adopt.Transaction = transaction;
            adopt.CommandText = """
WITH source AS (
    SELECT pll.item_id
    FROM production_pallet_lines pll
    WHERE pll.marking_subject_id = (
        SELECT marking_subject_id FROM marking_grandfather_operational_allowance WHERE id = @allowance_id)
), adopted_order AS (
    INSERT INTO orders(order_ref, order_type, status, created_at, marking_status)
    VALUES(@order_ref, 'CUSTOMER', 'IN_PROGRESS', @now, 'NOT_APPLIED')
    RETURNING id
)
INSERT INTO order_lines(order_id, item_id, qty_ordered)
SELECT adopted_order.id, source.item_id, 3500 FROM adopted_order CROSS JOIN source
RETURNING id;
""";
            adopt.Parameters.AddWithValue("@allowance_id", allowanceId);
            adopt.Parameters.AddWithValue("@order_ref", $"ADOPT-{suffix}");
            adopt.Parameters.AddWithValue("@now", "2026-08-21T10:01:00Z");
            adoptedOrderLineId = Convert.ToInt64(await adopt.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        await using (var rebind = connection.CreateCommand())
        {
            rebind.Transaction = transaction;
            rebind.CommandText = """
UPDATE production_pallet_lines
SET order_line_id = @order_line_id
WHERE marking_subject_id = (
    SELECT marking_subject_id FROM marking_grandfather_operational_allowance WHERE id = @allowance_id);
""";
            rebind.Parameters.AddWithValue("@order_line_id", adoptedOrderLineId);
            rebind.Parameters.AddWithValue("@allowance_id", allowanceId);
            await rebind.ExecuteNonQueryAsync();
        }

        await using (var adoptionState = connection.CreateCommand())
        {
            adoptionState.Transaction = transaction;
            adoptionState.CommandText = """
SELECT subject.current_order_line_id, allowance.approved_quantity, coverage.covered_quantity,
       consumption.active_quantity
FROM marking_grandfather_operational_allowance allowance
INNER JOIN marking_production_subject subject ON subject.id = allowance.marking_subject_id
INNER JOIN marking_operational_coverage coverage ON coverage.grandfather_allowance_id = allowance.id
INNER JOIN marking_operational_coverage_consumption consumption
        ON consumption.operational_coverage_id = coverage.id
WHERE allowance.id = @allowance_id;
""";
            adoptionState.Parameters.AddWithValue("@allowance_id", allowanceId);
            await using var reader = await adoptionState.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(adoptedOrderLineId, reader.GetInt64(0));
            Assert.Equal(3000m, reader.GetDecimal(1));
            Assert.Equal(3000m, reader.GetDecimal(2));
            Assert.Equal(3000m, reader.GetDecimal(3));
        }

        await using (var shrinkAndReincrease = connection.CreateCommand())
        {
            shrinkAndReincrease.Transaction = transaction;
            shrinkAndReincrease.CommandText = """
UPDATE production_pallet_lines
SET planned_qty = 2000
WHERE marking_subject_id = (
    SELECT marking_subject_id FROM marking_grandfather_operational_allowance WHERE id = @allowance_id);

UPDATE production_pallet_lines
SET planned_qty = 2500
WHERE marking_subject_id = (
    SELECT marking_subject_id FROM marking_grandfather_operational_allowance WHERE id = @allowance_id);

SELECT consumption.active_quantity
FROM marking_operational_coverage coverage
INNER JOIN marking_operational_coverage_consumption consumption
        ON consumption.operational_coverage_id = coverage.id
WHERE coverage.grandfather_allowance_id = @allowance_id;
""";
            shrinkAndReincrease.Parameters.AddWithValue("@allowance_id", allowanceId);
            Assert.Equal(2000m, Convert.ToDecimal(
                await shrinkAndReincrease.ExecuteScalarAsync(),
                CultureInfo.InvariantCulture));
        }

        await using (var cancel = connection.CreateCommand())
        {
            cancel.Transaction = transaction;
            cancel.CommandText = """
UPDATE production_pallets
SET status = 'CANCELLED'
WHERE id = (
    SELECT subject.current_production_pallet_id
    FROM marking_grandfather_operational_allowance allowance
    INNER JOIN marking_production_subject subject ON subject.id = allowance.marking_subject_id
    WHERE allowance.id = @allowance_id);
""";
            cancel.Parameters.AddWithValue("@allowance_id", allowanceId);
            await cancel.ExecuteNonQueryAsync();
        }

        await using (var retired = connection.CreateCommand())
        {
            retired.Transaction = transaction;
            retired.CommandText = """
SELECT allowance.retired_at IS NOT NULL,
       coverage.retired_at IS NOT NULL,
       (SELECT COUNT(*) FROM marking_operational_coverage other
        WHERE other.id <> coverage.id
          AND other.grandfather_allowance_id = allowance.id)
FROM marking_grandfather_operational_allowance allowance
INNER JOIN marking_operational_coverage coverage ON coverage.grandfather_allowance_id = allowance.id
WHERE allowance.id = @allowance_id;
""";
            retired.Parameters.AddWithValue("@allowance_id", allowanceId);
            await using var reader = await retired.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal(0L, reader.GetInt64(2));
        }

        await transaction.RollbackAsync();
    }

    private static MarkingCode CreateRealCode(Guid markingOrderId, Guid importId, string gtin, string suffix)
    {
        var id = Guid.NewGuid();
        return new MarkingCode
        {
            Id = id,
            Code = $"010{gtin}21TEST-{suffix}-{id:N}\u001D93VERIFY",
            CodeHash = $"REAL-{id:N}",
            Gtin = gtin,
            MarkingOrderId = markingOrderId,
            ImportId = importId,
            Status = MarkingCodeStatus.Imported,
            Origin = MarkingCodeOrigin.RealImport,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
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
            string itemName,
            long orderId,
            long orderLineId,
            long locationId,
            string gtin,
            long prdDocId,
            string hu)
        {
            _connectionString = connectionString;
            _itemTypeId = itemTypeId;
            ItemId = itemId;
            ItemName = itemName;
            OrderId = orderId;
            OrderLineId = orderLineId;
            LocationId = locationId;
            Gtin = gtin;
            PrdDocId = prdDocId;
            Hu = hu;
        }

        public long ItemId { get; }
        public string ItemName { get; }
        public long OrderId { get; }
        public long OrderLineId { get; }
        public long LocationId { get; }
        public string Gtin { get; }
        public long PrdDocId { get; }
        public string Hu { get; }

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
            var itemName = $"Marking item {suffix}";
            var itemId = await InsertReturningIdAsync(
                connection,
                """
INSERT INTO items(name, barcode, gtin, base_uom, item_type_id, max_qty_per_hu)
VALUES (@name, @barcode, @gtin, 'шт', @item_type_id, 10000)
RETURNING id;
""",
                ("name", itemName),
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
            long orderLineId;
            await using (var line = connection.CreateCommand())
            {
                line.CommandText = """
INSERT INTO order_lines(order_id, item_id, qty_ordered)
VALUES (@order_id, @item_id, @qty)
RETURNING id;
""";
                line.Parameters.AddWithValue("@order_id", orderId);
                line.Parameters.AddWithValue("@item_id", itemId);
                line.Parameters.AddWithValue("@qty", (double)quantity);
                orderLineId = Convert.ToInt64(await line.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            }

            var locationId = await InsertReturningIdAsync(
                connection,
                """
INSERT INTO locations(code, name)
VALUES (@code, @name)
RETURNING id;
""",
                ("code", $"MK-{suffix}"),
                ("name", $"Marking location {suffix}"));
            var docId = await InsertReturningIdAsync(
                connection,
                """
INSERT INTO docs(doc_ref, type, status, created_at, order_id, order_ref)
VALUES (@doc_ref, 'PRODUCTION_RECEIPT', 'DRAFT', @created_at, @order_id, @order_ref)
RETURNING id;
""",
                ("doc_ref", $"PRD-MARKING-{suffix}"),
                ("created_at", "2026-07-27T10:00:00"),
                ("order_id", orderId),
                ("order_ref", $"MARKING-{suffix}"));
            var hu = $"HU-MARKING-{suffix}";
            var docLineId = await InsertReturningIdAsync(
                connection,
                """
INSERT INTO doc_lines(doc_id, order_line_id, item_id, qty, to_location_id, to_hu)
VALUES (@doc_id, @order_line_id, @item_id, @qty, @location_id, @hu)
RETURNING id;
""",
                ("doc_id", docId),
                ("order_line_id", orderLineId),
                ("item_id", itemId),
                ("qty", (double)quantity),
                ("location_id", locationId),
                ("hu", hu));
            var palletId = await InsertReturningIdAsync(
                connection,
                """
INSERT INTO production_pallets(
    prd_doc_id, doc_line_id, order_id, order_line_id, item_id,
    hu_code, planned_qty, to_location_id, status, created_at)
VALUES(
    @doc_id, @doc_line_id, @order_id, @order_line_id, @item_id,
    @hu, @qty, @location_id, 'PLANNED', @created_at)
RETURNING id;
""",
                ("doc_id", docId),
                ("doc_line_id", docLineId),
                ("order_id", orderId),
                ("order_line_id", orderLineId),
                ("item_id", itemId),
                ("hu", hu),
                ("qty", (double)quantity),
                ("location_id", locationId),
                ("created_at", "2026-07-27T10:00:00"));
            await using (var component = connection.CreateCommand())
            {
                component.CommandText = """
INSERT INTO production_pallet_lines(
    production_pallet_id, doc_line_id, order_line_id, item_id,
    planned_qty, filled_qty, created_at)
VALUES(@pallet_id, @doc_line_id, @order_line_id, @item_id, @qty, 0, @created_at);
""";
                component.Parameters.AddWithValue("@pallet_id", palletId);
                component.Parameters.AddWithValue("@doc_line_id", docLineId);
                component.Parameters.AddWithValue("@order_line_id", orderLineId);
                component.Parameters.AddWithValue("@item_id", itemId);
                component.Parameters.AddWithValue("@qty", (double)quantity);
                component.Parameters.AddWithValue("@created_at", "2026-07-27T10:00:00");
                await component.ExecuteNonQueryAsync();
            }

            return new OrderFixture(
                connectionString, itemTypeId, itemId, itemName, orderId, orderLineId,
                locationId, gtin, docId, hu);
        }

        public async Task AddIndependentPlannedPalletAsync(decimal quantity)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var hu = $"HU-MARKING-DELTA-{Guid.NewGuid():N}";

            await using (var updateLine = connection.CreateCommand())
            {
                updateLine.Transaction = transaction;
                updateLine.CommandText = "UPDATE order_lines SET qty_ordered = qty_ordered + @qty WHERE id = @line_id";
                updateLine.Parameters.AddWithValue("@qty", quantity);
                updateLine.Parameters.AddWithValue("@line_id", OrderLineId);
                Assert.Equal(1, await updateLine.ExecuteNonQueryAsync());
            }

            long docLineId;
            await using (var insertLine = connection.CreateCommand())
            {
                insertLine.Transaction = transaction;
                insertLine.CommandText = """
INSERT INTO doc_lines(doc_id, order_line_id, item_id, qty, to_location_id, to_hu)
VALUES (@doc_id, @order_line_id, @item_id, @qty, @location_id, @hu)
RETURNING id;
""";
                insertLine.Parameters.AddWithValue("@doc_id", PrdDocId);
                insertLine.Parameters.AddWithValue("@order_line_id", OrderLineId);
                insertLine.Parameters.AddWithValue("@item_id", ItemId);
                insertLine.Parameters.AddWithValue("@qty", quantity);
                insertLine.Parameters.AddWithValue("@location_id", LocationId);
                insertLine.Parameters.AddWithValue("@hu", hu);
                docLineId = Convert.ToInt64(await insertLine.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            }

            long palletId;
            await using (var insertPallet = connection.CreateCommand())
            {
                insertPallet.Transaction = transaction;
                insertPallet.CommandText = """
INSERT INTO production_pallets(
    prd_doc_id, doc_line_id, order_id, order_line_id, item_id,
    hu_code, planned_qty, to_location_id, status, created_at)
VALUES(
    @doc_id, @doc_line_id, @order_id, @order_line_id, @item_id,
    @hu, @qty, @location_id, 'PLANNED', @created_at)
RETURNING id;
""";
                insertPallet.Parameters.AddWithValue("@doc_id", PrdDocId);
                insertPallet.Parameters.AddWithValue("@doc_line_id", docLineId);
                insertPallet.Parameters.AddWithValue("@order_id", OrderId);
                insertPallet.Parameters.AddWithValue("@order_line_id", OrderLineId);
                insertPallet.Parameters.AddWithValue("@item_id", ItemId);
                insertPallet.Parameters.AddWithValue("@hu", hu);
                insertPallet.Parameters.AddWithValue("@qty", quantity);
                insertPallet.Parameters.AddWithValue("@location_id", LocationId);
                insertPallet.Parameters.AddWithValue("@created_at", "2026-08-27T12:00:00");
                palletId = Convert.ToInt64(await insertPallet.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            }

            await using (var insertComponent = connection.CreateCommand())
            {
                insertComponent.Transaction = transaction;
                insertComponent.CommandText = """
INSERT INTO production_pallet_lines(
    production_pallet_id, doc_line_id, order_line_id, item_id,
    planned_qty, filled_qty, created_at)
VALUES (@pallet_id, @doc_line_id, @order_line_id, @item_id, @qty, 0, @created_at);
""";
                insertComponent.Parameters.AddWithValue("@pallet_id", palletId);
                insertComponent.Parameters.AddWithValue("@doc_line_id", docLineId);
                insertComponent.Parameters.AddWithValue("@order_line_id", OrderLineId);
                insertComponent.Parameters.AddWithValue("@item_id", ItemId);
                insertComponent.Parameters.AddWithValue("@qty", quantity);
                insertComponent.Parameters.AddWithValue("@created_at", "2026-08-27T12:00:00");
                Assert.Equal(1, await insertComponent.ExecuteNonQueryAsync());
            }

            await transaction.CommitAsync();
        }

        public async Task SetOrderQuantityOnlyAsync(decimal quantity)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE order_lines SET qty_ordered = @qty WHERE id = @line_id",
                connection);
            command.Parameters.AddWithValue("@qty", quantity);
            command.Parameters.AddWithValue("@line_id", OrderLineId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async Task UpdateItemNameAsync(string name)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE items SET name = @name WHERE id = @item_id",
                connection);
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@item_id", ItemId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async Task<(int Batches, int Requests)> ReadExportBatchCountsAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT
    (SELECT COUNT(*) FROM marking_request_export_batch WHERE order_id = @order_id),
    (SELECT COUNT(*)
     FROM marking_request_export_batch_request request
     INNER JOIN marking_request_export_batch batch ON batch.id = request.export_batch_id
     WHERE batch.order_id = @order_id);
""";
            command.Parameters.AddWithValue("@order_id", OrderId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetInt32(0), reader.GetInt32(1));
        }

        public async Task<string> ReadPalletStatusAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT status FROM production_pallets WHERE order_id = @order_id LIMIT 1",
                connection);
            command.Parameters.AddWithValue("@order_id", OrderId);
            return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)
                   ?? string.Empty;
        }

        public async Task CreateFaultTriggerAsync(string triggerName, string functionName)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
CREATE FUNCTION {functionName}() RETURNS trigger AS $$
BEGIN
    IF NEW.gtin_snapshot = '{Gtin}' THEN
        RAISE EXCEPTION 'injected marking_request_scope failure';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER {triggerName}
BEFORE INSERT ON marking_request_scope
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
DROP TRIGGER IF EXISTS {triggerName} ON marking_request_scope;
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

        public async Task<int> CountRequestScopesAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            return await ExecuteScalarIntAsync(
                connection,
                """
SELECT COUNT(*)
FROM marking_request_scope scope
INNER JOIN marking_order mo ON mo.id = scope.marking_order_id
WHERE mo.order_id = @order_id
""",
                ("order_id", OrderId));
        }

        public async Task<Guid> GetMarkingOrderIdAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM marking_order WHERE order_id = @order_id ORDER BY created_at LIMIT 1";
            command.Parameters.AddWithValue("@order_id", OrderId);
            return (Guid)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Marking request not found."));
        }

        public async Task<int> CountOperationalCoverageAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            return await ExecuteScalarIntAsync(
                connection,
                """
SELECT COUNT(*)
FROM marking_operational_coverage coverage
INNER JOIN marking_request_scope scope ON scope.id = coverage.marking_request_scope_id
INNER JOIN marking_order mo ON mo.id = scope.marking_order_id
WHERE mo.order_id = @order_id AND coverage.retired_at IS NULL
""",
                ("order_id", OrderId));
        }

        public async Task SeedAggregateRealCoverageAsync(decimal quantity)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
WITH request AS (
    SELECT mo.id AS marking_order_id, scope.id AS scope_id, scope.marking_subject_id,
           mo.order_id, mo.order_line_id
    FROM marking_order mo
    INNER JOIN marking_request_scope scope ON scope.marking_order_id = mo.id
    WHERE mo.order_id = @order_id
    ORDER BY mo.created_at, scope.created_at
    LIMIT 1
), batch AS (
    INSERT INTO marking_import_batch(
        id, order_id, order_line_id, marking_order_id,
        original_filename, file_hash, file_size_bytes, row_count,
        status, target_marking_qty_snapshot, coverage_snapshot_hash,
        idempotency_key, created_at, confirmed_at)
    SELECT @batch_id, request.order_id, request.order_line_id, request.marking_order_id,
           'synthetic-regression.tsv', @file_hash, 1, 1,
           'Confirmed', @qty, @file_hash, @idempotency_key, @now, @now
    FROM request
    RETURNING id
)
INSERT INTO marking_operational_coverage(
    id, marking_subject_id, marking_request_scope_id, source_type,
    covered_quantity, import_batch_id, created_at)
SELECT @coverage_id, request.marking_subject_id, request.scope_id, 'REAL_IMPORT',
       @qty, batch.id, @now
FROM request CROSS JOIN batch;
""";
            var batchId = Guid.NewGuid();
            command.Parameters.AddWithValue("@order_id", OrderId);
            command.Parameters.AddWithValue("@batch_id", batchId);
            command.Parameters.AddWithValue("@coverage_id", Guid.NewGuid());
            command.Parameters.AddWithValue("@file_hash", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("@idempotency_key", $"coverage-regression-{batchId:N}");
            command.Parameters.AddWithValue("@qty", quantity);
            command.Parameters.AddWithValue("@now", "2026-08-24T10:00:00Z");
            await command.ExecuteNonQueryAsync();
        }

        public async Task SetPlannedQuantityAsync(decimal quantity)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
UPDATE production_pallet_lines component
SET planned_qty = @qty
FROM production_pallets pallet
WHERE component.production_pallet_id = pallet.id
  AND pallet.order_id = @order_id;

UPDATE order_lines
SET qty_ordered = @qty
WHERE order_id = @order_id;
""";
            command.Parameters.AddWithValue("@order_id", OrderId);
            command.Parameters.AddWithValue("@qty", quantity);
            Assert.Equal(2, await command.ExecuteNonQueryAsync());
        }

        public async Task CancelPalletAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
UPDATE production_pallets
SET status = 'CANCELLED'
WHERE order_id = @order_id AND status <> 'CANCELLED';
""", connection);
            command.Parameters.AddWithValue("@order_id", OrderId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async Task TryReactivateConsumedScopeAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
UPDATE marking_request_scope_consumption consumption
SET active_quantity = 1, updated_at = @now
FROM marking_request_scope scope
INNER JOIN marking_order request ON request.id = scope.marking_order_id
WHERE consumption.marking_request_scope_id = scope.id
  AND request.order_id = @order_id
  AND consumption.active_quantity = 0;
""", connection);
            command.Parameters.AddWithValue("@order_id", OrderId);
            command.Parameters.AddWithValue("@now", "2026-08-24T11:00:00Z");
            await command.ExecuteNonQueryAsync();
        }

        public async Task<int[]> ReadMarkingRequestRequiredQuantitiesAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT required_quantity
FROM marking_order
WHERE order_id = @order_id
ORDER BY required_quantity;
""";
            command.Parameters.AddWithValue("@order_id", OrderId);
            await using var reader = await command.ExecuteReaderAsync();
            var result = new List<int>();
            while (await reader.ReadAsync())
            {
                result.Add(reader.GetInt32(0));
            }

            return result.ToArray();
        }

        public async Task<int[]> ReadRealCodeCountsByRequestAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT COUNT(code.id)::integer
FROM marking_order request
LEFT JOIN marking_code code
       ON code.marking_order_id = request.id
      AND code.origin = 'RealImport'
      AND code.status = 'Imported'
WHERE request.order_id = @order_id
GROUP BY request.id
ORDER BY request.created_at, request.request_number, request.id;
""";
            command.Parameters.AddWithValue("@order_id", OrderId);
            await using var reader = await command.ExecuteReaderAsync();
            var result = new List<int>();
            while (await reader.ReadAsync())
            {
                result.Add(reader.GetInt32(0));
            }
            return result.ToArray();
        }

        public async Task<ImportMutationCounts> ReadImportMutationCountsAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT
    (SELECT COUNT(*) FROM marking_import_batch WHERE order_id = @order_id),
    (SELECT COUNT(*)
     FROM marking_code_import import_record
     INNER JOIN marking_order request ON request.id = import_record.matched_marking_order_id
     WHERE request.order_id = @order_id),
    (SELECT COUNT(*)
     FROM marking_code code
     INNER JOIN marking_order request ON request.id = code.marking_order_id
     WHERE request.order_id = @order_id),
    (SELECT COUNT(*)
     FROM marking_operational_coverage coverage
     INNER JOIN marking_request_scope scope ON scope.id = coverage.marking_request_scope_id
     INNER JOIN marking_order request ON request.id = scope.marking_order_id
     WHERE request.order_id = @order_id AND coverage.retired_at IS NULL);
""";
            command.Parameters.AddWithValue("@order_id", OrderId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return new ImportMutationCounts(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3));
        }

        public async Task<int> SumOperationalCoverageAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            return await ExecuteScalarIntAsync(
                connection,
                """
SELECT COALESCE(SUM(coverage.covered_quantity), 0)
FROM marking_operational_coverage coverage
INNER JOIN marking_request_scope scope ON scope.id = coverage.marking_request_scope_id
INNER JOIN marking_order mo ON mo.id = scope.marking_order_id
WHERE mo.order_id = @order_id AND coverage.retired_at IS NULL
""",
                ("order_id", OrderId));
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            // Successful export creates immutable request scopes by contract. Such a
            // committed integration fixture is intentionally not destructively rewritten.
            if (await ExecuteScalarIntAsync(
                    connection,
                    """
SELECT COUNT(*)
FROM marking_request_scope scope
INNER JOIN marking_order mo ON mo.id = scope.marking_order_id
WHERE mo.order_id = @order_id
""",
                    ("order_id", OrderId)) > 0)
            {
                return;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
DELETE FROM marking_code
WHERE marking_order_id IN (SELECT id FROM marking_order WHERE order_id = @order_id);
DELETE FROM marking_ready_hu_fact_lineage
WHERE operational_coverage_id IN (
    SELECT id FROM marking_operational_coverage
    WHERE marking_subject_id IN (
        SELECT id FROM marking_production_subject WHERE original_order_id = @order_id));
DELETE FROM marking_operational_coverage_import_lineage
WHERE operational_coverage_id IN (
    SELECT id FROM marking_operational_coverage
    WHERE marking_subject_id IN (
        SELECT id FROM marking_production_subject WHERE original_order_id = @order_id));
DELETE FROM marking_operational_coverage
WHERE marking_subject_id IN (
    SELECT id FROM marking_production_subject WHERE original_order_id = @order_id);
DELETE FROM marking_import_file
WHERE import_batch_id IN (
    SELECT id FROM marking_import_batch
    WHERE marking_order_id IN (SELECT id FROM marking_order WHERE order_id = @order_id));
DELETE FROM marking_import_batch
WHERE marking_order_id IN (SELECT id FROM marking_order WHERE order_id = @order_id);
DELETE FROM marking_request_scope
WHERE marking_order_id IN (SELECT id FROM marking_order WHERE order_id = @order_id);
DELETE FROM marking_code_import
WHERE matched_marking_order_id IN (SELECT id FROM marking_order WHERE order_id = @order_id);
DELETE FROM marking_order WHERE order_id = @order_id;
DELETE FROM production_pallet_lines
WHERE production_pallet_id IN (SELECT id FROM production_pallets WHERE order_id = @order_id);
DELETE FROM production_pallets WHERE order_id = @order_id;
DELETE FROM marking_production_subject WHERE original_order_id = @order_id;
DELETE FROM doc_lines WHERE doc_id IN (SELECT id FROM docs WHERE order_id = @order_id);
DELETE FROM docs WHERE order_id = @order_id;
DELETE FROM order_lines WHERE order_id = @order_id;
DELETE FROM orders WHERE id = @order_id;
DELETE FROM items WHERE id = @item_id;
DELETE FROM item_types WHERE id = @item_type_id;
DELETE FROM locations WHERE id = @location_id;
""";
            command.Parameters.AddWithValue("@order_id", OrderId);
            command.Parameters.AddWithValue("@item_id", ItemId);
            command.Parameters.AddWithValue("@item_type_id", _itemTypeId);
            command.Parameters.AddWithValue("@location_id", LocationId);
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

    private sealed record ImportMutationCounts(
        int ImportBatches,
        int CodeImports,
        int Codes,
        int ActiveCoverage);

    private static MarkingImportUploadFile CreateObservedMarkingFile(string gtin, string dataMatrix, string fileName)
    {
        return new MarkingImportUploadFile(
            fileName,
            Encoding.UTF8.GetBytes($"\"{dataMatrix}\"\t{gtin}\tProduct"));
    }

    private static OrderScopedMarkingImportConfirmCommand CreateSingleCodeConfirmCommand(
        long orderId,
        string gtin,
        string dataMatrix,
        MarkingImportUploadFile file,
        OrderScopedMarkingImportPreviewResult preview,
        Guid batchId)
    {
        var allocatedRequest = Assert.Single(preview.Requests.Where(value => value.ValidInBatch == 1));
        var fileHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file.Content));
        var codeHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(dataMatrix)));
        return new OrderScopedMarkingImportConfirmCommand(
            orderId,
            batchId,
            preview.SnapshotHash,
            $"confirm-{batchId:N}",
            ConfirmRecovery: false,
            new[] { file },
            new[]
            {
                new OrderScopedMarkingImportCode(
                    allocatedRequest.MarkingOrderId,
                    gtin,
                    dataMatrix,
                    codeHash,
                    fileHash,
                    SourceRowNumber: 1)
            },
            preview.Requests,
            DateTime.UtcNow);
    }

    private static async Task SetCutoverStateAsync(string connectionString, string state)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE marking_cutover_state
SET state = @state, updated_at = @now
WHERE id = TRUE;
""";
        command.Parameters.AddWithValue("@state", state);
        command.Parameters.AddWithValue("@now", "2026-08-24T09:00:00Z");
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static FlowStock.Core.Models.OrderMarkingExportResult ExportWithPreview(
        PostgresDataStore store,
        long orderId)
    {
        var service = new OrderMarkingExportService(store);
        var preview = service.Preview(orderId);
        Assert.True(preview.IsSuccess, preview.Message);
        return service.Export(orderId, DateTime.UtcNow, preview.SnapshotHash);
    }

    private static string ReadWorksheetXml(byte[] workbook)
    {
        using var stream = new MemoryStream(workbook);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var worksheet = archive.GetEntry("xl/worksheets/sheet1.xml")
                        ?? throw new InvalidOperationException("Worksheet is missing.");
        using var reader = new StreamReader(worksheet.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task<string> ReadCutoverStateAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM marking_cutover_state WHERE id = TRUE";
        return Convert.ToString(
                   await command.ExecuteScalarAsync(),
                   CultureInfo.InvariantCulture)
               ?? MarkingCutoverState.Shadow;
    }
}

[CollectionDefinition("Postgres marking integration", DisableParallelization = true)]
public sealed class PostgresMarkingIntegrationCollection;

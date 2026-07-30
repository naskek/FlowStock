using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletFillingCorrectionPostgresTests
{
    [Fact]
    public async Task CorrectFilled_DuplicateSourceLedgerRows_BlockWholeCorrectionWithoutPartialInversion()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await Fixture.Create(
            connectionString,
            $"BIJECTION-DUP-{Guid.NewGuid():N}");
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await Fixture.Execute(connection, @"
INSERT INTO ledger(ts, doc_id, item_id, location_id, qty_delta, hu_code, hu)
VALUES(@now, @doc_id, @item_id, @location_id, 10, @hu, @hu);",
                ("@now", DateTime.Now.ToString("O")),
                ("@doc_id", fixture.SourcePrdDocId),
                ("@item_id", fixture.ItemId),
                ("@location_id", fixture.LocationId),
                ("@hu", fixture.Hu));
        }

        var preview = new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(connectionString)).Preview(fixture.Hu);

        Assert.False(preview.CanConfirm);
        Assert.Contains(
            preview.Blockers,
            blocker => blocker.Code == ProductionPalletFillingCorrectionErrorCodes.LedgerMismatch);
        Assert.Empty(preview.LedgerInversion);
    }

    [Fact]
    public async Task CorrectFilled_MissingMixedComponentLedger_BlocksWholeCorrection()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await PartialFixture.Create(
            connectionString,
            $"BIJECTION-MISSING-{Guid.NewGuid():N}");
        await fixture.CompleteAsFilled(includeAllLedgerRows: false);

        var preview = new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(connectionString)).Preview(fixture.Hu);

        Assert.False(preview.CanConfirm);
        Assert.Contains(
            preview.Blockers,
            blocker => blocker.Code == ProductionPalletFillingCorrectionErrorCodes.LedgerMismatch);
        Assert.Empty(preview.LedgerInversion);
    }

    [Fact]
    public async Task CorrectFilled_ExtraSourceLedgerRow_BlocksWholeCorrection()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await PartialFixture.Create(
            connectionString,
            $"BIJECTION-EXTRA-{Guid.NewGuid():N}");
        await fixture.CompleteAsFilled(addExtraLedgerRow: true);

        var preview = new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(connectionString)).Preview(fixture.Hu);

        Assert.False(preview.CanConfirm);
        Assert.Contains(
            preview.Blockers,
            blocker => blocker.Code == ProductionPalletFillingCorrectionErrorCodes.LedgerMismatch);
        Assert.Empty(preview.LedgerInversion);
    }

    [Fact]
    public async Task CorrectFilled_CompleteMixedBijection_ProducesOneInversionPerComponent()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await PartialFixture.Create(
            connectionString,
            $"BIJECTION-VALID-{Guid.NewGuid():N}");
        await fixture.CompleteAsFilled();

        var preview = new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(connectionString)).Preview(fixture.Hu);

        Assert.True(preview.CanConfirm);
        Assert.Equal(2, preview.Components.Count);
        Assert.Equal(2, preview.LedgerInversion.Count);
        Assert.Equal(
            preview.Components.Select(component => component.DocLineId).OrderBy(id => id),
            preview.LedgerInversion.Select(line => line.SourceDocLineId).OrderBy(id => id));
    }

    [Fact]
    public async Task ResetPartial_AllComponentsCompleteButParentNotFilled_IsBlocked()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await PartialFixture.Create(
            connectionString,
            $"RESET-COMPLETE-{Guid.NewGuid():N}");
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await Fixture.Execute(connection, @"
UPDATE production_pallet_lines
SET filled_qty = planned_qty, filled_at = @now
WHERE production_pallet_id = @pallet_id;",
                ("@now", DateTime.Now.ToString("O")),
                ("@pallet_id", fixture.PalletId));
        }

        var preview = new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(connectionString)).Preview(fixture.Hu);

        Assert.False(preview.CanConfirm);
        Assert.Null(preview.Action);
        Assert.Contains(
            preview.Blockers,
            blocker => blocker.Code == ProductionPalletFillingCorrectionErrorCodes.CorrectionStateChanged);
    }

    [Fact]
    public async Task CorrectFilled_ThenPartialFillReplacement_ResetPartialSucceedsWithoutLedgerChanges()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await PartialFixture.Create(
            connectionString,
            $"RESET-REPLACEMENT-{Guid.NewGuid():N}");
        await fixture.CompleteAsFilled();
        var service = new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(connectionString));
        var correction = service.Confirm(new ProductionPalletFillingCorrectionConfirmRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            HuCode = fixture.Hu,
            ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
            ReasonText = "Исправить mixed HU"
        });
        Assert.True(correction.Success, correction.Message);
        Assert.NotNull(correction.ReplacementPalletId);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await Fixture.Execute(connection, @"
UPDATE production_pallet_lines
SET filled_qty = planned_qty, filled_at = @now
WHERE id = (
    SELECT id
    FROM production_pallet_lines
    WHERE production_pallet_id = @pallet_id
    ORDER BY id
    LIMIT 1
);",
            ("@now", DateTime.Now.ToString("O")),
            ("@pallet_id", correction.ReplacementPalletId!.Value));
        var ledgerBefore = await ReadLedgerSnapshot(connection, fixture.Hu);

        var reset = service.Confirm(new ProductionPalletFillingCorrectionConfirmRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            HuCode = fixture.Hu,
            ExpectedAction = ProductionPalletFillingCorrectionAction.ResetPartial,
            ReasonText = "Сбросить частичное наполнение replacement"
        });

        Assert.True(reset.Success, reset.Message);
        Assert.Equal(ProductionPalletFillingCorrectionAction.ResetPartial, reset.Action);
        Assert.Equal(ledgerBefore, await ReadLedgerSnapshot(connection, fixture.Hu));
        await using var verify = connection.CreateCommand();
        verify.CommandText = @"
SELECT p.status,
       COUNT(*) FILTER (
           WHERE ABS(pl.filled_qty) > 0.000001 OR pl.filled_at IS NOT NULL)
FROM production_pallets p
JOIN production_pallet_lines pl ON pl.production_pallet_id = p.id
WHERE p.id = @pallet_id
GROUP BY p.status;";
        verify.Parameters.AddWithValue("@pallet_id", correction.ReplacementPalletId.Value);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Contains(reader.GetString(0), new[] { ProductionPalletStatus.Planned, ProductionPalletStatus.Printed });
        Assert.Equal(0L, reader.GetInt64(1));
    }

    [Fact]
    public async Task CorrectFilled_ThenPartialFillReplacement_NonZeroCurrentLedgerBalanceBlocksReset()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await PartialFixture.Create(
            connectionString,
            $"RESET-BALANCE-{Guid.NewGuid():N}");
        await fixture.CompleteAsFilled();
        var service = new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(connectionString));
        var correction = service.Confirm(new ProductionPalletFillingCorrectionConfirmRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            HuCode = fixture.Hu,
            ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
            ReasonText = "Исправить mixed HU"
        });
        Assert.True(correction.Success, correction.Message);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await Fixture.Execute(connection, @"
UPDATE production_pallet_lines
SET filled_qty = planned_qty, filled_at = @now
WHERE id = (
    SELECT id
    FROM production_pallet_lines
    WHERE production_pallet_id = @pallet_id
    ORDER BY id
    LIMIT 1
);
WITH movement_doc AS (
    INSERT INTO docs(doc_ref, type, status, created_at, closed_at, order_id, order_ref)
    VALUES(@ref, 'INVENTORY_CORRECTION', 'CLOSED', @now, @now, @order_id, @order_ref)
    RETURNING id
)
INSERT INTO ledger(ts, doc_id, item_id, location_id, qty_delta, hu_code, hu)
SELECT @now, id, @item_id, @location_id, 1, @hu, @hu
FROM movement_doc;",
            ("@now", DateTime.Now.ToString("O")),
            ("@pallet_id", correction.ReplacementPalletId!.Value),
            ("@ref", $"BALANCE-{Guid.NewGuid():N}"),
            ("@order_id", fixture.OrderId),
            ("@order_ref", $"BALANCE-{fixture.OrderId}"),
            ("@item_id", fixture.ItemIds[0]),
            ("@location_id", fixture.LocationId),
            ("@hu", fixture.Hu));

        var preview = service.Preview(fixture.Hu);

        Assert.Equal(ProductionPalletFillingCorrectionAction.ResetPartial, preview.Action);
        Assert.False(preview.CanConfirm);
        Assert.Contains(
            preview.Blockers,
            blocker => blocker.Code == ProductionPalletFillingCorrectionErrorCodes.LedgerMismatch);
    }

    [Fact]
    public async Task CorrectFilled_ServerAssignsAdjustmentAndCorReasonCodes()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await Fixture.Create(
            connectionString,
            $"REASON-COR-{Guid.NewGuid():N}");
        var result = new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(connectionString)).Confirm(
            new ProductionPalletFillingCorrectionConfirmRequest
            {
                RequestId = Guid.NewGuid().ToString(),
                HuCode = fixture.Hu,
                ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                ReasonText = "Неверно наполнена паллета"
            });

        Assert.True(result.Success);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    (SELECT reason_code FROM production_pallet_filling_adjustments WHERE id = @adjustment_id),
    (SELECT reason_code FROM docs WHERE id = @cor_doc_id);";
        command.Parameters.AddWithValue("@adjustment_id", result.AdjustmentId!.Value);
        command.Parameters.AddWithValue("@cor_doc_id", result.CorDocId!.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("ERRONEOUS_HU_FILL", reader.GetString(0));
        Assert.Equal("ERRONEOUS_HU_FILL", reader.GetString(1));
    }

    [Fact]
    public async Task ResetPartial_ServerAssignsAdjustmentReasonCode()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await PartialFixture.Create(
            connectionString,
            $"REASON-RESET-{Guid.NewGuid():N}");
        var result = new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(connectionString)).Confirm(
            new ProductionPalletFillingCorrectionConfirmRequest
            {
                RequestId = Guid.NewGuid().ToString(),
                HuCode = fixture.Hu,
                ExpectedAction = ProductionPalletFillingCorrectionAction.ResetPartial,
                ReasonText = "Неверно отмечен компонент"
            });

        Assert.True(result.Success);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT reason_code FROM production_pallet_filling_adjustments WHERE id = @adjustment_id;";
        command.Parameters.AddWithValue("@adjustment_id", result.AdjustmentId!.Value);
        Assert.Equal("ERRONEOUS_PARTIAL_FILL", (string?)await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CorrectFilled_ReasonTextAtLimit_IsNormalizedInHashCorAndAdjustment()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await Fixture.Create(
            connectionString,
            $"REASON-LIMIT-{Guid.NewGuid():N}");
        var normalizedReason = new string('я', 1000);
        var result = new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(connectionString)).Confirm(
            new ProductionPalletFillingCorrectionConfirmRequest
            {
                RequestId = Guid.NewGuid().ToString(),
                HuCode = fixture.Hu,
                ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                ReasonText = $"  {normalizedReason}  "
            });
        Assert.True(result.Success, result.Message);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT a.reason_text, a.payload_hash, d.comment
FROM production_pallet_filling_adjustments a
JOIN docs d ON d.id = a.cor_doc_id
WHERE a.id = @adjustment_id;";
        command.Parameters.AddWithValue("@adjustment_id", result.AdjustmentId!.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(normalizedReason, reader.GetString(0));
        Assert.Equal(
            ProductionPalletFillingCorrectionService.BuildPayloadHash(
                fixture.Hu,
                ProductionPalletFillingCorrectionAction.CorrectFilled,
                normalizedReason),
            reader.GetString(1));
        Assert.Equal(normalizedReason, reader.GetString(2));
    }

    [Fact]
    public async Task Migration_DuplicateCorDocReference_IsRejectedByDatabase()
    {
        await AssertDuplicateAdjustmentReferenceRejected("cor_doc_id");
    }

    [Fact]
    public async Task Migration_DuplicateReplacementPalletReference_IsRejectedByDatabase()
    {
        await AssertDuplicateAdjustmentReferenceRejected("replacement_pallet_id");
    }

    [Fact]
    public async Task CorrectFilled_PostsCanonicalCorCreatesReplacementAndReplaysAfterBlockOff()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();

        var prefix = $"COR-IT-{Guid.NewGuid():N}";
        await using var fixture = await Fixture.Create(connectionString, prefix);
        var store = new PostgresDataStore(connectionString);
        var service = new ProductionPalletFillingCorrectionService(store);
        var requestId = Guid.NewGuid();
        var request = new ProductionPalletFillingCorrectionConfirmRequest
        {
            RequestId = requestId.ToString(),
            HuCode = fixture.Hu,
            ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
            ReasonText = "Повреждена упаковка",
            ActorName = "operator-a",
            DeviceName = "pc-a"
        };

        var preview = service.Preview(fixture.Hu);

        Assert.True(preview.CanConfirm);
        Assert.Equal(ProductionPalletFillingCorrectionAction.CorrectFilled, preview.Action);
        Assert.Single(preview.LedgerInversion);

        var result = service.Confirm(request);

        Assert.True(result.Success);
        Assert.False(result.Replay);
        Assert.NotNull(result.CorDocId);
        Assert.NotNull(result.ReplacementPalletId);
        Assert.NotEqual(fixture.SourcePalletId, result.ReplacementPalletId);
        Assert.Equal(result.ReplacementPalletId, store.GetProductionPalletByHu(fixture.Hu)?.Id);
        Assert.Equal(
            0d,
            new OrderService(store).GetOrderLineViews(fixture.OrderId).Single().QtyProduced,
            6);

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT
    (SELECT status FROM production_pallets WHERE id = @source_pallet_id),
    (SELECT status FROM production_pallets WHERE id = @replacement_pallet_id),
    (SELECT hu_code FROM production_pallets WHERE id = @replacement_pallet_id),
    (SELECT pallet_no FROM production_pallets WHERE id = @replacement_pallet_id),
    (SELECT status FROM docs WHERE id = @source_prd_doc_id),
    (SELECT qty FROM doc_lines WHERE doc_id = @cor_doc_id AND qty < 0),
    (SELECT to_location_id FROM doc_lines WHERE doc_id = @cor_doc_id AND qty < 0),
    (SELECT to_hu FROM doc_lines WHERE doc_id = @cor_doc_id AND qty < 0),
    (SELECT from_location_id FROM doc_lines WHERE doc_id = @cor_doc_id AND qty < 0),
    (SELECT from_hu FROM doc_lines WHERE doc_id = @cor_doc_id AND qty < 0),
    (SELECT SUM(qty_delta) FROM ledger WHERE doc_id = @source_prd_doc_id),
    (SELECT SUM(qty_delta) FROM ledger WHERE doc_id = @cor_doc_id),
    (SELECT COUNT(*) FROM order_receipt_plan_lines WHERE order_id = @order_id),
    (SELECT COUNT(*) FROM production_pallet_filling_adjustments WHERE request_id = @request_id),
    (SELECT COUNT(*)
     FROM production_pallet_filling_adjustment_lines
     WHERE adjustment_id = (
         SELECT id FROM production_pallet_filling_adjustments WHERE request_id = @request_id)
       AND line_kind = 'PALLET_COMPONENT'
       AND replacement_doc_line_id IS NOT NULL
       AND replacement_component_id IS NOT NULL);
";
            command.Parameters.AddWithValue("@source_pallet_id", fixture.SourcePalletId);
            command.Parameters.AddWithValue("@replacement_pallet_id", result.ReplacementPalletId!.Value);
            command.Parameters.AddWithValue("@source_prd_doc_id", fixture.SourcePrdDocId);
            command.Parameters.AddWithValue("@cor_doc_id", result.CorDocId!.Value);
            command.Parameters.AddWithValue("@order_id", fixture.OrderId);
            command.Parameters.AddWithValue("@request_id", requestId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(ProductionPalletStatus.Corrected, reader.GetString(0));
            Assert.Equal(ProductionPalletStatus.Planned, reader.GetString(1));
            Assert.Equal(fixture.Hu, reader.GetString(2));
            Assert.Equal(1, reader.GetInt32(3));
            Assert.Equal("CLOSED", reader.GetString(4));
            Assert.Equal(-10d, reader.GetDouble(5), 6);
            Assert.Equal(fixture.LocationId, reader.GetInt64(6));
            Assert.Equal(fixture.Hu, reader.GetString(7));
            Assert.True(reader.IsDBNull(8));
            Assert.True(reader.IsDBNull(9));
            Assert.Equal(10d, reader.GetDouble(10), 6);
            Assert.Equal(-10d, reader.GetDouble(11), 6);
            Assert.Equal(0L, reader.GetInt64(12));
            Assert.Equal(1L, reader.GetInt64(13));
            Assert.Equal(1L, reader.GetInt64(14));
        }

        await fixture.SetFeatureBlock(false);
        var replay = service.Confirm(new ProductionPalletFillingCorrectionConfirmRequest
        {
            RequestId = request.RequestId,
            HuCode = request.HuCode,
            ExpectedAction = request.ExpectedAction,
            ReasonText = request.ReasonText,
            ActorName = "operator-b",
            DeviceName = "pc-b"
        });

        Assert.True(replay.Success);
        Assert.True(replay.Replay);
        Assert.Equal(result.AdjustmentId, replay.AdjustmentId);
        Assert.Equal(result.ReplacementPalletId, replay.ReplacementPalletId);

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await Fixture.Execute(
                connection,
                "UPDATE production_pallets SET status = 'CANCELLED' WHERE id = @pallet_id;",
                ("@pallet_id", result.ReplacementPalletId!.Value));
        }
        Assert.Null(store.GetProductionPalletByHu(fixture.Hu));
        Assert.Single(service.History(fixture.Hu));
    }

    [Fact]
    public async Task ResetPartial_ClearsWholeMixedHuWithoutCorReplacementOrLedger()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();

        await using var fixture = await PartialFixture.Create(
            connectionString,
            $"RESET-IT-{Guid.NewGuid():N}");
        var service = new ProductionPalletFillingCorrectionService(new PostgresDataStore(connectionString));
        var preview = service.Preview(fixture.Hu);

        Assert.True(preview.CanConfirm);
        Assert.Equal(ProductionPalletFillingCorrectionAction.ResetPartial, preview.Action);

        var result = service.Confirm(new ProductionPalletFillingCorrectionConfirmRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            HuCode = fixture.Hu,
            ExpectedAction = ProductionPalletFillingCorrectionAction.ResetPartial,
            ReasonText = "Ошибочно отмечен компонент"
        });

        Assert.True(result.Success);
        Assert.Null(result.CorDocId);
        Assert.Null(result.ReplacementPalletId);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    (SELECT status FROM production_pallets WHERE id = @pallet_id),
    (SELECT COUNT(*) FROM production_pallet_lines WHERE production_pallet_id = @pallet_id AND filled_qty = 0),
    (SELECT COUNT(*) FROM production_pallet_filling_adjustment_lines
     WHERE adjustment_id = @adjustment_id AND line_kind = 'RESET_COMPONENT'),
    (SELECT COUNT(*) FROM ledger WHERE doc_id = @doc_id),
    (SELECT COUNT(*) FROM docs WHERE order_id = @order_id AND type = 'INVENTORY_CORRECTION');
";
        command.Parameters.AddWithValue("@pallet_id", fixture.PalletId);
        command.Parameters.AddWithValue("@adjustment_id", result.AdjustmentId!.Value);
        command.Parameters.AddWithValue("@doc_id", fixture.PrdDocId);
        command.Parameters.AddWithValue("@order_id", fixture.OrderId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(ProductionPalletStatus.Printed, reader.GetString(0));
        Assert.Equal(2L, reader.GetInt64(1));
        Assert.Equal(2L, reader.GetInt64(2));
        Assert.Equal(0L, reader.GetInt64(3));
        Assert.Equal(0L, reader.GetInt64(4));
    }

    [Fact]
    public async Task CorrectFilled_RollsBackCompleteAppliedMarkingSetAndWritesImmutableAudit()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();

        var prefix = $"MARK-COR-{Guid.NewGuid():N}";
        await using var fixture = await Fixture.Create(connectionString, prefix);
        var markingOrderId = Guid.NewGuid();
        var importId = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            var itemTypeId = await Fixture.Scalar(connection, @"
INSERT INTO item_types(name, code, is_active, enable_marking)
VALUES(@name, @code, TRUE, TRUE) RETURNING id;",
                ("@name", $"{prefix}-type"),
                ("@code", $"{prefix}-type"));
            await Fixture.Execute(connection, @"
UPDATE items SET item_type_id = @item_type_id, gtin = @gtin WHERE id = @item_id;
INSERT INTO marking_order(
    id, order_id, order_line_id, item_id, gtin, requested_quantity,
    request_number, status, request_status, created_at, updated_at)
VALUES(
    @marking_order_id, @order_id, @order_line_id, @item_id, @gtin, 10,
    @request_number, 'Completed', 'ExcelRequested', @now, @now);
INSERT INTO marking_code_import(
    id, original_filename, storage_path, file_hash, source_type, status,
    imported_rows, valid_code_rows, duplicate_code_rows, created_at)
VALUES(
    @import_id, @filename, @filename, @file_hash, 'TEST', 'Completed',
    10, 10, 0, @now);",
                ("@item_type_id", itemTypeId),
                ("@gtin", "04601234567890"),
                ("@item_id", fixture.ItemId),
                ("@marking_order_id", markingOrderId),
                ("@order_id", fixture.OrderId),
                ("@order_line_id", fixture.OrderLineId),
                ("@request_number", $"{prefix}-request"),
                ("@now", DateTime.Now.ToString("O")),
                ("@import_id", importId),
                ("@filename", $"{prefix}.xlsx"),
                ("@file_hash", prefix));
            for (var index = 0; index < 10; index++)
            {
                await Fixture.Execute(connection, @"
INSERT INTO marking_code(
    id, code, code_hash, gtin, marking_order_id, import_id, status,
    origin, receipt_doc_id, receipt_line_id, applied_at, created_at, updated_at)
VALUES(
    @id, @code, @hash, @gtin, @marking_order_id, @import_id, 'Applied',
    'LegacySynthetic', @doc_id, @line_id, @now, @now, @now);",
                    ("@id", Guid.NewGuid()),
                    ("@code", $"{prefix}-code-{index}"),
                    ("@hash", $"{prefix}-hash-{index}"),
                    ("@gtin", "04601234567890"),
                    ("@marking_order_id", markingOrderId),
                    ("@import_id", importId),
                    ("@doc_id", fixture.SourcePrdDocId),
                    ("@line_id", fixture.SourceDocLineId),
                    ("@now", DateTime.Now.ToString("O")));
            }
        }

        var service = new ProductionPalletFillingCorrectionService(new PostgresDataStore(connectionString));
        Assert.True(service.Preview(fixture.Hu).CanConfirm);
        var result = service.Confirm(new ProductionPalletFillingCorrectionConfirmRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            HuCode = fixture.Hu,
            ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
            ReasonText = "Перемаркировка не требуется",
            ActorName = "marking-operator",
            DeviceName = "marking-pc"
        });
        Assert.True(result.Success);

        await using (var verify = new NpgsqlConnection(connectionString))
        {
            await verify.OpenAsync();
            await using var command = verify.CreateCommand();
            command.CommandText = @"
SELECT
    COUNT(*) FILTER (
        WHERE status = 'Reserved'
          AND receipt_doc_id IS NULL
          AND receipt_line_id IS NULL
          AND applied_at IS NULL),
    (SELECT COUNT(*)
     FROM production_marking_transition_audit
     WHERE adjustment_id = @adjustment_id
       AND marking_order_id = @marking_order_id
       AND import_id = @import_id
       AND origin = 'LegacySynthetic'
       AND source_prd_doc_id = @source_prd_doc_id
       AND cor_doc_id = @cor_doc_id
       AND old_receipt_doc_id = @source_prd_doc_id
       AND old_receipt_line_id = @source_doc_line_id
       AND old_applied_at IS NOT NULL
       AND old_status = 'Applied'
       AND new_status = 'Reserved'
       AND reason_text = 'Перемаркировка не требуется'
       AND actor_name = 'marking-operator'
       AND device_name = 'marking-pc'
       AND changed_at IS NOT NULL)
FROM marking_code
WHERE marking_order_id = @marking_order_id;
";
            command.Parameters.AddWithValue("@adjustment_id", result.AdjustmentId!.Value);
            command.Parameters.AddWithValue("@marking_order_id", markingOrderId);
            command.Parameters.AddWithValue("@import_id", importId);
            command.Parameters.AddWithValue("@source_prd_doc_id", fixture.SourcePrdDocId);
            command.Parameters.AddWithValue("@cor_doc_id", result.CorDocId!.Value);
            command.Parameters.AddWithValue("@source_doc_line_id", fixture.SourceDocLineId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(10L, reader.GetInt64(0));
            Assert.Equal(10L, reader.GetInt64(1));
        }

        var store = new PostgresDataStore(connectionString);
        var documents = new DocumentService(store);
        var fill = new ProductionPalletService(
            store,
            new ProductionFillCloseService(
                store,
                documents,
                new FlowStockLedgerFlowOptions { ProductionAutoCloseOnFill = true }))
            .Fill(fixture.Hu, "integration-test", fixture.OrderId, result.ReplacementPrdDocId);
        Assert.True(fill.Success, fill.ErrorMessage);
        Assert.True(fill.PrdAutoClosed);

        await using var reapplied = new NpgsqlConnection(connectionString);
        await reapplied.OpenAsync();
        await using var reapplyCommand = reapplied.CreateCommand();
        reapplyCommand.CommandText = @"
SELECT COUNT(*)
FROM marking_code
WHERE marking_order_id = @marking_order_id
  AND status = 'Applied'
  AND receipt_doc_id = @replacement_prd_doc_id
  AND receipt_line_id IN (
      SELECT id FROM doc_lines WHERE doc_id = @replacement_prd_doc_id AND order_line_id = @order_line_id
  );
";
        reapplyCommand.Parameters.AddWithValue("@marking_order_id", markingOrderId);
        reapplyCommand.Parameters.AddWithValue("@replacement_prd_doc_id", result.ReplacementPrdDocId!.Value);
        reapplyCommand.Parameters.AddWithValue("@order_line_id", fixture.OrderLineId);
        Assert.Equal(10L, Convert.ToInt64(await reapplyCommand.ExecuteScalarAsync()));
    }

    [Theory]
    [InlineData("Reported", false, false)]
    [InlineData("Circulated", false, false)]
    [InlineData("Voided", false, false)]
    [InlineData("Quarantined", false, false)]
    [InlineData("UnexpectedLifecycle", false, false)]
    [InlineData("Applied", true, false)]
    [InlineData("Applied", false, true)]
    public async Task CorrectFilled_DownstreamMarkingStatusOrHiddenTimestampBlocksWholeCorrection(
        string status,
        bool hasReportedAt,
        bool hasIntroducedAt)
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();

        var prefix = $"MARK-BLOCK-{Guid.NewGuid():N}";
        await using var fixture = await Fixture.Create(connectionString, prefix);
        var markingOrderId = Guid.NewGuid();
        var importId = Guid.NewGuid();
        var codeId = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            var itemTypeId = await Fixture.Scalar(connection, @"
INSERT INTO item_types(name, code, is_active, enable_marking)
VALUES(@name, @code, TRUE, TRUE) RETURNING id;",
                ("@name", $"{prefix}-type"),
                ("@code", $"{prefix}-type"));
            await Fixture.Execute(connection, @"
UPDATE items SET item_type_id = @item_type_id, gtin = @gtin WHERE id = @item_id;
UPDATE order_lines SET qty_ordered = 1 WHERE id = @order_line_id;
UPDATE doc_lines SET qty = 1 WHERE id = @doc_line_id;
UPDATE production_pallets SET planned_qty = 1 WHERE id = @pallet_id;
UPDATE production_pallet_lines SET planned_qty = 1, filled_qty = 1
WHERE production_pallet_id = @pallet_id;
UPDATE ledger SET qty_delta = 1 WHERE doc_id = @doc_id;
INSERT INTO marking_order(
    id, order_id, order_line_id, item_id, gtin, requested_quantity,
    request_number, status, request_status, created_at, updated_at)
VALUES(
    @marking_order_id, @order_id, @order_line_id, @item_id, @gtin, 1,
    @request_number, 'Completed', 'ExcelRequested', @now, @now);
INSERT INTO marking_code_import(
    id, original_filename, storage_path, file_hash, source_type, status,
    imported_rows, valid_code_rows, duplicate_code_rows, created_at)
VALUES(
    @import_id, @filename, @filename, @file_hash, 'TEST', 'Completed',
    1, 1, 0, @now);
INSERT INTO marking_code(
    id, code, code_hash, gtin, marking_order_id, import_id, status,
    origin, receipt_doc_id, receipt_line_id, applied_at, reported_at,
    introduced_at, created_at, updated_at)
VALUES(
    @code_id, @code, @hash, @gtin, @marking_order_id, @import_id, @status,
    'HistoricalUnknown', @doc_id, @doc_line_id, @now, @reported_at,
    @introduced_at, @now, @now);",
                ("@item_type_id", itemTypeId),
                ("@gtin", "04601234567890"),
                ("@item_id", fixture.ItemId),
                ("@order_line_id", fixture.OrderLineId),
                ("@doc_line_id", fixture.SourceDocLineId),
                ("@pallet_id", fixture.SourcePalletId),
                ("@doc_id", fixture.SourcePrdDocId),
                ("@marking_order_id", markingOrderId),
                ("@order_id", fixture.OrderId),
                ("@request_number", $"{prefix}-request"),
                ("@now", DateTime.Now.ToString("O")),
                ("@import_id", importId),
                ("@filename", $"{prefix}.xlsx"),
                ("@file_hash", prefix),
                ("@code_id", codeId),
                ("@code", $"{prefix}-code"),
                ("@hash", $"{prefix}-hash"),
                ("@status", status),
                ("@reported_at", hasReportedAt ? DateTime.Now.ToString("O") : DBNull.Value),
                ("@introduced_at", hasIntroducedAt ? DateTime.Now.ToString("O") : DBNull.Value));
        }

        var service = new ProductionPalletFillingCorrectionService(new PostgresDataStore(connectionString));
        var preview = service.Preview(fixture.Hu);
        Assert.Contains(
            preview.Blockers,
            blocker => blocker.Code == ProductionPalletFillingCorrectionErrorCodes.MarkingRollbackBlocked);

        var result = service.Confirm(new ProductionPalletFillingCorrectionConfirmRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            HuCode = fixture.Hu,
            ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
            ReasonText = "Must be blocked"
        });
        Assert.False(result.Success);
        Assert.Equal(ProductionPalletFillingCorrectionErrorCodes.MarkingRollbackBlocked, result.ErrorCode);

        await using var verify = new NpgsqlConnection(connectionString);
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = @"
SELECT status, receipt_doc_id, receipt_line_id,
       (SELECT COUNT(*) FROM production_pallet_filling_adjustments
        WHERE source_pallet_id = @pallet_id)
FROM marking_code
WHERE id = @code_id;
";
        command.Parameters.AddWithValue("@pallet_id", fixture.SourcePalletId);
        command.Parameters.AddWithValue("@code_id", codeId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(status, reader.GetString(0));
        Assert.Equal(fixture.SourcePrdDocId, reader.GetInt64(1));
        Assert.Equal(fixture.SourceDocLineId, reader.GetInt64(2));
        Assert.Equal(0L, reader.GetInt64(3));
    }

    [Fact]
    public async Task CorrectFilled_StatusRefreshFailure_RollsBackEntireTransaction()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();

        var prefix = $"FAULT-COR-{Guid.NewGuid():N}";
        await using var fixture = await Fixture.Create(connectionString, prefix);
        var suffix = Guid.NewGuid().ToString("N");
        var functionName = $"cor_refresh_fail_{suffix}";
        var triggerName = $"cor_refresh_fail_trigger_{suffix}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await Fixture.Execute(connection, $"UPDATE orders SET status = 'DRAFT' WHERE id = @order_id;" + $@"
CREATE FUNCTION {functionName}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.id = {fixture.OrderId} THEN
        RAISE EXCEPTION 'STATUS_REFRESH_FAULT';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER {triggerName}
BEFORE UPDATE ON orders
FOR EACH ROW EXECUTE FUNCTION {functionName}();",
            ("@order_id", fixture.OrderId));

        try
        {
            var service = new ProductionPalletFillingCorrectionService(new PostgresDataStore(connectionString));
            Assert.ThrowsAny<Exception>(() => service.Confirm(new ProductionPalletFillingCorrectionConfirmRequest
            {
                RequestId = Guid.NewGuid().ToString(),
                HuCode = fixture.Hu,
                ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                ReasonText = "Fault injection"
            }));
        }
        finally
        {
            await Fixture.Execute(connection, $@"
DROP TRIGGER IF EXISTS {triggerName} ON orders;
DROP FUNCTION IF EXISTS {functionName}();");
        }

        await using var verify = connection.CreateCommand();
        verify.CommandText = @"
SELECT
    (SELECT status FROM production_pallets WHERE id = @pallet_id),
    (SELECT COUNT(*) FROM production_pallets WHERE order_id = @order_id),
    (SELECT COUNT(*) FROM docs WHERE order_id = @order_id AND type = 'INVENTORY_CORRECTION'),
    (SELECT COUNT(*) FROM production_pallet_filling_adjustments WHERE source_pallet_id = @pallet_id),
    (SELECT COUNT(*) FROM ledger WHERE doc_id <> @source_doc_id AND item_id = @item_id);
";
        verify.Parameters.AddWithValue("@pallet_id", fixture.SourcePalletId);
        verify.Parameters.AddWithValue("@order_id", fixture.OrderId);
        verify.Parameters.AddWithValue("@source_doc_id", fixture.SourcePrdDocId);
        verify.Parameters.AddWithValue("@item_id", fixture.ItemId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(ProductionPalletStatus.Filled, reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(0L, reader.GetInt64(2));
        Assert.Equal(0L, reader.GetInt64(3));
        Assert.Equal(0L, reader.GetInt64(4));
    }

    [Fact]
    public async Task ResetPartial_StatusRefreshFailure_RollsBackProgressAndAdjustment()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();

        await using var fixture = await PartialFixture.Create(
            connectionString,
            $"FAULT-RESET-{Guid.NewGuid():N}");
        var suffix = Guid.NewGuid().ToString("N");
        var functionName = $"reset_refresh_fail_{suffix}";
        var triggerName = $"reset_refresh_fail_trigger_{suffix}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await Fixture.Execute(connection, $@"
CREATE FUNCTION {functionName}() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.id = {fixture.OrderId} THEN
        RAISE EXCEPTION 'STATUS_REFRESH_FAULT';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER {triggerName}
BEFORE UPDATE ON orders
FOR EACH ROW EXECUTE FUNCTION {functionName}();");

        try
        {
            var service = new ProductionPalletFillingCorrectionService(new PostgresDataStore(connectionString));
            Assert.ThrowsAny<Exception>(() => service.Confirm(new ProductionPalletFillingCorrectionConfirmRequest
            {
                RequestId = Guid.NewGuid().ToString(),
                HuCode = fixture.Hu,
                ExpectedAction = ProductionPalletFillingCorrectionAction.ResetPartial,
                ReasonText = "Fault injection"
            }));
        }
        finally
        {
            await Fixture.Execute(connection, $@"
DROP TRIGGER IF EXISTS {triggerName} ON orders;
DROP FUNCTION IF EXISTS {functionName}();");
        }

        await using var verify = connection.CreateCommand();
        verify.CommandText = @"
SELECT
    (SELECT status FROM production_pallets WHERE id = @pallet_id),
    (SELECT COUNT(*) FROM production_pallet_lines
     WHERE production_pallet_id = @pallet_id AND filled_qty > 0),
    (SELECT COUNT(*) FROM production_pallet_filling_adjustments
     WHERE source_pallet_id = @pallet_id);
";
        verify.Parameters.AddWithValue("@pallet_id", fixture.PalletId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(ProductionPalletStatus.Printed, reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(0L, reader.GetInt64(2));
    }

    [Fact]
    public async Task CorrectFilled_ActiveReservationBlocksAndLeavesReservationUnchanged()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();

        await using var fixture = await Fixture.Create(
            connectionString,
            $"RESERVATION-COR-{Guid.NewGuid():N}");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await Fixture.Execute(connection, @"
INSERT INTO order_receipt_plan_lines(
    order_id, order_line_id, item_id, qty_planned, to_location_id, to_hu, sort_order)
VALUES(@order_id, @order_line_id, @item_id, 10, @location_id, @hu, 1);",
            ("@order_id", fixture.OrderId),
            ("@order_line_id", fixture.OrderLineId),
            ("@item_id", fixture.ItemId),
            ("@location_id", fixture.LocationId),
            ("@hu", fixture.Hu));

        var service = new ProductionPalletFillingCorrectionService(new PostgresDataStore(connectionString));
        var preview = service.Preview(fixture.Hu);
        Assert.Contains(
            preview.Blockers,
            blocker => blocker.Code == ProductionPalletFillingCorrectionErrorCodes.ActiveReservation);

        var result = service.Confirm(new ProductionPalletFillingCorrectionConfirmRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            HuCode = fixture.Hu,
            ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
            ReasonText = "Reservation blocker"
        });

        Assert.False(result.Success);
        Assert.Equal(ProductionPalletFillingCorrectionErrorCodes.ActiveReservation, result.ErrorCode);
        await using var verify = connection.CreateCommand();
        verify.CommandText = @"
SELECT
    (SELECT COUNT(*) FROM order_receipt_plan_lines
     WHERE order_id = @order_id AND to_hu = @hu),
    (SELECT status FROM production_pallets WHERE id = @pallet_id),
    (SELECT COUNT(*) FROM production_pallet_filling_adjustments
     WHERE source_pallet_id = @pallet_id);
";
        verify.Parameters.AddWithValue("@order_id", fixture.OrderId);
        verify.Parameters.AddWithValue("@hu", fixture.Hu);
        verify.Parameters.AddWithValue("@pallet_id", fixture.SourcePalletId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(ProductionPalletStatus.Filled, reader.GetString(1));
        Assert.Equal(0L, reader.GetInt64(2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CorrectFilled_ActiveDraftFromOrToHuBlocks(bool useFromHu)
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();

        await using var fixture = await Fixture.Create(
            connectionString,
            $"DRAFT-COR-{Guid.NewGuid():N}");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var draftDocId = await Fixture.Scalar(connection, @"
INSERT INTO docs(doc_ref, type, status, created_at, order_id)
VALUES(@ref, 'MOVE', 'DRAFT', @now, @order_id)
RETURNING id;",
            ("@ref", $"DRAFT-{Guid.NewGuid():N}"),
            ("@now", DateTime.Now.ToString("O")),
            ("@order_id", fixture.OrderId));
        await Fixture.Execute(connection, @"
INSERT INTO doc_lines(
    doc_id, item_id, qty, from_location_id, to_location_id, from_hu, to_hu)
VALUES(@doc_id, @item_id, 1, @location_id, @location_id, @from_hu, @to_hu);",
            ("@doc_id", draftDocId),
            ("@item_id", fixture.ItemId),
            ("@location_id", fixture.LocationId),
            ("@from_hu", useFromHu ? fixture.Hu : DBNull.Value),
            ("@to_hu", useFromHu ? DBNull.Value : fixture.Hu));

        var preview = new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(connectionString)).Preview(fixture.Hu);

        Assert.Contains(
            preview.Blockers,
            blocker => blocker.Code == ProductionPalletFillingCorrectionErrorCodes.ActiveDraftReference);
    }

    [Fact]
    public async Task DraftBlocker_IgnoresSupersededAndTombstoneRows()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();

        await using var fixture = await Fixture.Create(
            connectionString,
            $"DRAFT-HISTORY-{Guid.NewGuid():N}");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var draftDocId = await Fixture.Scalar(connection, @"
INSERT INTO docs(doc_ref, type, status, created_at, order_id)
VALUES(@ref, 'MOVE', 'DRAFT', @now, @order_id)
RETURNING id;",
            ("@ref", $"DRAFT-{Guid.NewGuid():N}"),
            ("@now", DateTime.Now.ToString("O")),
            ("@order_id", fixture.OrderId));
        var supersededId = await Fixture.Scalar(connection, @"
INSERT INTO doc_lines(
    doc_id, item_id, qty, from_location_id, to_location_id, from_hu)
VALUES(@doc_id, @item_id, 1, @location_id, @location_id, @hu)
RETURNING id;",
            ("@doc_id", draftDocId),
            ("@item_id", fixture.ItemId),
            ("@location_id", fixture.LocationId),
            ("@hu", fixture.Hu));
        await Fixture.Execute(connection, @"
INSERT INTO doc_lines(
    doc_id, item_id, qty, from_location_id, to_location_id, from_hu, replaces_line_id)
VALUES(@doc_id, @item_id, 0, @location_id, @location_id, @hu, @replaces_line_id);
INSERT INTO doc_lines(
    doc_id, item_id, qty, from_location_id, to_location_id, to_hu)
VALUES(@doc_id, @item_id, 0, @location_id, @location_id, @hu);",
            ("@doc_id", draftDocId),
            ("@item_id", fixture.ItemId),
            ("@location_id", fixture.LocationId),
            ("@hu", fixture.Hu),
            ("@replaces_line_id", supersededId));

        var preview = new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(connectionString)).Preview(fixture.Hu);

        Assert.DoesNotContain(
            preview.Blockers,
            blocker => blocker.Code == ProductionPalletFillingCorrectionErrorCodes.ActiveDraftReference);
    }

    [Fact]
    public async Task CorrectFilled_ConcurrentDifferentRequestsCreateSingleCorrectionEdge()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();

        await using var fixture = await Fixture.Create(
            connectionString,
            $"CONCURRENT-COR-{Guid.NewGuid():N}");
        ProductionPalletFillingCorrectionConfirmRequest BuildRequest() => new()
        {
            RequestId = Guid.NewGuid().ToString(),
            HuCode = fixture.Hu,
            ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
            ReasonText = "Concurrent correction"
        };

        var firstTask = Task.Run(() =>
            new ProductionPalletFillingCorrectionService(
                new PostgresDataStore(connectionString)).Confirm(BuildRequest()));
        var secondTask = Task.Run(() =>
            new ProductionPalletFillingCorrectionService(
                new PostgresDataStore(connectionString)).Confirm(BuildRequest()));
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Single(results.Where(result => result.Success));
        Assert.Single(results.Where(result => !result.Success));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    (SELECT COUNT(*) FROM production_pallet_filling_adjustments
     WHERE source_pallet_id = @source_pallet_id),
    (SELECT COUNT(*) FROM production_pallets WHERE order_id = @order_id),
    (SELECT COUNT(*) FROM docs
     WHERE order_id = @order_id AND type = 'INVENTORY_CORRECTION' AND status = 'CLOSED');
";
        command.Parameters.AddWithValue("@source_pallet_id", fixture.SourcePalletId);
        command.Parameters.AddWithValue("@order_id", fixture.OrderId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(2L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    [Fact]
    public async Task CorrectFilled_ConcurrentSameRequestAndPayload_ReturnsCommittedReplay()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await Fixture.Create(
            connectionString,
            $"CONCURRENT-SAME-REQUEST-{Guid.NewGuid():N}");
        var requestId = Guid.NewGuid().ToString();
        var gate = new ManualResetEventSlim(false);
        Task<ProductionPalletFillingCorrectionResult> Start(string applicationName) =>
            Task.Run(() =>
            {
                gate.Wait();
                var builder = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    ApplicationName = applicationName,
                    Pooling = false
                };
                return new ProductionPalletFillingCorrectionService(
                    new PostgresDataStore(builder.ConnectionString)).Confirm(
                    new ProductionPalletFillingCorrectionConfirmRequest
                    {
                        RequestId = requestId,
                        HuCode = fixture.Hu,
                        ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                        ReasonText = "Одинаковый конкурентный запрос"
                    });
            });
        var firstTask = Start($"same-request-a-{Guid.NewGuid():N}");
        var secondTask = Start($"same-request-b-{Guid.NewGuid():N}");
        gate.Set();

        var results = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.All(results, result => Assert.True(result.Success, result.Message));
        Assert.Single(results, result => !result.Replay);
        Assert.Single(results, result => result.Replay);
        Assert.Equal(results[0].AdjustmentId, results[1].AdjustmentId);
        Assert.Equal(results[0].CorDocId, results[1].CorDocId);
        Assert.Equal(results[0].ReplacementPalletId, results[1].ReplacementPalletId);
        await AssertSingleCommittedBusinessEffect(
            connectionString,
            fixture,
            Guid.Parse(requestId));
    }

    [Fact]
    public async Task CorrectFilled_ConcurrentSameRequestDifferentPayload_ReturnsIdempotencyConflict()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await Fixture.Create(
            connectionString,
            $"CONCURRENT-REUSED-REQUEST-{Guid.NewGuid():N}");
        var requestId = Guid.NewGuid();
        var gate = new ManualResetEventSlim(false);
        Task<ProductionPalletFillingCorrectionResult> Start(string reason, string applicationName) =>
            Task.Run(() =>
            {
                gate.Wait();
                var builder = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    ApplicationName = applicationName,
                    Pooling = false
                };
                return new ProductionPalletFillingCorrectionService(
                    new PostgresDataStore(builder.ConnectionString)).Confirm(
                    new ProductionPalletFillingCorrectionConfirmRequest
                    {
                        RequestId = requestId.ToString(),
                        HuCode = fixture.Hu,
                        ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                        ReasonText = reason
                    });
            });
        var firstTask = Start("Конкурентная причина A", $"reused-request-a-{Guid.NewGuid():N}");
        var secondTask = Start("Конкурентная причина B", $"reused-request-b-{Guid.NewGuid():N}");
        gate.Set();

        var results = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Single(results, result => result.Success);
        var conflict = Assert.Single(results, result => !result.Success);
        Assert.Equal(
            ProductionPalletFillingCorrectionErrorCodes.IdempotencyKeyReused,
            conflict.ErrorCode);
        await AssertSingleCommittedBusinessEffect(connectionString, fixture, requestId);
    }

    [Fact]
    public Task CorrectFilled_ConcurrentReplacementOfActiveFromHu_SerializesWithoutPartialCommit() =>
        AssertConcurrentAppendOnlyReplacement(useFromHu: true, tombstone: false);

    [Fact]
    public Task CorrectFilled_ConcurrentReplacementOfActiveToHu_SerializesWithoutPartialCommit() =>
        AssertConcurrentAppendOnlyReplacement(useFromHu: false, tombstone: false);

    [Fact]
    public Task CorrectFilled_ConcurrentTombstoneOfActiveHu_SerializesWithoutPartialCommit() =>
        AssertConcurrentAppendOnlyReplacement(useFromHu: false, tombstone: true);

    [Fact]
    public async Task CorrectFilled_ConcurrentHydratedCreateDoc_AllowsOnlyConsistentDraftOrCorrection()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await Fixture.Create(
            connectionString,
            $"CONCURRENT-CREATEDOC-{Guid.NewGuid():N}");
        await using var huBlocker = new NpgsqlConnection(connectionString);
        await huBlocker.OpenAsync();
        await using var huBlockerTx = await huBlocker.BeginTransactionAsync();
        await using (var lockHu = huBlocker.CreateCommand())
        {
            lockHu.Transaction = huBlockerTx;
            lockHu.CommandText = "SELECT pg_advisory_xact_lock(73421, hashtext(@hu_code));";
            lockHu.Parameters.AddWithValue("@hu_code", fixture.Hu);
            await lockHu.ExecuteNonQueryAsync();
        }

        var createBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"create-doc-{Guid.NewGuid():N}",
            Pooling = false
        };
        var createTask = Task.Run(() =>
        {
            var documents = new DocumentService(new PostgresDataStore(createBuilder.ConnectionString));
            return documents.CreateDoc(
                DocType.Move,
                $"MOV-{Guid.NewGuid():N}",
                "Concurrent HU reference",
                partnerId: null,
                orderRef: null,
                shippingRef: fixture.Hu,
                orderId: fixture.OrderId,
                hydrateOrderLines: true);
        });
        await WaitUntilPostgresSessionWaitsForLock(
            connectionString,
            createBuilder.ApplicationName!,
            TimeSpan.FromSeconds(10));

        var correctionTask = Task.Run(() =>
            new ProductionPalletFillingCorrectionService(
                new PostgresDataStore(connectionString)).Confirm(
                new ProductionPalletFillingCorrectionConfirmRequest
                {
                    RequestId = Guid.NewGuid().ToString(),
                    HuCode = fixture.Hu,
                    ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                    ReasonText = "Concurrent CreateDoc"
                }));
        await huBlockerTx.CommitAsync();

        var createdDocId = await createTask.WaitAsync(TimeSpan.FromSeconds(10));
        var correction = await correctionTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(correction.Success);
        Assert.Equal(
            ProductionPalletFillingCorrectionErrorCodes.ActiveDraftReference,
            correction.ErrorCode);

        await using var verify = new NpgsqlConnection(connectionString);
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = @"
SELECT
    (SELECT COUNT(*) FROM docs WHERE id = @doc_id AND status = 'DRAFT'),
    (SELECT COUNT(*) FROM doc_lines
     WHERE doc_id = @doc_id
       AND UPPER(BTRIM(COALESCE(to_hu, ''))) = @hu),
    (SELECT COUNT(*) FROM production_pallet_filling_adjustments
     WHERE source_pallet_id = @source_pallet_id);
";
        command.Parameters.AddWithValue("@doc_id", createdDocId);
        command.Parameters.AddWithValue("@hu", fixture.Hu);
        command.Parameters.AddWithValue("@source_pallet_id", fixture.SourcePalletId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(0L, reader.GetInt64(2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CorrectFilled_ConcurrentDraftAppend_FromOrToHuBlocksCorrection(bool useFromHu)
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await Fixture.Create(
            connectionString,
            $"CONCURRENT-APPEND-{useFromHu}-{Guid.NewGuid():N}");
        var setupDocuments = new DocumentService(new PostgresDataStore(connectionString));
        var docId = setupDocuments.CreateDoc(
            DocType.Move,
            $"MOV-{Guid.NewGuid():N}",
            "Concurrent append",
            partnerId: null,
            orderRef: null,
            shippingRef: null,
            orderId: fixture.OrderId,
            hydrateOrderLines: false);

        await using var huBlocker = new NpgsqlConnection(connectionString);
        await huBlocker.OpenAsync();
        await using var huBlockerTx = await huBlocker.BeginTransactionAsync();
        await LockHu(huBlocker, huBlockerTx, fixture.Hu);
        var appendBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"draft-append-{Guid.NewGuid():N}",
            Pooling = false
        };
        var appendTask = Task.Run(() =>
            new DocumentService(new PostgresDataStore(appendBuilder.ConnectionString))
                .AddDocLine(
                    docId,
                    fixture.ItemId,
                    1,
                    fixture.LocationId,
                    fixture.LocationId,
                    fromHu: useFromHu ? fixture.Hu : null,
                    toHu: useFromHu ? null : fixture.Hu,
                    orderLineId: fixture.OrderLineId,
                    productionPurpose: ProductionLinePurpose.InternalStock));
        await WaitUntilPostgresSessionWaitsForLock(
            connectionString,
            appendBuilder.ApplicationName!,
            TimeSpan.FromSeconds(10));
        var correctionTask = Task.Run(() =>
            new ProductionPalletFillingCorrectionService(
                new PostgresDataStore(connectionString)).Confirm(
                new ProductionPalletFillingCorrectionConfirmRequest
                {
                    RequestId = Guid.NewGuid().ToString(),
                    HuCode = fixture.Hu,
                    ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                    ReasonText = "Concurrent draft append"
                }));

        await huBlockerTx.CommitAsync();
        _ = await appendTask.WaitAsync(TimeSpan.FromSeconds(10));
        var correction = await correctionTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(correction.Success);
        Assert.Equal(
            ProductionPalletFillingCorrectionErrorCodes.ActiveDraftReference,
            correction.ErrorCode);
    }

    [Fact]
    public async Task CorrectFilled_ConcurrentRepeatFill_CompletesWithoutDeadlockOrDuplicateSourceEffect()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await Fixture.Create(
            connectionString,
            $"CONCURRENT-FILL-{Guid.NewGuid():N}");
        await using var huBlocker = new NpgsqlConnection(connectionString);
        await huBlocker.OpenAsync();
        await using var huBlockerTx = await huBlocker.BeginTransactionAsync();
        await LockHu(huBlocker, huBlockerTx, fixture.Hu);

        var fillBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"repeat-fill-{Guid.NewGuid():N}",
            Pooling = false
        };
        var fillTask = Task.Run(() =>
        {
            var store = new PostgresDataStore(fillBuilder.ConnectionString);
            return new ProductionPalletService(
                store,
                new ProductionFillCloseService(
                    store,
                    new DocumentService(store),
                    new FlowStockLedgerFlowOptions { ProductionAutoCloseOnFill = true }))
                .Fill(fixture.Hu, "concurrency-test", fixture.OrderId, fixture.SourcePrdDocId);
        });
        await WaitUntilPostgresSessionWaitsForLock(
            connectionString,
            fillBuilder.ApplicationName!,
            TimeSpan.FromSeconds(10));

        var correctionTask = Task.Run(() =>
            new ProductionPalletFillingCorrectionService(
                new PostgresDataStore(connectionString)).Confirm(
                new ProductionPalletFillingCorrectionConfirmRequest
                {
                    RequestId = Guid.NewGuid().ToString(),
                    HuCode = fixture.Hu,
                    ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                    ReasonText = "Concurrent repeat fill"
                }));
        await huBlockerTx.CommitAsync();

        var fill = await fillTask.WaitAsync(TimeSpan.FromSeconds(10));
        var correction = await correctionTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fill.Success, fill.ErrorMessage);
        Assert.True(fill.AlreadyFilled);
        Assert.True(correction.Success, correction.Message);
    }

    [Fact]
    public async Task ResetPartial_ConcurrentFinalMixedFill_ReturnsStateChangedWithoutDeadlock()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await PartialFixture.Create(
            connectionString,
            $"CONCURRENT-MIXED-FILL-{Guid.NewGuid():N}");
        long remainingComponentId;
        await using (var lookup = new NpgsqlConnection(connectionString))
        {
            await lookup.OpenAsync();
            await using var command = lookup.CreateCommand();
            command.CommandText = @"
SELECT id
FROM production_pallet_lines
WHERE production_pallet_id = @pallet_id
  AND filled_qty < planned_qty
ORDER BY id
LIMIT 1;";
            command.Parameters.AddWithValue("@pallet_id", fixture.PalletId);
            remainingComponentId = Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        await using var huBlocker = new NpgsqlConnection(connectionString);
        await huBlocker.OpenAsync();
        await using var huBlockerTx = await huBlocker.BeginTransactionAsync();
        await LockHu(huBlocker, huBlockerTx, fixture.Hu);
        var fillBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"mixed-fill-{Guid.NewGuid():N}",
            Pooling = false
        };
        var fillTask = Task.Run(() =>
        {
            var store = new PostgresDataStore(fillBuilder.ConnectionString);
            return new ProductionPalletService(
                store,
                new ProductionFillCloseService(
                    store,
                    new DocumentService(store),
                    new FlowStockLedgerFlowOptions { ProductionAutoCloseOnFill = true }))
                .FillMixedComponents(
                    fixture.Hu,
                    new[] { remainingComponentId },
                    "concurrency-test",
                    fixture.OrderId,
                    fixture.PrdDocId);
        });
        await WaitUntilPostgresSessionWaitsForLock(
            connectionString,
            fillBuilder.ApplicationName!,
            TimeSpan.FromSeconds(10));
        var resetTask = Task.Run(() =>
            new ProductionPalletFillingCorrectionService(
                new PostgresDataStore(connectionString)).Confirm(
                new ProductionPalletFillingCorrectionConfirmRequest
                {
                    RequestId = Guid.NewGuid().ToString(),
                    HuCode = fixture.Hu,
                    ExpectedAction = ProductionPalletFillingCorrectionAction.ResetPartial,
                    ReasonText = "Concurrent final mixed fill"
                }));

        await huBlockerTx.CommitAsync();
        var fill = await fillTask.WaitAsync(TimeSpan.FromSeconds(10));
        var reset = await resetTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fill.Success, fill.ErrorMessage);
        Assert.False(reset.Success);
        Assert.Equal(
            ProductionPalletFillingCorrectionErrorCodes.CorrectionStateChanged,
            reset.ErrorCode);
    }

    [Theory]
    [InlineData("CREATE")]
    [InlineData("REPLACE")]
    [InlineData("DELETE")]
    public async Task CorrectFilled_ConcurrentReservationMutation_IsSerializedByOrderAndHuLocks(
        string mutation)
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await Fixture.Create(
            connectionString,
            $"CONCURRENT-RES-{mutation}-{Guid.NewGuid():N}");
        var seedStore = new PostgresDataStore(connectionString);
        if (mutation == "REPLACE")
        {
            seedStore.ReplaceOrderReceiptPlanLines(
                fixture.OrderId,
                new[]
                {
                    new OrderReceiptPlanLine
                    {
                        OrderId = fixture.OrderId,
                        OrderLineId = fixture.OrderLineId,
                        ItemId = fixture.ItemId,
                        QtyPlanned = 10,
                        ToLocationId = fixture.LocationId,
                        ToHu = $"OTHER-{Guid.NewGuid():N}"
                    }
                });
        }
        else if (mutation == "DELETE")
        {
            seedStore.ReplaceOrderReceiptPlanLines(
                fixture.OrderId,
                new[]
                {
                    new OrderReceiptPlanLine
                    {
                        OrderId = fixture.OrderId,
                        OrderLineId = fixture.OrderLineId,
                        ItemId = fixture.ItemId,
                        QtyPlanned = 10,
                        ToLocationId = fixture.LocationId,
                        ToHu = fixture.Hu
                    }
                });
        }

        await using var huBlocker = new NpgsqlConnection(connectionString);
        await huBlocker.OpenAsync();
        await using var huBlockerTx = await huBlocker.BeginTransactionAsync();
        await LockHu(huBlocker, huBlockerTx, fixture.Hu);
        var mutationBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"reservation-{mutation}-{Guid.NewGuid():N}",
            Pooling = false
        };
        var mutationTask = Task.Run(() =>
        {
            var store = new PostgresDataStore(mutationBuilder.ConnectionString);
            var replacement = mutation == "DELETE"
                ? Array.Empty<OrderReceiptPlanLine>()
                : new[]
                {
                    new OrderReceiptPlanLine
                    {
                        OrderId = fixture.OrderId,
                        OrderLineId = fixture.OrderLineId,
                        ItemId = fixture.ItemId,
                        QtyPlanned = 10,
                        ToLocationId = fixture.LocationId,
                        ToHu = fixture.Hu
                    }
                };
            store.ReplaceOrderReceiptPlanLines(fixture.OrderId, replacement);
        });
        await WaitUntilPostgresSessionWaitsForLock(
            connectionString,
            mutationBuilder.ApplicationName!,
            TimeSpan.FromSeconds(10));
        var correctionTask = Task.Run(() =>
            new ProductionPalletFillingCorrectionService(
                new PostgresDataStore(connectionString)).Confirm(
                new ProductionPalletFillingCorrectionConfirmRequest
                {
                    RequestId = Guid.NewGuid().ToString(),
                    HuCode = fixture.Hu,
                    ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                    ReasonText = $"Concurrent reservation {mutation}"
                }));

        await huBlockerTx.CommitAsync();
        await mutationTask.WaitAsync(TimeSpan.FromSeconds(10));
        var correction = await correctionTask.WaitAsync(TimeSpan.FromSeconds(10));
        if (mutation == "DELETE")
        {
            Assert.True(correction.Success, correction.Message);
        }
        else
        {
            Assert.False(correction.Success);
            Assert.Equal(
                ProductionPalletFillingCorrectionErrorCodes.ActiveReservation,
                correction.ErrorCode);
        }
    }

    [Theory]
    [InlineData(DocType.Outbound)]
    [InlineData(DocType.Move)]
    [InlineData(DocType.WriteOff)]
    [InlineData(DocType.InventoryCorrection)]
    public async Task CorrectFilled_ConcurrentCanonicalPosting_SeesCommittedHuMovement(
        DocType docType)
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await Fixture.Create(
            connectionString,
            $"CONCURRENT-POST-{docType}-{Guid.NewGuid():N}");
        var setupStore = new PostgresDataStore(connectionString);
        var setupDocuments = new DocumentService(setupStore);
        var outboundPartnerId = docType == DocType.Outbound
            ? setupStore.AddPartner(new Partner
            {
                Name = "Concurrent posting customer",
                Code = fixture.Hu,
                CreatedAt = DateTime.Now
            })
            : (long?)null;
        var docId = setupDocuments.CreateDoc(
            docType,
            $"{docType}-{Guid.NewGuid():N}",
            "Concurrent posting",
            partnerId: outboundPartnerId,
            orderRef: null,
            shippingRef: null,
            orderId: docType == DocType.Outbound ? null : fixture.OrderId,
            hydrateOrderLines: false);
        if (docType == DocType.WriteOff)
        {
            setupDocuments.UpdateDocReason(docId, "TEST_COR_CONCURRENCY");
        }
        setupDocuments.AddDocLine(
            docId,
            fixture.ItemId,
            docType == DocType.InventoryCorrection ? -1 : 1,
            fromLocationId: docType is DocType.Outbound or DocType.Move or DocType.WriteOff
                ? fixture.LocationId
                : null,
            toLocationId: docType is DocType.Move or DocType.InventoryCorrection
                ? fixture.LocationId
                : null,
            fromHu: docType is DocType.Outbound or DocType.Move or DocType.WriteOff
                ? fixture.Hu
                : null,
            toHu: docType is DocType.Move or DocType.InventoryCorrection
                ? fixture.Hu
                : null,
            orderLineId: docType == DocType.Outbound ? null : fixture.OrderLineId,
            productionPurpose: docType == DocType.Outbound
                ? ProductionLinePurpose.InternalStock
                : ProductionLinePurpose.CustomerOrder);

        await using var huBlocker = new NpgsqlConnection(connectionString);
        await huBlocker.OpenAsync();
        await using var huBlockerTx = await huBlocker.BeginTransactionAsync();
        await LockHu(huBlocker, huBlockerTx, fixture.Hu);
        var postingBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"posting-{docType}-{Guid.NewGuid():N}",
            Pooling = false
        };
        var postingTask = Task.Run(() =>
            new DocumentService(new PostgresDataStore(postingBuilder.ConnectionString))
                .TryCloseDoc(docId, allowNegative: true));
        var waitForPostingLock = WaitUntilPostgresSessionWaitsForLock(
            connectionString,
            postingBuilder.ApplicationName!,
            TimeSpan.FromSeconds(10));
        var postingState = await Task.WhenAny(postingTask, waitForPostingLock);
        if (postingState == postingTask)
        {
            var earlyPosting = await postingTask;
            Assert.Fail(
                "Posting завершился до ожидания HU-lock: "
                + string.Join(" | ", earlyPosting.Errors.Concat(earlyPosting.Warnings)));
        }
        await waitForPostingLock;
        var correctionTask = Task.Run(() =>
            new ProductionPalletFillingCorrectionService(
                new PostgresDataStore(connectionString)).Confirm(
                new ProductionPalletFillingCorrectionConfirmRequest
                {
                    RequestId = Guid.NewGuid().ToString(),
                    HuCode = fixture.Hu,
                    ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                    ReasonText = $"Concurrent posting {docType}"
                }));

        await huBlockerTx.CommitAsync();
        var posting = await postingTask.WaitAsync(TimeSpan.FromSeconds(10));
        var correction = await correctionTask.WaitAsync(TimeSpan.FromSeconds(10));
        if (docType == DocType.Outbound)
        {
            await using var attachForFixtureCleanup = new NpgsqlConnection(connectionString);
            await attachForFixtureCleanup.OpenAsync();
            await Fixture.Execute(
                attachForFixtureCleanup,
                "UPDATE docs SET order_id = @order_id WHERE id = @doc_id;",
                ("@order_id", fixture.OrderId),
                ("@doc_id", docId));
        }
        Assert.True(posting.Success, string.Join(" | ", posting.Errors.Concat(posting.Warnings)));
        Assert.False(correction.Success);
        Assert.Contains(
            correction.ErrorCode,
            new[]
            {
                ProductionPalletFillingCorrectionErrorCodes.LaterLedgerMovement,
                ProductionPalletFillingCorrectionErrorCodes.LedgerMismatch
            });
    }

    [Fact]
    public async Task CorrectFilled_ConcurrentOrderControlCreate_SeesActiveControl()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await Fixture.Create(
            connectionString,
            $"CONCURRENT-CONTROL-{Guid.NewGuid():N}");
        await using (var makeEligible = new NpgsqlConnection(connectionString))
        {
            await makeEligible.OpenAsync();
            await Fixture.Execute(makeEligible, @"
UPDATE orders
SET order_type = 'CUSTOMER', status = 'ACCEPTED'
WHERE id = @order_id;",
                ("@order_id", fixture.OrderId));
        }
        Assert.True(
            new OrderControlService(new PostgresDataStore(connectionString))
                .Preview(new[] { fixture.OrderId }).CanCreate);

        await using var huBlocker = new NpgsqlConnection(connectionString);
        await huBlocker.OpenAsync();
        await using var huBlockerTx = await huBlocker.BeginTransactionAsync();
        await LockHu(huBlocker, huBlockerTx, fixture.Hu);
        var controlBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"order-control-{Guid.NewGuid():N}",
            Pooling = false
        };
        var controlTask = Task.Run(() =>
            new OrderControlService(new PostgresDataStore(controlBuilder.ConnectionString))
                .Create(new[] { fixture.OrderId }, "concurrency-test", "HU correction race"));
        await WaitUntilPostgresSessionWaitsForLock(
            connectionString,
            controlBuilder.ApplicationName!,
            TimeSpan.FromSeconds(10));
        var correctionTask = Task.Run(() =>
            new ProductionPalletFillingCorrectionService(
                new PostgresDataStore(connectionString)).Confirm(
                new ProductionPalletFillingCorrectionConfirmRequest
                {
                    RequestId = Guid.NewGuid().ToString(),
                    HuCode = fixture.Hu,
                    ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                    ReasonText = "Concurrent order control"
                }));

        await huBlockerTx.CommitAsync();
        var control = await controlTask.WaitAsync(TimeSpan.FromSeconds(10));
        var correction = await correctionTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(control.Success, control.Message);
        Assert.False(correction.Success);
        Assert.Equal(
            ProductionPalletFillingCorrectionErrorCodes.ActiveOrderControl,
            correction.ErrorCode);
    }

    [Fact]
    public async Task CorrectFilled_ConcurrentMarkingTransition_BlocksWholeRollback()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await Fixture.Create(
            connectionString,
            $"CONCURRENT-MARKING-{Guid.NewGuid():N}");
        var codeIds = await fixture.SeedAppliedMarkingCodes(10);

        await using var transition = new NpgsqlConnection(connectionString);
        await transition.OpenAsync();
        await using var transitionTx = await transition.BeginTransactionAsync();
        await using (var update = transition.CreateCommand())
        {
            update.Transaction = transitionTx;
            update.CommandText = @"
UPDATE marking_code
SET status = 'Reported', reported_at = @now, updated_at = @now
WHERE id = ANY(@ids);";
            update.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
            update.Parameters.AddWithValue("@ids", codeIds.ToArray());
            Assert.Equal(10, await update.ExecuteNonQueryAsync());
        }

        var correctionBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"marking-correction-{Guid.NewGuid():N}",
            Pooling = false
        };
        var correctionTask = Task.Run(() =>
            new ProductionPalletFillingCorrectionService(
                new PostgresDataStore(correctionBuilder.ConnectionString)).Confirm(
                new ProductionPalletFillingCorrectionConfirmRequest
                {
                    RequestId = Guid.NewGuid().ToString(),
                    HuCode = fixture.Hu,
                    ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                    ReasonText = "Concurrent marking transition"
                }));
        await WaitUntilPostgresSessionWaitsForLock(
            connectionString,
            correctionBuilder.ApplicationName!,
            TimeSpan.FromSeconds(10));
        await transitionTx.CommitAsync();
        var correction = await correctionTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(correction.Success);
        Assert.Equal(
            ProductionPalletFillingCorrectionErrorCodes.MarkingRollbackBlocked,
            correction.ErrorCode);
        await using var verify = new NpgsqlConnection(connectionString);
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = @"
SELECT
    (SELECT COUNT(*) FROM marking_code WHERE id = ANY(@ids) AND status = 'Reported'),
    (SELECT COUNT(*) FROM production_pallet_filling_adjustments
     WHERE source_pallet_id = @source_pallet_id);";
        command.Parameters.AddWithValue("@ids", codeIds.ToArray());
        command.Parameters.AddWithValue("@source_pallet_id", fixture.SourcePalletId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(10L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
    }

    [Fact]
    public async Task CorrectFilled_ExistingTargetPrdAssignsNextCanonicalPalletNumber()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();

        await using var fixture = await Fixture.Create(
            connectionString,
            $"NUMBERING-COR-{Guid.NewGuid():N}");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var targetDocId = await Fixture.Scalar(connection, @"
INSERT INTO docs(doc_ref, type, status, created_at, order_id, order_ref)
VALUES(@ref, 'PRODUCTION_RECEIPT', 'DRAFT', @now, @order_id, @order_ref)
RETURNING id;",
            ("@ref", $"TARGET-{Guid.NewGuid():N}"),
            ("@now", DateTime.Now.ToString("O")),
            ("@order_id", fixture.OrderId),
            ("@order_ref", $"ORDER-{fixture.OrderId}"));
        var targetLineId = await Fixture.Scalar(connection, @"
INSERT INTO doc_lines(
    doc_id, order_line_id, production_purpose, item_id, qty,
    to_location_id, to_hu, pack_single_hu)
VALUES(
    @doc_id, @order_line_id, 'INTERNAL_STOCK', @item_id, 5,
    @location_id, @hu, TRUE)
RETURNING id;",
            ("@doc_id", targetDocId),
            ("@order_line_id", fixture.OrderLineId),
            ("@item_id", fixture.ItemId),
            ("@location_id", fixture.LocationId),
            ("@hu", $"OTHER-{Guid.NewGuid():N}".ToUpperInvariant()));
        var existingPalletId = await Fixture.Scalar(connection, @"
INSERT INTO production_pallets(
    prd_doc_id, doc_line_id, order_id, order_line_id, item_id, hu_code,
    planned_qty, to_location_id, status, pallet_no, pallet_count, created_at)
SELECT
    @doc_id, @doc_line_id, @order_id, @order_line_id, @item_id, to_hu,
    5, @location_id, 'PLANNED', 7, 7, @now
FROM doc_lines
WHERE id = @doc_line_id
RETURNING id;",
            ("@doc_id", targetDocId),
            ("@doc_line_id", targetLineId),
            ("@order_id", fixture.OrderId),
            ("@order_line_id", fixture.OrderLineId),
            ("@item_id", fixture.ItemId),
            ("@location_id", fixture.LocationId),
            ("@now", DateTime.Now.ToString("O")));
        await Fixture.Execute(connection, @"
INSERT INTO production_pallet_lines(
    production_pallet_id, doc_line_id, order_line_id, item_id,
    planned_qty, filled_qty, created_at)
VALUES(@pallet_id, @doc_line_id, @order_line_id, @item_id, 5, 0, @now);",
            ("@pallet_id", existingPalletId),
            ("@doc_line_id", targetLineId),
            ("@order_line_id", fixture.OrderLineId),
            ("@item_id", fixture.ItemId),
            ("@now", DateTime.Now.ToString("O")));

        var result = new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(connectionString)).Confirm(
            new ProductionPalletFillingCorrectionConfirmRequest
            {
                RequestId = Guid.NewGuid().ToString(),
                HuCode = fixture.Hu,
                ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                ReasonText = "Numbering check"
            });

        Assert.True(result.Success);
        Assert.Equal(targetDocId, result.ReplacementPrdDocId);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, pallet_no, pallet_count
FROM production_pallets
WHERE prd_doc_id = @doc_id
ORDER BY id;
";
        command.Parameters.AddWithValue("@doc_id", targetDocId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(existingPalletId, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(result.ReplacementPalletId, reader.GetInt64(0));
        Assert.Equal(2, reader.GetInt32(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.False(await reader.ReadAsync());
    }

    [Theory]
    [InlineData("PRINTED", 2d, false)]
    [InlineData("FILLED", 5d, true)]
    public async Task CorrectFilled_InconsistentTargetPrdIsNotSelected(
        string targetStatus,
        double targetFilledQty,
        bool targetFilledAt)
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await Fixture.Create(
            connectionString,
            $"TARGET-INCOMPAT-{targetStatus}-{Guid.NewGuid():N}");
        var incompatibleDocId = await fixture.CreateTargetPrd(
            targetStatus,
            targetFilledQty,
            targetFilledAt);

        var result = new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(connectionString)).Confirm(
            new ProductionPalletFillingCorrectionConfirmRequest
            {
                RequestId = Guid.NewGuid().ToString(),
                HuCode = fixture.Hu,
                ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                ReasonText = "Target compatibility"
            });

        Assert.True(result.Success);
        Assert.NotEqual(incompatibleDocId, result.ReplacementPrdDocId);
    }

    [Fact]
    public async Task CorrectFilled_TargetPrdCompatibilityIsRecheckedAfterDocumentLock()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await Fixture.Create(
            connectionString,
            $"TARGET-RECHECK-{Guid.NewGuid():N}");
        var targetDocId = await fixture.CreateTargetPrd(
            ProductionPalletStatus.Planned,
            componentFilledQty: 0,
            setFilledAt: false);

        var blockingBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"target-recheck-blocker-{Guid.NewGuid():N}",
            Pooling = false
        };
        await using var blocker = new NpgsqlConnection(blockingBuilder.ConnectionString);
        await blocker.OpenAsync();
        await using var blockerTx = await blocker.BeginTransactionAsync();
        await using (var mutate = blocker.CreateCommand())
        {
            mutate.Transaction = blockerTx;
            mutate.CommandText = @"
SELECT id FROM docs WHERE id = @doc_id FOR UPDATE;
UPDATE production_pallet_lines
SET filled_qty = 1, filled_at = @now
WHERE production_pallet_id IN (
    SELECT id FROM production_pallets WHERE prd_doc_id = @doc_id
);";
            mutate.Parameters.AddWithValue("@doc_id", targetDocId);
            mutate.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
            await mutate.ExecuteNonQueryAsync();
        }

        var correctionBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"target-recheck-correction-{Guid.NewGuid():N}",
            Pooling = false
        };
        var correctionTask = Task.Run(() =>
            new ProductionPalletFillingCorrectionService(
                new PostgresDataStore(correctionBuilder.ConnectionString)).Confirm(
                new ProductionPalletFillingCorrectionConfirmRequest
                {
                    RequestId = Guid.NewGuid().ToString(),
                    HuCode = fixture.Hu,
                    ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                    ReasonText = "Target lock recheck"
                }));

        await WaitUntilPostgresSessionWaitsForLock(
            connectionString,
            correctionBuilder.ApplicationName!,
            TimeSpan.FromSeconds(10));
        await blockerTx.CommitAsync();
        var result = await correctionTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Success);
        Assert.Equal(
            ProductionPalletFillingCorrectionErrorCodes.CorrectionStateChanged,
            result.ErrorCode);
    }

    [Fact]
    public async Task CorrectFilled_ReplacementCanBeFilledAndCorrectedAgainAsRevisionChain()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();

        await using var fixture = await Fixture.Create(
            connectionString,
            $"CHAIN-COR-{Guid.NewGuid():N}");
        var store = new PostgresDataStore(connectionString);
        var correction = new ProductionPalletFillingCorrectionService(store);
        var first = correction.Confirm(new ProductionPalletFillingCorrectionConfirmRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            HuCode = fixture.Hu,
            ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
            ReasonText = "First correction"
        });
        Assert.True(first.Success);

        var documents = new DocumentService(store);
        var fillClose = new ProductionFillCloseService(
            store,
            documents,
            new FlowStockLedgerFlowOptions { ProductionAutoCloseOnFill = true });
        var fill = new ProductionPalletService(store, fillClose).Fill(
            fixture.Hu,
            "integration-test",
            fixture.OrderId,
            first.ReplacementPrdDocId);
        Assert.True(fill.Success, fill.ErrorMessage);
        Assert.True(fill.PrdAutoClosed);

        var second = correction.Confirm(new ProductionPalletFillingCorrectionConfirmRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            HuCode = fixture.Hu,
            ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
            ReasonText = "Second correction"
        });
        Assert.True(second.Success, $"{second.ErrorCode}: {second.Message}");
        Assert.NotEqual(first.ReplacementPalletId, second.ReplacementPalletId);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT COUNT(*),
       COUNT(DISTINCT root_pallet_id),
       COUNT(*) FILTER (WHERE predecessor_adjustment_id IS NOT NULL)
FROM production_pallet_filling_adjustments
WHERE root_pallet_id = @root_pallet_id
  AND action_type = 'CORRECT_FILLED'
  AND result_json IS NOT NULL;
";
        command.Parameters.AddWithValue("@root_pallet_id", fixture.SourcePalletId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    [Fact]
    public async Task CorrectFilled_IndependentHistoricalChainWithSameHu_DoesNotBecomePredecessor()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var firstFixture = await Fixture.Create(
            connectionString,
            $"CHAIN-FIRST-{Guid.NewGuid():N}");
        var first = new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(connectionString)).Confirm(
            new ProductionPalletFillingCorrectionConfirmRequest
            {
                RequestId = Guid.NewGuid().ToString(),
                HuCode = firstFixture.Hu,
                ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                ReasonText = "Independent first chain"
            });
        Assert.True(first.Success);

        await using var secondFixture = await Fixture.Create(
            connectionString,
            $"CHAIN-SECOND-{Guid.NewGuid():N}");
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await Fixture.Execute(connection, @"
UPDATE production_pallets SET status = 'CANCELLED' WHERE id = @first_replacement_id;
UPDATE docs SET status = 'CLOSED', closed_at = @now WHERE id = @first_replacement_doc_id;
UPDATE production_pallets SET hu_code = @shared_hu WHERE id = @second_source_id;
UPDATE doc_lines SET to_hu = @shared_hu WHERE doc_id = @second_doc_id;
UPDATE docs SET shipping_ref = @shared_hu WHERE id = @second_doc_id;
UPDATE ledger SET hu_code = @shared_hu, hu = @shared_hu WHERE doc_id = @second_doc_id;",
                ("@first_replacement_id", first.ReplacementPalletId!.Value),
                ("@first_replacement_doc_id", first.ReplacementPrdDocId!.Value),
                ("@now", DateTime.Now.ToString("O")),
                ("@shared_hu", firstFixture.Hu),
                ("@second_source_id", secondFixture.SourcePalletId),
                ("@second_doc_id", secondFixture.SourcePrdDocId));
        }

        var second = new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(connectionString)).Confirm(
            new ProductionPalletFillingCorrectionConfirmRequest
            {
                RequestId = Guid.NewGuid().ToString(),
                HuCode = firstFixture.Hu,
                ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                ReasonText = "Independent second chain"
            });

        Assert.True(second.Success, $"{second.ErrorCode}: {second.Message}");
        await using var verify = new NpgsqlConnection(connectionString);
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = @"
SELECT root_pallet_id, predecessor_adjustment_id
FROM production_pallet_filling_adjustments
WHERE id = @adjustment_id;";
        command.Parameters.AddWithValue("@adjustment_id", second.AdjustmentId!.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(secondFixture.SourcePalletId, reader.GetInt64(0));
        Assert.True(reader.IsDBNull(1));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly string _prefix;

        private Fixture(
            string connectionString,
            string prefix,
            string hu,
            long itemId,
            long locationId,
            long orderId,
            long orderLineId,
            long sourcePrdDocId,
            long sourceDocLineId,
            long sourcePalletId)
        {
            _connectionString = connectionString;
            _prefix = prefix;
            Hu = hu;
            ItemId = itemId;
            LocationId = locationId;
            OrderId = orderId;
            OrderLineId = orderLineId;
            SourcePrdDocId = sourcePrdDocId;
            SourceDocLineId = sourceDocLineId;
            SourcePalletId = sourcePalletId;
        }

        public string Hu { get; }
        public long ItemId { get; }
        public long LocationId { get; }
        public long OrderId { get; }
        public long OrderLineId { get; }
        public long SourcePrdDocId { get; }
        public long SourceDocLineId { get; }
        public long SourcePalletId { get; }

        public static async Task<Fixture> Create(string connectionString, string prefix)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            var now = DateTime.Now.ToString("O");
            var hu = $"HU-{prefix}".ToUpperInvariant();

            var itemTypeId = await Scalar(connection, @"
SELECT id FROM item_types WHERE code = 'GENERAL' ORDER BY id LIMIT 1;");
            var itemId = await Scalar(connection, @"
INSERT INTO items(name, barcode, base_uom, item_type_id, is_active)
VALUES(@name, @barcode, 'шт', @item_type_id, TRUE)
RETURNING id;",
                ("@name", $"{prefix}-item"),
                ("@barcode", $"{prefix}-barcode"),
                ("@item_type_id", itemTypeId));
            var locationId = await Scalar(connection, @"
INSERT INTO locations(code, name) VALUES(@code, @name) RETURNING id;",
                ("@code", $"{prefix}-loc"),
                ("@name", $"{prefix}-location"));
            var orderId = await Scalar(connection, @"
INSERT INTO orders(order_ref, order_type, status, created_at)
VALUES(@ref, 'INTERNAL', 'IN_PROGRESS', @now)
RETURNING id;",
                ("@ref", $"{prefix}-order"),
                ("@now", now));
            var orderLineId = await Scalar(connection, @"
INSERT INTO order_lines(order_id, item_id, qty_ordered, production_purpose)
VALUES(@order_id, @item_id, 10, 'INTERNAL_STOCK')
RETURNING id;",
                ("@order_id", orderId),
                ("@item_id", itemId));
            var sourceDocId = await Scalar(connection, @"
INSERT INTO docs(doc_ref, type, status, created_at, closed_at, order_id, order_ref, shipping_ref)
VALUES(@ref, 'PRODUCTION_RECEIPT', 'CLOSED', @now, @now, @order_id, @order_ref, @hu)
RETURNING id;",
                ("@ref", $"{prefix}-PRD"),
                ("@now", now),
                ("@order_id", orderId),
                ("@order_ref", $"{prefix}-order"),
                ("@hu", hu));
            var sourceLineId = await Scalar(connection, @"
INSERT INTO doc_lines(
    doc_id, order_line_id, production_purpose, item_id, qty,
    to_location_id, to_hu, pack_single_hu)
VALUES(@doc_id, @order_line_id, 'INTERNAL_STOCK', @item_id, 10, @location_id, @hu, TRUE)
RETURNING id;",
                ("@doc_id", sourceDocId),
                ("@order_line_id", orderLineId),
                ("@item_id", itemId),
                ("@location_id", locationId),
                ("@hu", hu));
            var palletId = await Scalar(connection, @"
INSERT INTO production_pallets(
    prd_doc_id, doc_line_id, order_id, order_line_id, item_id, hu_code,
    planned_qty, to_location_id, status, pallet_no, pallet_count,
    filled_at, created_at)
VALUES(
    @doc_id, @doc_line_id, @order_id, @order_line_id, @item_id, @hu,
    10, @location_id, 'FILLED', 1, 1, @now, @now)
RETURNING id;",
                ("@doc_id", sourceDocId),
                ("@doc_line_id", sourceLineId),
                ("@order_id", orderId),
                ("@order_line_id", orderLineId),
                ("@item_id", itemId),
                ("@hu", hu),
                ("@location_id", locationId),
                ("@now", now));
            await Execute(connection, @"
INSERT INTO production_pallet_lines(
    production_pallet_id, doc_line_id, order_line_id, item_id,
    planned_qty, filled_qty, filled_at, created_at)
VALUES(@pallet_id, @doc_line_id, @order_line_id, @item_id, 10, 10, @now, @now);
INSERT INTO ledger(ts, doc_id, item_id, location_id, qty_delta, hu_code, hu)
VALUES(@now, @doc_id, @item_id, @location_id, 10, @hu, @hu);
UPDATE client_blocks SET is_enabled = TRUE, updated_at = @now
WHERE block_key = 'pc_hu_correction';",
                ("@pallet_id", palletId),
                ("@doc_line_id", sourceLineId),
                ("@order_line_id", orderLineId),
                ("@item_id", itemId),
                ("@location_id", locationId),
                ("@doc_id", sourceDocId),
                ("@hu", hu),
                ("@now", now));

            return new Fixture(
                connectionString,
                prefix,
                hu,
                itemId,
                locationId,
                orderId,
                orderLineId,
                sourceDocId,
                sourceLineId,
                palletId);
        }

        public async Task SetFeatureBlock(bool enabled)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await Execute(
                connection,
                "UPDATE client_blocks SET is_enabled = @enabled WHERE block_key = 'pc_hu_correction';",
                ("@enabled", enabled));
        }

        public async Task<long> CreateTargetPrd(
            string palletStatus,
            double componentFilledQty,
            bool setFilledAt)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            var now = DateTime.Now.ToString("O");
            var docId = await Scalar(connection, @"
INSERT INTO docs(doc_ref, type, status, created_at, order_id, order_ref)
VALUES(@ref, 'PRODUCTION_RECEIPT', 'DRAFT', @now, @order_id, @order_ref)
RETURNING id;",
                ("@ref", $"{_prefix}-target-{Guid.NewGuid():N}"),
                ("@now", now),
                ("@order_id", OrderId),
                ("@order_ref", $"{_prefix}-order"));
            var targetHu = $"TARGET-{Guid.NewGuid():N}".ToUpperInvariant();
            var lineId = await Scalar(connection, @"
INSERT INTO doc_lines(
    doc_id, order_line_id, production_purpose, item_id, qty,
    to_location_id, to_hu, pack_single_hu)
VALUES(
    @doc_id, @order_line_id, 'INTERNAL_STOCK', @item_id, 5,
    @location_id, @hu, TRUE)
RETURNING id;",
                ("@doc_id", docId),
                ("@order_line_id", OrderLineId),
                ("@item_id", ItemId),
                ("@location_id", LocationId),
                ("@hu", targetHu));
            var palletId = await Scalar(connection, @"
INSERT INTO production_pallets(
    prd_doc_id, doc_line_id, order_id, order_line_id, item_id, hu_code,
    planned_qty, to_location_id, status, pallet_no, pallet_count,
    filled_at, created_at)
VALUES(
    @doc_id, @line_id, @order_id, @order_line_id, @item_id, @hu,
    5, @location_id, @status, 1, 1, @filled_at, @now)
RETURNING id;",
                ("@doc_id", docId),
                ("@line_id", lineId),
                ("@order_id", OrderId),
                ("@order_line_id", OrderLineId),
                ("@item_id", ItemId),
                ("@hu", targetHu),
                ("@location_id", LocationId),
                ("@status", palletStatus),
                ("@filled_at", setFilledAt ? now : DBNull.Value),
                ("@now", now));
            await Execute(connection, @"
INSERT INTO production_pallet_lines(
    production_pallet_id, doc_line_id, order_line_id, item_id,
    planned_qty, filled_qty, filled_at, created_at)
VALUES(
    @pallet_id, @line_id, @order_line_id, @item_id,
    5, @filled_qty, @filled_at, @now);",
                ("@pallet_id", palletId),
                ("@line_id", lineId),
                ("@order_line_id", OrderLineId),
                ("@item_id", ItemId),
                ("@filled_qty", componentFilledQty),
                ("@filled_at", setFilledAt || componentFilledQty > 0 ? now : DBNull.Value),
                ("@now", now));
            return docId;
        }

        public async Task<IReadOnlyList<Guid>> SeedAppliedMarkingCodes(int quantity)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            var now = DateTime.Now.ToString("O");
            var markingOrderId = Guid.NewGuid();
            var importId = Guid.NewGuid();
            var itemTypeId = await Scalar(connection, @"
INSERT INTO item_types(name, code, is_active, enable_marking)
VALUES(@name, @code, TRUE, TRUE) RETURNING id;",
                ("@name", $"{_prefix}-type"),
                ("@code", $"{_prefix}-type"));
            await Execute(connection, @"
UPDATE items SET item_type_id = @item_type_id, gtin = @gtin WHERE id = @item_id;
INSERT INTO marking_order(
    id, order_id, order_line_id, item_id, gtin, requested_quantity,
    request_number, status, request_status, created_at, updated_at)
VALUES(
    @marking_order_id, @order_id, @order_line_id, @item_id, @gtin, @quantity,
    @request_number, 'Completed', 'ExcelRequested', @now, @now);
INSERT INTO marking_code_import(
    id, original_filename, storage_path, file_hash, source_type, status,
    imported_rows, valid_code_rows, duplicate_code_rows, created_at)
VALUES(
    @import_id, @filename, @filename, @file_hash, 'TEST', 'Completed',
    @quantity, @quantity, 0, @now);",
                ("@item_type_id", itemTypeId),
                ("@gtin", "04601234567890"),
                ("@item_id", ItemId),
                ("@marking_order_id", markingOrderId),
                ("@order_id", OrderId),
                ("@order_line_id", OrderLineId),
                ("@quantity", quantity),
                ("@request_number", $"{_prefix}-request"),
                ("@now", now),
                ("@import_id", importId),
                ("@filename", $"{_prefix}.xlsx"),
                ("@file_hash", _prefix));
            var result = new List<Guid>(quantity);
            for (var index = 0; index < quantity; index++)
            {
                var id = Guid.NewGuid();
                result.Add(id);
                await Execute(connection, @"
INSERT INTO marking_code(
    id, code, code_hash, gtin, marking_order_id, import_id, status,
    origin, receipt_doc_id, receipt_line_id, applied_at, created_at, updated_at)
VALUES(
    @id, @code, @hash, @gtin, @marking_order_id, @import_id, 'Applied',
    'LegacySynthetic', @doc_id, @line_id, @now, @now, @now);",
                    ("@id", id),
                    ("@code", $"{_prefix}-code-{index}"),
                    ("@hash", $"{_prefix}-hash-{index}"),
                    ("@gtin", "04601234567890"),
                    ("@marking_order_id", markingOrderId),
                    ("@import_id", importId),
                    ("@doc_id", SourcePrdDocId),
                    ("@line_id", SourceDocLineId),
                    ("@now", now));
            }
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await Execute(connection, @"
DELETE FROM production_marking_transition_audit
WHERE adjustment_id IN (
    SELECT id FROM production_pallet_filling_adjustments
    WHERE source_pallet_id IN (SELECT id FROM production_pallets WHERE order_id = @order_id)
);
DELETE FROM production_pallet_filling_adjustment_lines
WHERE adjustment_id IN (
    SELECT id FROM production_pallet_filling_adjustments
    WHERE source_pallet_id IN (SELECT id FROM production_pallets WHERE order_id = @order_id)
);
DELETE FROM production_pallet_filling_adjustments
WHERE source_pallet_id IN (SELECT id FROM production_pallets WHERE order_id = @order_id);
DELETE FROM marking_code
WHERE marking_order_id IN (SELECT id FROM marking_order WHERE order_id = @order_id);
DELETE FROM marking_order WHERE order_id = @order_id;
DELETE FROM marking_code_import WHERE original_filename = @marking_filename;
DELETE FROM ledger WHERE doc_id IN (SELECT id FROM docs WHERE order_id = @order_id);
DELETE FROM production_pallet_lines
WHERE production_pallet_id IN (SELECT id FROM production_pallets WHERE order_id = @order_id);
DELETE FROM production_pallets WHERE order_id = @order_id;
DELETE FROM doc_lines WHERE doc_id IN (SELECT id FROM docs WHERE order_id = @order_id);
DELETE FROM docs WHERE order_id = @order_id;
DELETE FROM partners WHERE code = @hu;
DELETE FROM order_control_events
WHERE task_id IN (
    SELECT task_id FROM order_control_task_orders WHERE order_id = @order_id
);
DELETE FROM order_control_task_hu_lines
WHERE task_id IN (
    SELECT task_id FROM order_control_task_orders WHERE order_id = @order_id
);
DELETE FROM order_control_task_hus
WHERE task_id IN (
    SELECT task_id FROM order_control_task_orders WHERE order_id = @order_id
);
DELETE FROM order_control_tasks
WHERE id IN (
    SELECT task_id FROM order_control_task_orders WHERE order_id = @order_id
);
DELETE FROM order_lines WHERE order_id = @order_id;
DELETE FROM orders WHERE id = @order_id;
DELETE FROM items WHERE id = @item_id;
DELETE FROM item_types WHERE name = @item_type_name;
DELETE FROM locations WHERE id = @location_id;
UPDATE client_blocks SET is_enabled = FALSE WHERE block_key = 'pc_hu_correction';",
                ("@order_id", OrderId),
                ("@item_id", ItemId),
                ("@hu", Hu),
                ("@marking_filename", $"{_prefix}.xlsx"),
                ("@item_type_name", $"{_prefix}-type"),
                ("@location_id", LocationId));
        }

        public static async Task<long> Scalar(
            NpgsqlConnection connection,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public static async Task Execute(
            NpgsqlConnection connection,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string> ReadLedgerSnapshot(
        NpgsqlConnection connection,
        string hu)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT COALESCE(
    jsonb_agg(
        jsonb_build_array(id, ts, doc_id, item_id, location_id, qty_delta, hu_code, hu)
        ORDER BY id)::text,
    '[]')
FROM ledger
WHERE UPPER(BTRIM(COALESCE(hu_code, hu, ''))) = @hu;";
        command.Parameters.AddWithValue("@hu", hu.Trim().ToUpperInvariant());
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "[]";
    }

    private static async Task AssertDuplicateAdjustmentReferenceRejected(string columnName)
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await Fixture.Create(
            connectionString,
            $"UNIQUE-{columnName}-{Guid.NewGuid():N}");
        var result = new ProductionPalletFillingCorrectionService(
            new PostgresDataStore(connectionString)).Confirm(
            new ProductionPalletFillingCorrectionConfirmRequest
            {
                RequestId = Guid.NewGuid().ToString(),
                HuCode = fixture.Hu,
                ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                ReasonText = "Проверка уникальности adjustment"
            });
        Assert.True(result.Success, result.Message);
        var referenceId = columnName switch
        {
            "cor_doc_id" => result.CorDocId!.Value,
            "replacement_pallet_id" => result.ReplacementPalletId!.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(columnName))
        };
        var duplicateRequestId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        try
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $@"
INSERT INTO production_pallet_filling_adjustments(
    action_type, request_id, payload_hash, {columnName}, reason_code,
    reason_text, created_at)
VALUES(
    'CORRECT_FILLED', @request_id, 'duplicate-reference', @reference_id,
    'ERRONEOUS_HU_FILL', 'duplicate-reference', @now);";
                command.Parameters.AddWithValue("@request_id", duplicateRequestId);
                command.Parameters.AddWithValue("@reference_id", referenceId);
                command.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
                await command.ExecuteNonQueryAsync();
            });
            Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        }
        finally
        {
            await Fixture.Execute(
                connection,
                "DELETE FROM production_pallet_filling_adjustments WHERE request_id = @request_id;",
                ("@request_id", duplicateRequestId));
        }
    }

    private static async Task AssertSingleCommittedBusinessEffect(
        string connectionString,
        Fixture fixture,
        Guid requestId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    (SELECT COUNT(*) FROM production_pallet_filling_adjustments
     WHERE request_id = @request_id AND result_json IS NOT NULL),
    (SELECT COUNT(*) FROM docs
     WHERE order_id = @order_id
       AND type = 'INVENTORY_CORRECTION'
       AND status = 'CLOSED'),
    (SELECT COUNT(*) FROM production_pallets WHERE order_id = @order_id),
    (SELECT COUNT(*) FROM ledger
     WHERE doc_id = @source_doc_id AND qty_delta > 0),
    (SELECT COUNT(*) FROM ledger l
     JOIN docs d ON d.id = l.doc_id
     WHERE d.order_id = @order_id
       AND d.type = 'INVENTORY_CORRECTION'
       AND l.qty_delta < 0),
    (SELECT COALESCE(SUM(l.qty_delta), 0)
     FROM ledger l
     JOIN docs d ON d.id = l.doc_id
     WHERE d.order_id = @order_id
       AND d.type = 'INVENTORY_CORRECTION'
       AND l.qty_delta < 0);";
        command.Parameters.AddWithValue("@request_id", requestId);
        command.Parameters.AddWithValue("@order_id", fixture.OrderId);
        command.Parameters.AddWithValue("@source_doc_id", fixture.SourcePrdDocId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(2L, reader.GetInt64(2));
        Assert.Equal(1L, reader.GetInt64(3));
        Assert.Equal(1L, reader.GetInt64(4));
        Assert.Equal(-10d, reader.GetDouble(5), 6);
    }

    private static async Task AssertConcurrentAppendOnlyReplacement(
        bool useFromHu,
        bool tombstone)
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        await using var fixture = await Fixture.Create(
            connectionString,
            $"REPLACE-OLD-HU-{useFromHu}-{tombstone}-{Guid.NewGuid():N}");
        var setupDocuments = new DocumentService(new PostgresDataStore(connectionString));
        var docId = setupDocuments.CreateDoc(
            DocType.Move,
            $"MOV-{Guid.NewGuid():N}",
            "Append-only replacement race",
            partnerId: null,
            orderRef: null,
            shippingRef: null,
            orderId: fixture.OrderId,
            hydrateOrderLines: false);
        var oldOtherHu = $"OLD-OTHER-{Guid.NewGuid():N}".ToUpperInvariant();
        var activeLineId = setupDocuments.AddDocLine(
            docId,
            fixture.ItemId,
            1,
            fixture.LocationId,
            fixture.LocationId,
            fromHu: useFromHu ? fixture.Hu : oldOtherHu,
            toHu: useFromHu ? oldOtherHu : fixture.Hu,
            orderLineId: fixture.OrderLineId,
            productionPurpose: ProductionLinePurpose.InternalStock);

        await using var huBlocker = new NpgsqlConnection(connectionString);
        await huBlocker.OpenAsync();
        await using var huBlockerTx = await huBlocker.BeginTransactionAsync();
        await LockHu(huBlocker, huBlockerTx, fixture.Hu);
        var replacementBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"replace-old-hu-{Guid.NewGuid():N}",
            Pooling = false
        };
        var newFromHu = $"NEW-FROM-{Guid.NewGuid():N}".ToUpperInvariant();
        var newToHu = $"NEW-TO-{Guid.NewGuid():N}".ToUpperInvariant();
        var replacementTask = Task.Run(() =>
            new DocumentService(new PostgresDataStore(replacementBuilder.ConnectionString))
                .AddDocLine(
                    docId,
                    fixture.ItemId,
                    tombstone ? 0 : 1,
                    fixture.LocationId,
                    fixture.LocationId,
                    fromHu: newFromHu,
                    toHu: newToHu,
                    orderLineId: fixture.OrderLineId,
                    replacesLineId: activeLineId,
                    productionPurpose: ProductionLinePurpose.InternalStock));
        await WaitUntilPostgresSessionWaitsForLockOrFailIfCompleted(
            connectionString,
            replacementBuilder.ApplicationName!,
            replacementTask,
            TimeSpan.FromSeconds(5));
        var correctionTask = Task.Run(() =>
            new ProductionPalletFillingCorrectionService(
                new PostgresDataStore(connectionString)).Confirm(
                new ProductionPalletFillingCorrectionConfirmRequest
                {
                    RequestId = Guid.NewGuid().ToString(),
                    HuCode = fixture.Hu,
                    ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                    ReasonText = "Concurrent append-only replacement"
                }));

        await huBlockerTx.CommitAsync();
        var replacementLineId = await replacementTask.WaitAsync(TimeSpan.FromSeconds(10));
        var correction = await correctionTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(correction.Success, correction.Message);
        await using var verify = new NpgsqlConnection(connectionString);
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = @"
SELECT
    (SELECT COUNT(*) FROM doc_lines
     WHERE id = @replacement_line_id
       AND replaces_line_id = @active_line_id),
    (SELECT COUNT(*) FROM production_pallet_filling_adjustments
     WHERE source_pallet_id = @source_pallet_id
       AND result_json IS NOT NULL);";
        command.Parameters.AddWithValue("@replacement_line_id", replacementLineId);
        command.Parameters.AddWithValue("@active_line_id", activeLineId);
        command.Parameters.AddWithValue("@source_pallet_id", fixture.SourcePalletId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    private static async Task WaitUntilPostgresSessionWaitsForLockOrFailIfCompleted(
        string connectionString,
        string applicationName,
        Task operation,
        TimeSpan timeout)
    {
        var waitForLock = WaitUntilPostgresSessionWaitsForLock(
            connectionString,
            applicationName,
            timeout);
        var completed = await Task.WhenAny(operation, waitForLock);
        if (completed == operation)
        {
            try
            {
                await operation;
                Assert.Fail(
                    $"Операция {applicationName} завершилась без ожидания старого HU-lock.");
            }
            catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
            {
                Assert.Fail(
                    $"Операция {applicationName} завершилась до старого HU-lock: {exception.Message}");
            }
        }
        await waitForLock;
    }

    private static async Task WaitUntilPostgresSessionWaitsForLock(
        string connectionString,
        string applicationName,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        while (DateTime.UtcNow < deadline)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT EXISTS(
    SELECT 1
    FROM pg_stat_activity
    WHERE application_name = @application_name
      AND wait_event_type = 'Lock'
);";
            command.Parameters.AddWithValue("@application_name", applicationName);
            if (Convert.ToBoolean(await command.ExecuteScalarAsync()))
            {
                return;
            }
            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"PostgreSQL session {applicationName} не перешла в ожидание lock.");
    }

    private static async Task LockHu(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string hu)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(73421, hashtext(@hu_code));";
        command.Parameters.AddWithValue("@hu_code", hu.Trim().ToUpperInvariant());
        await command.ExecuteNonQueryAsync();
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

    private sealed class PartialFixture : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly long[] _itemIds;
        private readonly long _locationId;

        private PartialFixture(
            string connectionString,
            string hu,
            long orderId,
            long prdDocId,
            long palletId,
            long[] itemIds,
            long locationId)
        {
            _connectionString = connectionString;
            Hu = hu;
            OrderId = orderId;
            PrdDocId = prdDocId;
            PalletId = palletId;
            _itemIds = itemIds;
            _locationId = locationId;
        }

        public string Hu { get; }
        public long OrderId { get; }
        public long PrdDocId { get; }
        public long PalletId { get; }
        public IReadOnlyList<long> ItemIds => _itemIds;
        public long LocationId => _locationId;

        public async Task CompleteAsFilled(bool includeAllLedgerRows = true, bool addExtraLedgerRow = false)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            var now = DateTime.Now.ToString("O");
            await Fixture.Execute(connection, @"
UPDATE production_pallet_lines
SET filled_qty = planned_qty, filled_at = @now
WHERE production_pallet_id = @pallet_id;
UPDATE production_pallets
SET status = 'FILLED', filled_at = @now
WHERE id = @pallet_id;
UPDATE docs
SET status = 'CLOSED', closed_at = @now
WHERE id = @doc_id;",
                ("@now", now),
                ("@pallet_id", PalletId),
                ("@doc_id", PrdDocId));

            var take = includeAllLedgerRows ? _itemIds.Length : 1;
            await Fixture.Execute(connection, @"
INSERT INTO ledger(ts, doc_id, item_id, location_id, qty_delta, hu_code, hu)
SELECT @now, @doc_id, dl.item_id, @location_id, dl.qty, @hu, @hu
FROM doc_lines dl
WHERE dl.doc_id = @doc_id
ORDER BY dl.id
LIMIT @take;",
                ("@now", now),
                ("@doc_id", PrdDocId),
                ("@location_id", _locationId),
                ("@hu", Hu),
                ("@take", take));
            if (addExtraLedgerRow)
            {
                await Fixture.Execute(connection, @"
INSERT INTO ledger(ts, doc_id, item_id, location_id, qty_delta, hu_code, hu)
VALUES(@now, @doc_id, @item_id, @location_id, 1, @hu, @hu);",
                    ("@now", now),
                    ("@doc_id", PrdDocId),
                    ("@item_id", _itemIds[0]),
                    ("@location_id", _locationId),
                    ("@hu", Hu));
            }
        }

        public static async Task<PartialFixture> Create(string connectionString, string prefix)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            var now = DateTime.Now.ToString("O");
            var hu = $"HU-{prefix}".ToUpperInvariant();
            var itemTypeId = await Fixture.Scalar(
                connection,
                "SELECT id FROM item_types WHERE code = 'GENERAL' ORDER BY id LIMIT 1;");
            var itemIds = new long[2];
            for (var index = 0; index < itemIds.Length; index++)
            {
                itemIds[index] = await Fixture.Scalar(connection, @"
INSERT INTO items(name, barcode, base_uom, item_type_id, is_active)
VALUES(@name, @barcode, 'шт', @item_type_id, TRUE) RETURNING id;",
                    ("@name", $"{prefix}-item-{index}"),
                    ("@barcode", $"{prefix}-barcode-{index}"),
                    ("@item_type_id", itemTypeId));
            }
            var locationId = await Fixture.Scalar(connection,
                "INSERT INTO locations(code, name) VALUES(@code, @name) RETURNING id;",
                ("@code", $"{prefix}-loc"),
                ("@name", prefix));
            var orderId = await Fixture.Scalar(connection, @"
INSERT INTO orders(order_ref, order_type, status, created_at)
VALUES(@ref, 'INTERNAL', 'IN_PROGRESS', @now) RETURNING id;",
                ("@ref", $"{prefix}-order"),
                ("@now", now));
            var orderLineIds = new long[2];
            for (var index = 0; index < itemIds.Length; index++)
            {
                orderLineIds[index] = await Fixture.Scalar(connection, @"
INSERT INTO order_lines(order_id, item_id, qty_ordered, production_purpose, production_pallet_group)
VALUES(@order_id, @item_id, 5, 'INTERNAL_STOCK', @group) RETURNING id;",
                    ("@order_id", orderId),
                    ("@item_id", itemIds[index]),
                    ("@group", prefix));
            }
            var docId = await Fixture.Scalar(connection, @"
INSERT INTO docs(doc_ref, type, status, created_at, order_id, order_ref, shipping_ref)
VALUES(@ref, 'PRODUCTION_RECEIPT', 'DRAFT', @now, @order_id, @order_ref, @hu)
RETURNING id;",
                ("@ref", $"{prefix}-PRD"),
                ("@now", now),
                ("@order_id", orderId),
                ("@order_ref", $"{prefix}-order"),
                ("@hu", hu));
            var docLineIds = new long[2];
            for (var index = 0; index < itemIds.Length; index++)
            {
                docLineIds[index] = await Fixture.Scalar(connection, @"
INSERT INTO doc_lines(
    doc_id, order_line_id, production_purpose, item_id, qty,
    to_location_id, to_hu, pack_single_hu)
VALUES(@doc_id, @order_line_id, 'INTERNAL_STOCK', @item_id, 5, @location_id, @hu, TRUE)
RETURNING id;",
                    ("@doc_id", docId),
                    ("@order_line_id", orderLineIds[index]),
                    ("@item_id", itemIds[index]),
                    ("@location_id", locationId),
                    ("@hu", hu));
            }
            var palletId = await Fixture.Scalar(connection, @"
INSERT INTO production_pallets(
    prd_doc_id, doc_line_id, order_id, order_line_id, item_id, hu_code,
    planned_qty, to_location_id, status, pallet_no, pallet_count, printed_at, created_at)
VALUES(@doc_id, @doc_line_id, @order_id, @order_line_id, @item_id, @hu,
       10, @location_id, 'PRINTED', 1, 1, @now, @now)
RETURNING id;",
                ("@doc_id", docId),
                ("@doc_line_id", docLineIds[0]),
                ("@order_id", orderId),
                ("@order_line_id", orderLineIds[0]),
                ("@item_id", itemIds[0]),
                ("@hu", hu),
                ("@location_id", locationId),
                ("@now", now));
            for (var index = 0; index < itemIds.Length; index++)
            {
                await Fixture.Execute(connection, @"
INSERT INTO production_pallet_lines(
    production_pallet_id, doc_line_id, order_line_id, item_id,
    planned_qty, filled_qty, filled_at, created_at)
VALUES(@pallet_id, @doc_line_id, @order_line_id, @item_id, 5, @filled_qty, @filled_at, @now);",
                    ("@pallet_id", palletId),
                    ("@doc_line_id", docLineIds[index]),
                    ("@order_line_id", orderLineIds[index]),
                    ("@item_id", itemIds[index]),
                    ("@filled_qty", index == 0 ? 5d : 0d),
                    ("@filled_at", index == 0 ? now : DBNull.Value),
                    ("@now", now));
            }
            await Fixture.Execute(connection,
                "UPDATE client_blocks SET is_enabled = TRUE WHERE block_key = 'pc_hu_correction';");
            return new PartialFixture(connectionString, hu, orderId, docId, palletId, itemIds, locationId);
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await Fixture.Execute(connection, @"
DELETE FROM production_marking_transition_audit
WHERE adjustment_id IN (
    SELECT a.id
    FROM production_pallet_filling_adjustments a
    JOIN production_pallets p ON p.id = a.source_pallet_id
    WHERE p.order_id = @order_id
);
DELETE FROM production_pallet_filling_adjustment_lines
WHERE adjustment_id IN (
    SELECT a.id
    FROM production_pallet_filling_adjustments a
    JOIN production_pallets p ON p.id = a.source_pallet_id
    WHERE p.order_id = @order_id
);
DELETE FROM production_pallet_filling_adjustments
WHERE source_pallet_id IN (
    SELECT id FROM production_pallets WHERE order_id = @order_id
);
DELETE FROM ledger WHERE doc_id IN (SELECT id FROM docs WHERE order_id = @order_id);
DELETE FROM production_pallet_lines
WHERE production_pallet_id IN (
    SELECT id FROM production_pallets WHERE order_id = @order_id
);
DELETE FROM production_pallets WHERE order_id = @order_id;
DELETE FROM doc_lines WHERE doc_id IN (SELECT id FROM docs WHERE order_id = @order_id);
DELETE FROM docs WHERE order_id = @order_id;
DELETE FROM order_lines WHERE order_id = @order_id;
DELETE FROM orders WHERE id = @order_id;
DELETE FROM items WHERE id = ANY(@item_ids);
DELETE FROM locations WHERE id = @location_id;
UPDATE client_blocks SET is_enabled = FALSE WHERE block_key = 'pc_hu_correction';",
                ("@order_id", OrderId),
                ("@item_ids", _itemIds),
                ("@location_id", _locationId));
        }
    }
}

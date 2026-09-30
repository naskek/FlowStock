using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletAdoptionProvenancePostgresTests
{
    [Fact]
    public void Migration_DefinesDurableTransferHistoryWithoutOperationalEntityForeignKeys()
    {
        var sql = File.ReadAllText(GetRepoFilePath(
            "deploy",
            "postgres",
            "migrations",
            "V0036__order_coverage_transfer_provenance.sql"));

        Assert.Contains("CREATE TABLE order_coverage_transfers", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE order_coverage_transfer_lines", sql, StringComparison.Ordinal);
        Assert.Contains("REFERENCES order_coverage_transfers(id) ON DELETE CASCADE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCES order_lines", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCES production_pallets", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCES docs", sql, StringComparison.Ordinal);
        Assert.Contains("PLANNED_PALLET_ADOPTION", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ProducedCompensationMigration_AddsCurrentSourceLineMappingWithoutOperationalForeignKey()
    {
        var sql = File.ReadAllText(GetRepoFilePath(
            "deploy",
            "postgres",
            "migrations",
            "V0037__produced_coverage_compensation.sql"));

        Assert.Contains("compensated_source_order_line_id", sql, StringComparison.Ordinal);
        Assert.Contains("ix_order_coverage_transfer_lines_compensated_source", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCES order_lines", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCES production_pallets", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCES docs", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectedAdoption_PersistsMixedLineProvenance_ThatSurvivesSourceLineDeletion()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var fixture = AdoptionFixture.Create(connectionString, mixed: true);
        var store = new PostgresDataStore(connectionString);
        store.Initialize();

        var affected = store.AdoptSelectedProductionPallets(
            fixture.TargetPrdDocId,
            fixture.TargetOrderId,
            [fixture.BuildSelectedAdoption()]);

        Assert.Equal(1, affected);
        var transfer = Assert.Single(store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId));
        Assert.Equal(OrderCoverageTransferType.PlannedPalletAdoption, transfer.TransferType);
        Assert.Equal(fixture.SourceOrderId, transfer.SourceOrderId);
        Assert.Equal(fixture.TargetOrderId, transfer.TargetOrderId);
        Assert.Equal(fixture.SourcePrdDocId, transfer.SourcePrdDocId);
        Assert.Equal(fixture.TargetPrdDocId, transfer.TargetPrdDocId);
        Assert.Equal(fixture.ProductionPalletId, transfer.ProductionPalletId);
        Assert.Equal(fixture.HuCode, transfer.HuCode);
        Assert.Equal(ProductionPalletStatus.Planned, transfer.PalletStatusAtTransfer);
        Assert.Equal("IN_PROGRESS", transfer.SourceOrderStatus);
        Assert.Equal(100d, transfer.TransferredQty, 6);
        Assert.Equal(2, transfer.Lines.Count);
        Assert.Equal(
            fixture.SourceOrderLineIds.Order().ToArray(),
            transfer.Lines.Select(line => line.SourceOrderLineId).Order().ToArray());
        Assert.All(transfer.Lines, line =>
        {
            Assert.Equal("INTERNAL_STOCK", line.SourceProductionPurpose);
            Assert.Equal("MIX-1", line.SourceProductionPalletGroup);
            Assert.True(
                line.SourceProductionPalletLineId.HasValue
                && line.SourceProductionPalletLineId.Value > 0);
        });

        fixture.DeleteSourceOrderLines();

        var persisted = Assert.Single(store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId));
        Assert.Equal(
            fixture.SourceOrderLineIds.Order().ToArray(),
            persisted.Lines.Select(line => line.SourceOrderLineId).Order().ToArray());
        Assert.Equal(new[] { 40d, 60d }, persisted.Lines.Select(line => line.TransferredQty).Order().ToArray());
    }

    [Fact]
    public void SelectedAdoption_RollsBackProvenanceAndOwnershipTogether()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var fixture = AdoptionFixture.Create(connectionString, mixed: false);
        var store = new PostgresDataStore(connectionString);
        store.Initialize();

        Assert.Throws<RollbackRequestedException>(() =>
            store.ExecuteInTransaction(transactionStore =>
            {
                transactionStore.AdoptSelectedProductionPallets(
                    fixture.TargetPrdDocId,
                    fixture.TargetOrderId,
                    [fixture.BuildSelectedAdoption()]);
                throw new RollbackRequestedException();
            }));

        Assert.Empty(store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId));
        var ownership = fixture.ReadPalletOwnership();
        Assert.Equal(fixture.SourceOrderId, ownership.OrderId);
        Assert.Equal(fixture.SourcePrdDocId, ownership.PrdDocId);
        Assert.Equal(fixture.SourceOrderLineIds.Single(), ownership.OrderLineId);
    }

    [Fact]
    public void CustomerCancel_Internal300Adopts100_RestoresExistingSourceDemandTo300()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var fixture = AdoptionFixture.Create(connectionString, mixed: false);
        var store = new PostgresDataStore(connectionString);
        store.Initialize();
        fixture.SetSingleSourceQty(300);

        store.AdoptSelectedProductionPallets(
            fixture.TargetPrdDocId,
            fixture.TargetOrderId,
            [fixture.BuildSelectedAdoption()]);
        fixture.SetSingleSourceQty(200);
        fixture.DeleteSourcePrdDoc();

        new OrderService(store).CancelOrder(fixture.TargetOrderId);

        var sourceLine = Assert.Single(store.GetOrderLines(fixture.SourceOrderId));
        Assert.Equal(fixture.SourceOrderLineIds.Single(), sourceLine.Id);
        Assert.Equal(300d, sourceLine.QtyOrdered, 6);
        Assert.Equal(OrderStatus.InProgress, store.GetOrder(fixture.SourceOrderId)?.Status);
        var ownership = fixture.ReadPalletOwnership();
        Assert.Equal(fixture.SourceOrderId, ownership.OrderId);
        Assert.Equal(sourceLine.Id, ownership.OrderLineId);
        Assert.Equal(OrderCoverageCompensationKind.ReturnPlan,
            Assert.Single(store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId)).CompensationKind);
    }

    [Fact]
    public void CustomerCancel_ReversesMixedPlannedAdoption_RestoresMergedInternalAndIsIdempotent()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var fixture = AdoptionFixture.Create(connectionString, mixed: true);
        var store = new PostgresDataStore(connectionString);
        store.Initialize();

        store.AdoptSelectedProductionPallets(
            fixture.TargetPrdDocId,
            fixture.TargetOrderId,
            [fixture.BuildSelectedAdoption()]);
        fixture.ConsumeSourceDemandAndDeleteOperationalSourceRows();

        var ledgerBefore = fixture.CountLedgerRowsForHu();
        new OrderService(store).CancelOrder(fixture.TargetOrderId);

        Assert.Equal(OrderStatus.Cancelled, store.GetOrder(fixture.TargetOrderId)?.Status);
        Assert.Equal(OrderStatus.InProgress, store.GetOrder(fixture.SourceOrderId)?.Status);
        Assert.Equal(new[] { 40d, 60d }, store.GetOrderLines(fixture.SourceOrderId)
            .Select(line => line.QtyOrdered)
            .Order()
            .ToArray());

        var ownership = fixture.ReadPalletOwnership();
        Assert.Equal(fixture.SourceOrderId, ownership.OrderId);
        Assert.NotEqual(fixture.SourcePrdDocId, ownership.PrdDocId);
        Assert.Null(ownership.OrderLineId);
        var restoredPallet = Assert.Single(store.GetProductionPalletsByDoc(ownership.PrdDocId));
        Assert.Equal(fixture.HuCode, restoredPallet.HuCode);
        Assert.Equal(ProductionPalletStatus.Planned, restoredPallet.Status);
        Assert.Null(restoredPallet.PrintedAt);
        Assert.Equal(2, restoredPallet.Lines.Count);
        Assert.All(restoredPallet.Lines, line => Assert.Contains(
            line.OrderLineId,
            store.GetOrderLines(fixture.SourceOrderId).Select(sourceLine => (long?)sourceLine.Id)));

        var transfer = Assert.Single(store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId));
        Assert.Equal(OrderCoverageCompensationKind.ReturnPlan, transfer.CompensationKind);
        Assert.NotNull(transfer.CompensatedAt);
        Assert.Equal(ledgerBefore, fixture.CountLedgerRowsForHu());

        new OrderService(store).CancelOrder(fixture.TargetOrderId);
        Assert.Equal(new[] { 40d, 60d }, store.GetOrderLines(fixture.SourceOrderId)
            .Select(line => line.QtyOrdered)
            .Order()
            .ToArray());
        Assert.Equal(ledgerBefore, fixture.CountLedgerRowsForHu());
    }

    [Fact]
    public void CustomerCancel_ReversesPrintedAdoption_PreservesHuAndInvalidatesPrintState()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var fixture = AdoptionFixture.Create(connectionString, mixed: false);
        var store = new PostgresDataStore(connectionString);
        store.Initialize();
        var printedAt = new DateTime(2042, 1, 2, 10, 30, 0, DateTimeKind.Utc);
        fixture.SetPalletPrinted(printedAt);

        store.AdoptSelectedProductionPallets(
            fixture.TargetPrdDocId,
            fixture.TargetOrderId,
            [fixture.BuildSelectedAdoption(ProductionPalletStatus.Printed)]);
        fixture.ConsumeSourceDemandAndDeleteOperationalSourceRows();

        new OrderService(store).CancelOrder(fixture.TargetOrderId);

        var ownership = fixture.ReadPalletOwnership();
        var pallet = Assert.Single(store.GetProductionPalletsByDoc(ownership.PrdDocId));
        Assert.Equal(fixture.HuCode, pallet.HuCode);
        Assert.Equal(ProductionPalletStatus.Planned, pallet.Status);
        Assert.Null(pallet.PrintedAt);

        var transfer = Assert.Single(store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId));
        Assert.Equal(ProductionPalletStatus.Printed, transfer.PalletStatusAtTransfer);
        Assert.NotNull(transfer.PrintedAtAtTransfer);
        Assert.Equal(OrderCoverageCompensationKind.ReturnPlan, transfer.CompensationKind);
        Assert.Equal(0, fixture.CountLedgerRowsForHu());
    }

    [Fact]
    public void CustomerQtyDecrease_ReturnsWholeAdoptedTransferBeforeQtyValidation()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var fixture = AdoptionFixture.Create(connectionString, mixed: false);
        var store = new PostgresDataStore(connectionString);
        store.Initialize();

        store.AdoptSelectedProductionPallets(
            fixture.TargetPrdDocId,
            fixture.TargetOrderId,
            [fixture.BuildSelectedAdoption()]);
        fixture.ConsumeSourceDemandAndDeleteOperationalSourceRows();

        var targetOrder = store.GetOrder(fixture.TargetOrderId)!;
        var targetLine = Assert.Single(store.GetOrderLines(fixture.TargetOrderId));
        new OrderService(store).UpdateOrder(
            targetOrder.Id,
            targetOrder.OrderRef,
            fixture.TargetPartnerId,
            targetOrder.DueDate,
            targetOrder.Comment,
            [
                new OrderLineView
                {
                    Id = targetLine.Id,
                    ItemId = targetLine.ItemId,
                    QtyOrdered = 50,
                    ProductionPurpose = ProductionLinePurpose.CustomerOrder
                }
            ],
            OrderType.Customer);

        Assert.Equal(50d, Assert.Single(store.GetOrderLines(fixture.TargetOrderId)).QtyOrdered, 6);
        Assert.Equal(100d, Assert.Single(store.GetOrderLines(fixture.SourceOrderId)).QtyOrdered, 6);
        var ownership = fixture.ReadPalletOwnership();
        Assert.Equal(fixture.SourceOrderId, ownership.OrderId);
        var transfer = Assert.Single(store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId));
        Assert.Equal(OrderCoverageCompensationKind.ReturnPlan, transfer.CompensationKind);
        Assert.Equal(OrderStatus.InProgress, store.GetOrder(fixture.SourceOrderId)?.Status);
    }

    [Fact]
    public void CustomerQtyChange_UsesServerNormalizedReservationQtyForReturnPlanDecision()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var fixture = AdoptionFixture.Create(connectionString, mixed: false);
        var store = new PostgresDataStore(connectionString);
        store.Initialize();

        store.AdoptSelectedProductionPallets(
            fixture.TargetPrdDocId,
            fixture.TargetOrderId,
            [fixture.BuildSelectedAdoption()]);
        fixture.ConsumeSourceDemandAndDeleteOperationalSourceRows();

        var targetLine = Assert.Single(store.GetOrderLines(fixture.TargetOrderId));
        var reservedHu = $"{fixture.Token}-RESERVED";
        fixture.SeedTargetReservation(targetLine.Id, targetLine.ItemId, 60, reservedHu);
        var targetOrder = store.GetOrder(fixture.TargetOrderId)!;

        new OrderService(store).UpdateOrder(
            targetOrder.Id,
            targetOrder.OrderRef,
            fixture.TargetPartnerId,
            targetOrder.DueDate,
            targetOrder.Comment,
            [
                new OrderLineView
                {
                    Id = targetLine.Id,
                    ItemId = targetLine.ItemId,
                    QtyOrdered = 120,
                    ProductionPurpose = ProductionLinePurpose.CustomerOrder
                }
            ],
            OrderType.Customer,
            customerReservedHuSelectionsByOrderLineId:
                new Dictionary<long, IReadOnlyList<string>>
                {
                    [targetLine.Id] = [reservedHu]
                });

        Assert.Equal(60d, Assert.Single(store.GetOrderLines(fixture.TargetOrderId)).QtyOrdered, 6);
        Assert.Equal(100d, Assert.Single(store.GetOrderLines(fixture.SourceOrderId)).QtyOrdered, 6);
        Assert.Equal(fixture.SourceOrderId, fixture.ReadPalletOwnership().OrderId);
        Assert.Equal(
            OrderCoverageCompensationKind.ReturnPlan,
            Assert.Single(store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId)).CompensationKind);
    }

    [Fact]
    public void CustomerCancel_BlocksReturnPlanOnUnexpectedLedgerItemForSamePrdAndHu()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var fixture = AdoptionFixture.Create(connectionString, mixed: false);
        var store = new PostgresDataStore(connectionString);
        store.Initialize();

        store.AdoptSelectedProductionPallets(
            fixture.TargetPrdDocId,
            fixture.TargetOrderId,
            [fixture.BuildSelectedAdoption()]);
        fixture.ConsumeSourceDemandAndDeleteOperationalSourceRows();
        fixture.AddUnexpectedLedgerForTargetHu();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new OrderService(store).CancelOrder(fixture.TargetOrderId));

        Assert.Contains("паллета уже изменилась", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixture.TargetOrderId, fixture.ReadPalletOwnership().OrderId);
        Assert.Equal(OrderStatus.InProgress, store.GetOrder(fixture.TargetOrderId)?.Status);
        Assert.Equal(OrderStatus.Merged, store.GetOrder(fixture.SourceOrderId)?.Status);
        Assert.Null(Assert.Single(
            store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId)).CompensationKind);
    }

    [Fact]
    public void CustomerLineDelete_ReturnsWholeMixedAdoptedTransfer()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var fixture = AdoptionFixture.Create(connectionString, mixed: true);
        var store = new PostgresDataStore(connectionString);
        store.Initialize();

        store.AdoptSelectedProductionPallets(
            fixture.TargetPrdDocId,
            fixture.TargetOrderId,
            [fixture.BuildSelectedAdoption()]);
        fixture.ConsumeSourceDemandAndDeleteOperationalSourceRows();

        var targetOrder = store.GetOrder(fixture.TargetOrderId)!;
        var remainingTargetLine = store.GetOrderLines(fixture.TargetOrderId)
            .OrderBy(line => line.Id)
            .Last();
        new OrderService(store).UpdateOrder(
            targetOrder.Id,
            targetOrder.OrderRef,
            fixture.TargetPartnerId,
            targetOrder.DueDate,
            targetOrder.Comment,
            [
                new OrderLineView
                {
                    Id = remainingTargetLine.Id,
                    ItemId = remainingTargetLine.ItemId,
                    QtyOrdered = remainingTargetLine.QtyOrdered,
                    ProductionPurpose = ProductionLinePurpose.CustomerOrder
                }
            ],
            OrderType.Customer);

        var targetLines = store.GetOrderLines(fixture.TargetOrderId);
        Assert.Single(targetLines);
        Assert.Equal(remainingTargetLine.Id, targetLines[0].Id);
        Assert.Equal(new[] { 40d, 60d }, store.GetOrderLines(fixture.SourceOrderId)
            .Select(line => line.QtyOrdered)
            .Order()
            .ToArray());
        Assert.Equal(fixture.SourceOrderId, fixture.ReadPalletOwnership().OrderId);
        Assert.Equal(
            OrderCoverageCompensationKind.ReturnPlan,
            Assert.Single(store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId)).CompensationKind);
    }

    [Fact]
    public void CustomerCancel_CompensatesProducedAdoption_RestoresDemandAndConfirmedCredit()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var fixture = AdoptionFixture.Create(connectionString, mixed: false);
        var store = new PostgresDataStore(connectionString);
        store.Initialize();
        fixture.SetSingleSourceQty(300);

        store.AdoptSelectedProductionPallets(
            fixture.TargetPrdDocId,
            fixture.TargetOrderId,
            [fixture.BuildSelectedAdoption()]);
        fixture.SetSingleSourceQty(200);
        fixture.MarkAdoptedPalletProducedAndClosed();

        var targetLine = Assert.Single(store.GetOrderLines(fixture.TargetOrderId));
        fixture.SeedTargetReservation(targetLine.Id, targetLine.ItemId, 100, fixture.HuCode);

        var ledgerBefore = fixture.CountLedgerRowsForHu();
        var ownershipBefore = fixture.ReadPalletOwnership();
        new OrderService(store).CancelOrder(fixture.TargetOrderId);

        Assert.Equal(OrderStatus.Cancelled, store.GetOrder(fixture.TargetOrderId)?.Status);
        var sourceLine = Assert.Single(store.GetOrderLines(fixture.SourceOrderId));
        Assert.Equal(300d, sourceLine.QtyOrdered, 6);
        Assert.Equal(OrderStatus.InProgress, store.GetOrder(fixture.SourceOrderId)?.Status);

        var remaining = Assert.Single(new DocumentService(store).GetOrderReceiptRemaining(fixture.SourceOrderId));
        Assert.Equal(300d, remaining.QtyOrdered, 6);
        Assert.Equal(100d, remaining.QtyReceived, 6);
        Assert.Equal(200d, remaining.QtyRemaining, 6);

        var transfer = Assert.Single(store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId));
        Assert.Equal(OrderCoverageCompensationKind.ReturnProduced, transfer.CompensationKind);
        Assert.NotNull(transfer.CompensatedAt);
        Assert.Equal(sourceLine.Id, Assert.Single(transfer.Lines).CompensatedSourceOrderLineId);

        var ownershipAfter = fixture.ReadPalletOwnership();
        Assert.Equal(ownershipBefore, ownershipAfter);
        Assert.Equal(fixture.TargetOrderId, ownershipAfter.OrderId);
        Assert.Equal(fixture.TargetPrdDocId, ownershipAfter.PrdDocId);
        Assert.Equal(DocStatus.Closed, store.GetDoc(fixture.TargetPrdDocId)?.Status);
        Assert.Equal(ledgerBefore, fixture.CountLedgerRowsForHu());
        Assert.Empty(store.GetOrderReceiptPlanLines(fixture.TargetOrderId));

        var stockQty = store.GetHuStockRows()
            .Where(row => string.Equals(row.HuCode, fixture.HuCode, StringComparison.OrdinalIgnoreCase))
            .Sum(row => row.Qty);
        Assert.Equal(100d, stockQty, 6);

        var need = Assert.Single(
            new ProductionNeedService(store).GetRows(includeZeroNeed: true),
            row => row.ItemId == targetLine.ItemId);
        Assert.Equal(200d, need.OpenInternalOrderQty, 6);

        new OrderService(store).CancelOrder(fixture.TargetOrderId);
        Assert.Equal(300d, Assert.Single(store.GetOrderLines(fixture.SourceOrderId)).QtyOrdered, 6);
        Assert.Equal(100d, Assert.Single(
            store.GetCompensatedProducedCoverageBySourceOrderLine(fixture.SourceOrderId)).Value, 6);
    }

    [Fact]
    public void CustomerCancel_CompensatesMixedProducedAdoption_RecreatesSourceLinesAndCreditsComponents()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var fixture = AdoptionFixture.Create(connectionString, mixed: true);
        var store = new PostgresDataStore(connectionString);
        store.Initialize();

        store.AdoptSelectedProductionPallets(
            fixture.TargetPrdDocId,
            fixture.TargetOrderId,
            [fixture.BuildSelectedAdoption()]);
        fixture.ConsumeSourceDemandAndDeleteOperationalSourceRows();
        fixture.MarkAdoptedPalletProducedAndClosed();

        var ledgerBefore = fixture.CountLedgerRowsForHu();
        new OrderService(store).CancelOrder(fixture.TargetOrderId);

        var sourceLines = store.GetOrderLines(fixture.SourceOrderId)
            .OrderBy(line => line.ItemId)
            .ToArray();
        Assert.Equal(2, sourceLines.Length);
        Assert.Equal(new[] { 40d, 60d }, sourceLines.Select(line => line.QtyOrdered).Order().ToArray());

        var remaining = new DocumentService(store).GetOrderReceiptRemaining(fixture.SourceOrderId)
            .OrderBy(line => line.ItemId)
            .ToArray();
        Assert.Equal(2, remaining.Length);
        Assert.All(remaining, line => Assert.Equal(line.QtyOrdered, line.QtyReceived, 6));
        Assert.All(remaining, line => Assert.Equal(0d, line.QtyRemaining, 6));
        Assert.Equal(OrderStatus.Shipped, store.GetOrder(fixture.SourceOrderId)?.Status);

        var transfer = Assert.Single(store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId));
        Assert.Equal(OrderCoverageCompensationKind.ReturnProduced, transfer.CompensationKind);
        Assert.All(transfer.Lines, line =>
        {
            var restored = Assert.Single(sourceLines, sourceLine => sourceLine.ItemId == line.ItemId);
            Assert.Equal(restored.Id, line.CompensatedSourceOrderLineId);
        });

        Assert.Equal(fixture.TargetOrderId, fixture.ReadPalletOwnership().OrderId);
        Assert.Equal(DocStatus.Closed, store.GetDoc(fixture.TargetPrdDocId)?.Status);
        Assert.Equal(ledgerBefore, fixture.CountLedgerRowsForHu());
    }

    [Fact]
    public void CustomerCancel_ProducedCompensation_DoesNotCreditOwnCustomerProduction()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var fixture = AdoptionFixture.Create(connectionString, mixed: false);
        var store = new PostgresDataStore(connectionString);
        store.Initialize();
        fixture.SetSingleSourceQty(300);

        store.AdoptSelectedProductionPallets(
            fixture.TargetPrdDocId,
            fixture.TargetOrderId,
            [fixture.BuildSelectedAdoption()]);
        fixture.SetSingleSourceQty(200);
        fixture.MarkAdoptedPalletProducedAndClosed();
        var own = fixture.AddOwnCustomerProducedStock(25);

        new OrderService(store).CancelOrder(fixture.TargetOrderId);

        var sourceLine = Assert.Single(store.GetOrderLines(fixture.SourceOrderId));
        Assert.Equal(300d, sourceLine.QtyOrdered, 6);
        var credits = store.GetCompensatedProducedCoverageBySourceOrderLine(fixture.SourceOrderId);
        Assert.Equal(100d, Assert.Single(credits).Value, 6);

        Assert.Equal(DocStatus.Closed, store.GetDoc(own.PrdDocId)?.Status);
        Assert.Equal(25d, store.GetLedgerQtyByDocItemHu(own.PrdDocId, own.ItemId, own.HuCode), 6);
        var ownPallet = Assert.Single(store.GetProductionPalletsByDoc(own.PrdDocId));
        Assert.Equal(fixture.TargetOrderId, ownPallet.OrderId);
        Assert.Equal(ProductionPalletStatus.Filled, ownPallet.Status);
    }

    [Fact]
    public void CustomerCancel_DoesNotReverseAdoptedFilledPallet()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var fixture = AdoptionFixture.Create(connectionString, mixed: false);
        var store = new PostgresDataStore(connectionString);
        store.Initialize();

        store.AdoptSelectedProductionPallets(
            fixture.TargetPrdDocId,
            fixture.TargetOrderId,
            [fixture.BuildSelectedAdoption()]);
        fixture.ConsumeSourceDemandAndDeleteOperationalSourceRows();
        fixture.SetCurrentPalletStatus(ProductionPalletStatus.Filled);

        Assert.Throws<InvalidOperationException>(() =>
            new OrderService(store).CancelOrder(fixture.TargetOrderId));

        Assert.Equal(OrderStatus.InProgress, store.GetOrder(fixture.TargetOrderId)?.Status);
        Assert.Equal(OrderStatus.Merged, store.GetOrder(fixture.SourceOrderId)?.Status);
        var transfer = Assert.Single(store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId));
        Assert.Null(transfer.CompensationKind);
        Assert.Null(transfer.CompensatedAt);
    }

    [Fact]
    public void LegacyWholePlanAdoption_PersistsTheSameProvenanceShape()
    {
        var connectionString = ResolvePostgresTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var fixture = AdoptionFixture.Create(connectionString, mixed: true);
        var store = new PostgresDataStore(connectionString);
        store.Initialize();

        var result = store.AdoptProductionPalletPlan(
            fixture.SourcePrdDocId,
            fixture.TargetPrdDocId,
            fixture.SourceOrderId,
            fixture.TargetOrderId,
            fixture.TargetOrderLineIdsByItemId);

        Assert.True(result.Success);
        var transfer = Assert.Single(store.GetOrderCoverageTransfersByTargetOrder(fixture.TargetOrderId));
        Assert.Equal(fixture.ProductionPalletId, transfer.ProductionPalletId);
        Assert.Equal(100d, transfer.TransferredQty, 6);
        Assert.Equal(2, transfer.Lines.Count);
        Assert.Equal(
            fixture.TargetOrderLineIdsByItemId.Values.Order().ToArray(),
            transfer.Lines.Select(line => line.TargetOrderLineId).Order().ToArray());
    }

    private static string GetRepoFilePath(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(new[] { current.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Repository file not found: {string.Join('/', parts)}");
    }

    private static string? ResolvePostgresTestConnectionString()
    {
        foreach (var key in new[]
                 {
                     "FLOWSTOCK_POSTGRES_TEST_CONNECTION",
                     "FLOWSTOCK_POSTGRES_CONNECTION",
                     "POSTGRES_CONNECTION_STRING"
                 })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private sealed class RollbackRequestedException : Exception;

    private sealed class AdoptionFixture : IDisposable
    {
        private readonly string _connectionString;
        private readonly long _locationId;
        private readonly long _targetPartnerId;
        private readonly long[] _itemIds;
        private readonly List<long> _extraItemIds = new();
        private readonly long[] _sourceDocLineIds;

        private AdoptionFixture(
            string connectionString,
            string token,
            long sourceOrderId,
            long targetOrderId,
            long[] sourceOrderLineIds,
            Dictionary<long, long> targetOrderLineIdsByItemId,
            long sourcePrdDocId,
            long targetPrdDocId,
            long productionPalletId,
            long locationId,
            long targetPartnerId,
            long[] itemIds,
            long[] sourceDocLineIds,
            string huCode)
        {
            _connectionString = connectionString;
            Token = token;
            SourceOrderId = sourceOrderId;
            TargetOrderId = targetOrderId;
            SourceOrderLineIds = sourceOrderLineIds;
            TargetOrderLineIdsByItemId = targetOrderLineIdsByItemId;
            SourcePrdDocId = sourcePrdDocId;
            TargetPrdDocId = targetPrdDocId;
            ProductionPalletId = productionPalletId;
            _locationId = locationId;
            _targetPartnerId = targetPartnerId;
            _itemIds = itemIds;
            _sourceDocLineIds = sourceDocLineIds;
            HuCode = huCode;
        }

        public string Token { get; }
        public long SourceOrderId { get; }
        public long TargetOrderId { get; }
        public long[] SourceOrderLineIds { get; }
        public Dictionary<long, long> TargetOrderLineIdsByItemId { get; }
        public long SourcePrdDocId { get; }
        public long TargetPrdDocId { get; }
        public long ProductionPalletId { get; }
        public long TargetPartnerId => _targetPartnerId;
        public string HuCode { get; }

        public static AdoptionFixture Create(string connectionString, bool mixed)
        {
            var token = $"prov-{Guid.NewGuid():N}";
            var huCode = $"{token}-HU";
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();

            long Scalar(string sql, params (string Name, object Value)[] parameters)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                foreach (var parameter in parameters)
                {
                    command.Parameters.AddWithValue(parameter.Name, parameter.Value);
                }

                return Convert.ToInt64(command.ExecuteScalar());
            }

            var item1 = Scalar(
                "INSERT INTO items(name, barcode) VALUES (@name, @barcode) RETURNING id;",
                ("name", $"{token}-item-1"),
                ("barcode", $"{token}-sku-1"));
            var itemIds = new List<long> { item1 };
            if (mixed)
            {
                itemIds.Add(Scalar(
                    "INSERT INTO items(name, barcode) VALUES (@name, @barcode) RETURNING id;",
                    ("name", $"{token}-item-2"),
                    ("barcode", $"{token}-sku-2")));
            }

            var locationId = Scalar(
                "INSERT INTO locations(code, name) VALUES (@code, @name) RETURNING id;",
                ("code", $"{token}-loc"),
                ("name", $"{token}-location"));
            var targetPartnerId = Scalar(
                "INSERT INTO partners(name, code, created_at) VALUES (@name, @code, @created) RETURNING id;",
                ("name", $"{token}-partner"),
                ("code", $"{token}-partner"),
                ("created", "2042-01-01T00:00:00"));
            var sourceOrderId = Scalar(
                "INSERT INTO orders(order_ref, order_type, status, created_at) VALUES (@ref, 'INTERNAL', 'IN_PROGRESS', @created) RETURNING id;",
                ("ref", $"{token}-source"),
                ("created", "2042-01-01T00:00:00"));
            var targetOrderId = Scalar(
                "INSERT INTO orders(order_ref, order_type, partner_id, status, created_at) VALUES (@ref, 'CUSTOMER', @partner_id, 'IN_PROGRESS', @created) RETURNING id;",
                ("ref", $"{token}-target"),
                ("partner_id", targetPartnerId),
                ("created", "2042-01-01T00:00:00"));

            var sourceLineIds = new List<long>();
            var targetLineIds = new Dictionary<long, long>();
            for (var index = 0; index < itemIds.Count; index++)
            {
                var qty = mixed ? (index == 0 ? 60d : 40d) : 100d;
                var itemId = itemIds[index];
                sourceLineIds.Add(Scalar(
                    @"INSERT INTO order_lines(
    order_id, item_id, qty_ordered, production_purpose, production_pallet_group)
VALUES(@order_id, @item_id, @qty, 'INTERNAL_STOCK', 'MIX-1')
RETURNING id;",
                    ("order_id", sourceOrderId),
                    ("item_id", itemId),
                    ("qty", qty)));
                targetLineIds[itemId] = Scalar(
                    @"INSERT INTO order_lines(
    order_id, item_id, qty_ordered, production_purpose)
VALUES(@order_id, @item_id, @qty, 'CUSTOMER_ORDER')
RETURNING id;",
                    ("order_id", targetOrderId),
                    ("item_id", itemId),
                    ("qty", qty));
            }

            var sourcePrdDocId = Scalar(
                @"INSERT INTO docs(doc_ref, type, status, created_at, order_id, order_ref)
VALUES(@ref, 'PRODUCTION_RECEIPT', 'DRAFT', @created, @order_id, @order_ref)
RETURNING id;",
                ("ref", $"{token}-source-prd"),
                ("created", "2042-01-01T00:00:00"),
                ("order_id", sourceOrderId),
                ("order_ref", $"{token}-source"));
            var targetPrdDocId = Scalar(
                @"INSERT INTO docs(doc_ref, type, status, created_at, order_id, order_ref)
VALUES(@ref, 'PRODUCTION_RECEIPT', 'DRAFT', @created, @order_id, @order_ref)
RETURNING id;",
                ("ref", $"{token}-target-prd"),
                ("created", "2042-01-01T00:00:00"),
                ("order_id", targetOrderId),
                ("order_ref", $"{token}-target"));

            var sourceDocLineIds = new List<long>();
            for (var index = 0; index < itemIds.Count; index++)
            {
                var qty = mixed ? (index == 0 ? 60d : 40d) : 100d;
                sourceDocLineIds.Add(Scalar(
                    @"INSERT INTO doc_lines(
    doc_id, order_line_id, production_purpose, item_id, qty, to_location_id, to_hu)
VALUES(@doc_id, @order_line_id, 'INTERNAL_STOCK', @item_id, @qty, @location_id, @hu)
RETURNING id;",
                    ("doc_id", sourcePrdDocId),
                    ("order_line_id", sourceLineIds[index]),
                    ("item_id", itemIds[index]),
                    ("qty", qty),
                    ("location_id", locationId),
                    ("hu", huCode)));
            }

            var productionPalletId = Scalar(
                @"INSERT INTO production_pallets(
    prd_doc_id, doc_line_id, order_id, order_line_id, item_id, hu_code,
    planned_qty, to_location_id, status, created_at)
VALUES(
    @prd_doc_id, @doc_line_id, @order_id, @order_line_id, @item_id, @hu,
    @planned_qty, @location_id, 'PLANNED', @created_at)
RETURNING id;",
                ("prd_doc_id", sourcePrdDocId),
                ("doc_line_id", sourceDocLineIds[0]),
                ("order_id", sourceOrderId),
                ("order_line_id", sourceLineIds[0]),
                ("item_id", itemIds[0]),
                ("hu", huCode),
                ("planned_qty", 100d),
                ("location_id", locationId),
                ("created_at", "2042-01-01T00:00:00"));

            for (var index = 0; index < itemIds.Count; index++)
            {
                var qty = mixed ? (index == 0 ? 60d : 40d) : 100d;
                Scalar(
                    @"INSERT INTO production_pallet_lines(
    production_pallet_id, doc_line_id, order_line_id, item_id,
    planned_qty, filled_qty, created_at)
VALUES(
    @production_pallet_id, @doc_line_id, @order_line_id, @item_id,
    @planned_qty, 0, @created_at)
RETURNING id;",
                    ("production_pallet_id", productionPalletId),
                    ("doc_line_id", sourceDocLineIds[index]),
                    ("order_line_id", sourceLineIds[index]),
                    ("item_id", itemIds[index]),
                    ("planned_qty", qty),
                    ("created_at", "2042-01-01T00:00:00"));
            }

            return new AdoptionFixture(
                connectionString,
                token,
                sourceOrderId,
                targetOrderId,
                sourceLineIds.ToArray(),
                targetLineIds,
                sourcePrdDocId,
                targetPrdDocId,
                productionPalletId,
                locationId,
                targetPartnerId,
                itemIds.ToArray(),
                sourceDocLineIds.ToArray(),
                huCode);
        }

        public ProductionPalletSelectedAdoption BuildSelectedAdoption(string expectedStatus = ProductionPalletStatus.Planned)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            var lines = new List<ProductionPalletSelectedAdoptionLine>();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT pll.doc_line_id, pll.order_line_id, pll.item_id, pll.planned_qty
FROM production_pallet_lines pll
WHERE pll.production_pallet_id = @pallet_id
ORDER BY pll.id;";
            command.Parameters.AddWithValue("pallet_id", ProductionPalletId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var itemId = reader.GetInt64(2);
                lines.Add(new ProductionPalletSelectedAdoptionLine
                {
                    DocLineId = reader.GetInt64(0),
                    SourceOrderLineId = reader.GetInt64(1),
                    TargetOrderLineId = TargetOrderLineIdsByItemId[itemId],
                    ItemId = itemId,
                    PlannedQty = reader.GetDouble(3)
                });
            }

            return new ProductionPalletSelectedAdoption
            {
                ProductionPalletId = ProductionPalletId,
                SourceOrderId = SourceOrderId,
                SourcePrdDocId = SourcePrdDocId,
                ExpectedStatus = expectedStatus,
                HuCode = HuCode,
                TargetOrderLineId = TargetOrderLineIdsByItemId[_itemIds[0]],
                Lines = lines
            };
        }

        public void SeedTargetReservation(long targetLineId, long itemId, double qty, string huCode)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO order_receipt_plan_lines(
    order_id, order_line_id, item_id, qty_planned, to_location_id, to_hu, sort_order)
VALUES(@order_id, @order_line_id, @item_id, @qty, @location_id, @hu, 0);";
            command.Parameters.AddWithValue("order_id", TargetOrderId);
            command.Parameters.AddWithValue("order_line_id", targetLineId);
            command.Parameters.AddWithValue("item_id", itemId);
            command.Parameters.AddWithValue("qty", qty);
            command.Parameters.AddWithValue("location_id", _locationId);
            command.Parameters.AddWithValue("hu", huCode);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public void AddUnexpectedLedgerForTargetHu()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            long itemId;
            using (var itemCommand = connection.CreateCommand())
            {
                itemCommand.CommandText =
                    "INSERT INTO items(name, barcode) VALUES (@name, @barcode) RETURNING id;";
                itemCommand.Parameters.AddWithValue("name", $"{Token}-unexpected-ledger-item");
                itemCommand.Parameters.AddWithValue("barcode", $"{Token}-unexpected-ledger-sku");
                itemId = Convert.ToInt64(itemCommand.ExecuteScalar());
                _extraItemIds.Add(itemId);
            }

            using var ledgerCommand = connection.CreateCommand();
            ledgerCommand.CommandText = @"
INSERT INTO ledger(ts, doc_id, item_id, location_id, qty_delta, hu_code, hu)
VALUES(@timestamp, @doc_id, @item_id, @location_id, 1, @hu, @hu);";
            ledgerCommand.Parameters.AddWithValue("doc_id", TargetPrdDocId);
            ledgerCommand.Parameters.AddWithValue("item_id", itemId);
            ledgerCommand.Parameters.AddWithValue("location_id", _locationId);
            ledgerCommand.Parameters.AddWithValue("hu", HuCode);
            ledgerCommand.Parameters.AddWithValue("timestamp", "2042-01-02T00:00:00");
            Assert.Equal(1, ledgerCommand.ExecuteNonQuery());
        }

        public void SetSingleSourceQty(double qty)
        {
            Assert.Single(SourceOrderLineIds);
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE order_lines SET qty_ordered = @qty WHERE id = @line_id;";
            command.Parameters.AddWithValue("qty", qty);
            command.Parameters.AddWithValue("line_id", SourceOrderLineIds.Single());
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public void DeleteSourcePrdDoc()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM docs WHERE id = @doc_id;";
            command.Parameters.AddWithValue("doc_id", SourcePrdDocId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public void SetPalletPrinted(DateTime printedAt)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE production_pallets
SET status = 'PRINTED',
    printed_at = @printed_at
WHERE id = @pallet_id;";
            command.Parameters.AddWithValue("printed_at", printedAt.ToString("O"));
            command.Parameters.AddWithValue("pallet_id", ProductionPalletId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public void MarkAdoptedPalletProducedAndClosed()
        {
            var filledAt = "2042-01-03T00:00:00Z";
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var fillLines = connection.CreateCommand())
            {
                fillLines.Transaction = transaction;
                fillLines.CommandText = @"
UPDATE production_pallet_lines
SET filled_qty = planned_qty,
    filled_at = @filled_at
WHERE production_pallet_id = @pallet_id;";
                fillLines.Parameters.AddWithValue("filled_at", filledAt);
                fillLines.Parameters.AddWithValue("pallet_id", ProductionPalletId);
                Assert.True(fillLines.ExecuteNonQuery() > 0);
            }

            using (var fillPallet = connection.CreateCommand())
            {
                fillPallet.Transaction = transaction;
                fillPallet.CommandText = @"
UPDATE production_pallets
SET status = 'FILLED',
    filled_at = @filled_at
WHERE id = @pallet_id;";
                fillPallet.Parameters.AddWithValue("filled_at", filledAt);
                fillPallet.Parameters.AddWithValue("pallet_id", ProductionPalletId);
                Assert.Equal(1, fillPallet.ExecuteNonQuery());
            }

            using (var closeDoc = connection.CreateCommand())
            {
                closeDoc.Transaction = transaction;
                closeDoc.CommandText = @"
UPDATE docs
SET status = 'CLOSED',
    closed_at = @filled_at
WHERE id = @doc_id;";
                closeDoc.Parameters.AddWithValue("filled_at", filledAt);
                closeDoc.Parameters.AddWithValue("doc_id", TargetPrdDocId);
                Assert.Equal(1, closeDoc.ExecuteNonQuery());
            }

            using (var ledger = connection.CreateCommand())
            {
                ledger.Transaction = transaction;
                ledger.CommandText = @"
INSERT INTO ledger(ts, doc_id, item_id, location_id, qty_delta, hu_code, hu)
SELECT @filled_at,
       @doc_id,
       item_id,
       @location_id,
       planned_qty,
       @hu,
       @hu
FROM production_pallet_lines
WHERE production_pallet_id = @pallet_id;";
                ledger.Parameters.AddWithValue("filled_at", filledAt);
                ledger.Parameters.AddWithValue("doc_id", TargetPrdDocId);
                ledger.Parameters.AddWithValue("location_id", _locationId);
                ledger.Parameters.AddWithValue("hu", HuCode);
                ledger.Parameters.AddWithValue("pallet_id", ProductionPalletId);
                Assert.True(ledger.ExecuteNonQuery() > 0);
            }

            transaction.Commit();
        }

        public OwnCustomerProducedStock AddOwnCustomerProducedStock(double qty)
        {
            Assert.Single(_itemIds);
            var itemId = _itemIds[0];
            var orderLineId = TargetOrderLineIdsByItemId[itemId];
            var huCode = $"{Token}-OWN-HU";
            var createdAt = "2042-01-04T00:00:00Z";

            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            long Scalar(string sql, params (string Name, object Value)[] parameters)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                foreach (var parameter in parameters)
                {
                    command.Parameters.AddWithValue(parameter.Name, parameter.Value);
                }

                return Convert.ToInt64(command.ExecuteScalar());
            }

            var prdDocId = Scalar(
                @"INSERT INTO docs(doc_ref, type, status, created_at, closed_at, order_id, order_ref)
VALUES(@ref, 'PRODUCTION_RECEIPT', 'CLOSED', @created_at, @created_at, @order_id, @order_ref)
RETURNING id;",
                ("ref", $"{Token}-own-prd"),
                ("created_at", createdAt),
                ("order_id", TargetOrderId),
                ("order_ref", $"{Token}-target"));
            var docLineId = Scalar(
                @"INSERT INTO doc_lines(
    doc_id, order_line_id, production_purpose, item_id, qty, to_location_id, to_hu)
VALUES(@doc_id, @order_line_id, 'CUSTOMER_ORDER', @item_id, @qty, @location_id, @hu)
RETURNING id;",
                ("doc_id", prdDocId),
                ("order_line_id", orderLineId),
                ("item_id", itemId),
                ("qty", qty),
                ("location_id", _locationId),
                ("hu", huCode));
            var palletId = Scalar(
                @"INSERT INTO production_pallets(
    prd_doc_id, doc_line_id, order_id, order_line_id, item_id, hu_code,
    planned_qty, to_location_id, status, filled_at, created_at)
VALUES(
    @prd_doc_id, @doc_line_id, @order_id, @order_line_id, @item_id, @hu,
    @qty, @location_id, 'FILLED', @created_at, @created_at)
RETURNING id;",
                ("prd_doc_id", prdDocId),
                ("doc_line_id", docLineId),
                ("order_id", TargetOrderId),
                ("order_line_id", orderLineId),
                ("item_id", itemId),
                ("hu", huCode),
                ("qty", qty),
                ("location_id", _locationId),
                ("created_at", createdAt));
            Scalar(
                @"INSERT INTO production_pallet_lines(
    production_pallet_id, doc_line_id, order_line_id, item_id,
    planned_qty, filled_qty, filled_at, created_at)
VALUES(
    @pallet_id, @doc_line_id, @order_line_id, @item_id,
    @qty, @qty, @created_at, @created_at)
RETURNING id;",
                ("pallet_id", palletId),
                ("doc_line_id", docLineId),
                ("order_line_id", orderLineId),
                ("item_id", itemId),
                ("qty", qty),
                ("created_at", createdAt));

            using (var ledger = connection.CreateCommand())
            {
                ledger.Transaction = transaction;
                ledger.CommandText = @"
INSERT INTO ledger(ts, doc_id, item_id, location_id, qty_delta, hu_code, hu)
VALUES(@created_at, @doc_id, @item_id, @location_id, @qty, @hu, @hu);";
                ledger.Parameters.AddWithValue("created_at", createdAt);
                ledger.Parameters.AddWithValue("doc_id", prdDocId);
                ledger.Parameters.AddWithValue("item_id", itemId);
                ledger.Parameters.AddWithValue("location_id", _locationId);
                ledger.Parameters.AddWithValue("qty", qty);
                ledger.Parameters.AddWithValue("hu", huCode);
                Assert.Equal(1, ledger.ExecuteNonQuery());
            }

            transaction.Commit();
            return new OwnCustomerProducedStock(itemId, huCode, prdDocId, palletId);
        }

        public void SetCurrentPalletStatus(string status)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE production_pallets SET status = @status WHERE id = @pallet_id;";
            command.Parameters.AddWithValue("status", status);
            command.Parameters.AddWithValue("pallet_id", ProductionPalletId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public void ConsumeSourceDemandAndDeleteOperationalSourceRows()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var deleteLines = connection.CreateCommand())
            {
                deleteLines.Transaction = transaction;
                deleteLines.CommandText = "DELETE FROM order_lines WHERE id = ANY(@ids);";
                deleteLines.Parameters.AddWithValue("ids", SourceOrderLineIds);
                deleteLines.ExecuteNonQuery();
            }

            using (var deleteDoc = connection.CreateCommand())
            {
                deleteDoc.Transaction = transaction;
                deleteDoc.CommandText = "DELETE FROM docs WHERE id = @doc_id;";
                deleteDoc.Parameters.AddWithValue("doc_id", SourcePrdDocId);
                Assert.Equal(1, deleteDoc.ExecuteNonQuery());
            }

            using (var mergeSource = connection.CreateCommand())
            {
                mergeSource.Transaction = transaction;
                mergeSource.CommandText = "UPDATE orders SET status = 'MERGED' WHERE id = @order_id;";
                mergeSource.Parameters.AddWithValue("order_id", SourceOrderId);
                Assert.Equal(1, mergeSource.ExecuteNonQuery());
            }

            transaction.Commit();
        }

        public int CountLedgerRowsForHu()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(*)
FROM ledger
WHERE UPPER(BTRIM(COALESCE(hu_code, hu, ''))) = UPPER(BTRIM(@hu));";
            command.Parameters.AddWithValue("hu", HuCode);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        public void DeleteSourceOrderLines()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM order_lines WHERE id = ANY(@ids);";
            command.Parameters.AddWithValue("ids", SourceOrderLineIds);
            command.ExecuteNonQuery();
        }

        public (long? OrderId, long PrdDocId, long? OrderLineId) ReadPalletOwnership()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT order_id, prd_doc_id, order_line_id
FROM production_pallets
WHERE id = @id;";
            command.Parameters.AddWithValue("id", ProductionPalletId);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            return (
                reader.IsDBNull(0) ? null : reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2));
        }

        public void Dispose()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
DELETE FROM order_coverage_transfers
WHERE source_order_id = @source_order_id OR target_order_id = @target_order_id;

DELETE FROM order_receipt_plan_lines
WHERE order_id = @target_order_id;

DELETE FROM ledger
WHERE doc_id IN (
    SELECT id FROM docs WHERE order_id = ANY(@order_ids)
);

DELETE FROM production_pallet_lines
WHERE production_pallet_id IN (
    SELECT id FROM production_pallets WHERE order_id = ANY(@order_ids)
);

DELETE FROM production_pallets
WHERE order_id = ANY(@order_ids);

DELETE FROM doc_lines
WHERE doc_id IN (
    SELECT id FROM docs WHERE order_id = ANY(@order_ids)
);

DELETE FROM docs
WHERE order_id = ANY(@order_ids);

DELETE FROM order_lines
WHERE order_id = ANY(@order_ids);

DELETE FROM orders
WHERE id = ANY(@order_ids);

DELETE FROM partners
WHERE id = @partner_id;

DELETE FROM locations
WHERE id = @location_id;

DELETE FROM items
WHERE id = ANY(@item_ids);";
            command.Parameters.AddWithValue("source_order_id", SourceOrderId);
            command.Parameters.AddWithValue("target_order_id", TargetOrderId);
            command.Parameters.AddWithValue("pallet_id", ProductionPalletId);
            command.Parameters.AddWithValue("doc_line_ids", _sourceDocLineIds);
            command.Parameters.AddWithValue("doc_ids", new[] { SourcePrdDocId, TargetPrdDocId });
            command.Parameters.AddWithValue("order_ids", new[] { SourceOrderId, TargetOrderId });
            command.Parameters.AddWithValue("partner_id", _targetPartnerId);
            command.Parameters.AddWithValue("location_id", _locationId);
            command.Parameters.AddWithValue("hu", HuCode);
            command.Parameters.AddWithValue("item_ids", _itemIds.Concat(_extraItemIds).ToArray());
            command.ExecuteNonQuery();
        }

        public sealed record OwnCustomerProducedStock(
            long ItemId,
            string HuCode,
            long PrdDocId,
            long PalletId);
    }
}

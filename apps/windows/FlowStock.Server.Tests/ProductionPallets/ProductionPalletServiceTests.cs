using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;
using System.Text.RegularExpressions;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletServiceTests
{
    [Fact]
    public void GetFillingOrders_DoesNotReturnOrderWithoutPreparedPallets()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);

        Assert.Empty(service.GetFillingOrders());
    }

    [Fact]
    public void PlanOrder_CreatesProductionPalletsWithServerGeneratedHus_AndNoLedger()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10);

        Assert.Equal(10, result.OrderId);
        Assert.Equal("056", result.OrderRef);
        Assert.StartsWith("PRD-", result.PrdDocRef, StringComparison.Ordinal);
        Assert.Equal(2, result.Summary.PlannedPalletCount);
        Assert.Equal(1200, result.Summary.PlannedQty);
        Assert.Equal(0, result.Summary.FilledPalletCount);
        Assert.Equal(1200, result.Summary.RemainingQty);
        Assert.Empty(harness.LedgerEntries);
        var pallets = harness.Store.GetProductionPalletsByDoc(result.PrdDocId);
        Assert.Equal(2, pallets.Count);
        Assert.All(pallets, pallet => Assert.Matches("^HU-[0-9]{7}$", pallet.HuCode));
        Assert.Equal(2, pallets.Select(pallet => pallet.HuCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Single(service.GetFillingOrders());
    }

    [Fact]
    public void CancelOrderPlan_KeepsEmptyDraftPrdForReuse()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);

        var plan = service.PlanOrder(10);
        service.CancelOrderPlan(10);

        var prdAfterCancel = harness.Store.GetDoc(plan.PrdDocId);
        Assert.NotNull(prdAfterCancel);
        Assert.Equal(DocStatus.Draft, prdAfterCancel.Status);
        Assert.False(harness.Store.HasProductionPallets(plan.PrdDocId));
        Assert.Empty(harness.Store.GetDocLines(plan.PrdDocId));
    }

    [Fact]
    public void PlanOrder_IsIdempotent()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);

        var first = service.PlanOrder(10);
        var firstHuCodes = harness.Store.GetProductionPalletsByDoc(first.PrdDocId).Select(pallet => pallet.HuCode).ToArray();
        var second = service.PlanOrder(10);
        var secondHuCodes = harness.Store.GetProductionPalletsByDoc(second.PrdDocId).Select(pallet => pallet.HuCode).ToArray();

        Assert.Equal(first.PrdDocId, second.PrdDocId);
        Assert.Equal(first.PrdDocRef, second.PrdDocRef);
        Assert.False(first.WasExisting);
        Assert.True(second.WasExisting);
        Assert.Equal(1, harness.Store.GetDocsByOrder(10).Count(doc => doc.Type == DocType.ProductionReceipt));
        Assert.Equal(2, harness.Store.GetDocLines(first.PrdDocId).Count);
        Assert.Equal(2, harness.Store.GetProductionPalletsByDoc(first.PrdDocId).Count);
        Assert.Equal(2, secondHuCodes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.NotEqual(firstHuCodes, secondHuCodes);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void PlanOrder_GeneratesHuAfterExistingHuNumber()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        harness.SeedHu(new HuRecord
        {
            Id = 42,
            Code = "HU-0000042",
            Status = "OPEN",
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10);

        var pallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(result.PrdDocId));
        Assert.Matches("^HU-[0-9]{7}$", pallet.HuCode);
        Assert.True(long.Parse(Regex.Match(pallet.HuCode, "[0-9]+").Value) > 42);
    }

    [Fact]
    public void PlanOrder_TwoMixedGroups_CreatesTwoDistinctHus_IgnoringMaxQtyPerHu()
    {
        var harness = CreateHarnessWithFourLineTwoMixedGroups();
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10);

        Assert.Equal(2, result.Summary.PlannedPalletCount);
        Assert.Equal(2400, result.Summary.PlannedQty);
        var pallets = harness.Store.GetProductionPalletsByDoc(result.PrdDocId).OrderBy(pallet => pallet.Id).ToArray();
        Assert.Equal(2, pallets.Length);
        Assert.Equal(2, pallets.Select(pallet => pallet.HuCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(pallets, pallet =>
        {
            Assert.True(pallet.IsMixedPallet);
            Assert.Equal(2, pallet.Lines.Count);
        });
        Assert.Equal(new[] { 101L, 102L }, pallets[0].Lines.Select(line => line.OrderLineId!.Value).Order().ToArray());
        Assert.Equal(new[] { 103L, 104L }, pallets[1].Lines.Select(line => line.OrderLineId!.Value).Order().ToArray());
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void PlanOrder_SingleMixedGroupOverCapacity_SucceedsWithOneHuAndAllLines()
    {
        var harness = CreateHarnessWithFourLineSingleMixedGroupOverCapacity();
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10);

        Assert.Equal(1, result.Summary.PlannedPalletCount);
        Assert.Equal(2400, result.Summary.PlannedQty);
        var pallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(result.PrdDocId));
        Assert.True(pallet.IsMixedPallet);
        Assert.Equal(4, pallet.Lines.Count);
        Assert.Equal(new[] { 101L, 102L, 103L, 104L }, pallet.Lines.Select(line => line.OrderLineId!.Value).Order().ToArray());
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void PlanOrder_AfterFilledMixedPlan_DoesNotReassignHu()
    {
        var harness = CreateHarnessWithFourLineTwoMixedGroups();
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        var pallets = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).OrderBy(pallet => pallet.Id).ToArray();
        var huCodesBefore = pallets.Select(pallet => pallet.HuCode).ToArray();

        service.Fill(pallets[0].HuCode, "TSD-01");
        var ex = Assert.Throws<InvalidOperationException>(() => service.PlanOrder(10));

        Assert.Equal("План паллет уже напечатан или наполнен. Переназначение HU запрещено.", ex.Message);
        var huCodesAfter = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .OrderBy(pallet => pallet.Id)
            .Select(pallet => pallet.HuCode)
            .ToArray();
        Assert.Equal(huCodesBefore, huCodesAfter);
    }

    [Fact]
    public void ScanAndFill_TwoMixedGroups_FillsWithoutWritingLedger()
    {
        var harness = CreateHarnessWithFourLineTwoMixedGroups();
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        var pallets = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).OrderBy(pallet => pallet.Id).ToArray();

        foreach (var pallet in pallets)
        {
            var scan = service.Scan(10, plan.PrdDocId, pallet.HuCode);
            Assert.True(scan.Success);
            Assert.True(scan.IsMixedPallet);
            Assert.Equal(2, scan.Lines.Count);

            var fill = service.Fill(pallet.HuCode, "TSD-01");
            Assert.True(fill.Success);
            Assert.False(fill.AlreadyFilled);
        }

        Assert.Empty(harness.LedgerEntries);
        Assert.All(harness.Store.GetProductionPalletsByDoc(plan.PrdDocId), pallet => Assert.Equal(ProductionPalletStatus.Filled, pallet.Status));
    }

    [Fact]
    public void PlanOrder_MixedLines_CreateOneHuWithMultipleComponentLines_AndNoLedger()
    {
        var harness = CreateHarnessWithMixedOrderOnly();
        var service = new ProductionPalletService(harness.Store);

        var result = service.PlanOrder(10);

        Assert.Equal(1, result.Summary.PlannedPalletCount);
        Assert.Equal(500, result.Summary.PlannedQty);
        Assert.Empty(harness.LedgerEntries);
        var pallet = Assert.Single(harness.Store.GetProductionPalletsByDoc(result.PrdDocId));
        Assert.Matches("^HU-[0-9]{7}$", pallet.HuCode);
        Assert.True(pallet.IsMixedPallet);
        Assert.Equal(2, pallet.Lines.Count);
        Assert.Equal(new[] { 101L, 102L }, pallet.Lines.Select(line => line.OrderLineId!.Value).Order().ToArray());
        Assert.Single(harness.Store.GetProductionPalletsByDoc(result.PrdDocId).Select(p => p.HuCode).Distinct());
    }

    [Fact]
    public void PlanOrder_MixedLines_IsIdempotent()
    {
        var harness = CreateHarnessWithMixedOrderOnly();
        var service = new ProductionPalletService(harness.Store);

        var first = service.PlanOrder(10);
        var firstHu = harness.Store.GetProductionPalletsByDoc(first.PrdDocId).Single().HuCode;
        var second = service.PlanOrder(10);
        var secondHu = harness.Store.GetProductionPalletsByDoc(second.PrdDocId).Single().HuCode;

        Assert.Equal(first.PrdDocId, second.PrdDocId);
        Assert.Single(harness.Store.GetProductionPalletsByDoc(first.PrdDocId));
        Assert.Equal(2, harness.Store.GetDocLines(first.PrdDocId).Count);
        Assert.NotEqual(firstHu, secondHu);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void PlanOrder_AfterMixedCheckboxesCleared_RebuildsAsSeparateHus()
    {
        var harness = CreateHarnessWithMixedOrderOnly();
        var service = new ProductionPalletService(harness.Store);
        var first = service.PlanOrder(10);

        harness.Store.UpdateOrderLineProductionPalletGroup(101, null);
        harness.Store.UpdateOrderLineProductionPalletGroup(102, null);
        var second = service.PlanOrder(10);

        Assert.Equal(first.PrdDocId, second.PrdDocId);
        var pallets = harness.Store.GetProductionPalletsByDoc(second.PrdDocId).OrderBy(pallet => pallet.Id).ToArray();
        Assert.Equal(2, pallets.Length);
        Assert.Equal(2, pallets.Select(pallet => pallet.HuCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(pallets, pallet => Assert.False(pallet.IsMixedPallet));
        Assert.Equal(new[] { 101L, 102L }, pallets.SelectMany(pallet => pallet.Lines).Select(line => line.OrderLineId!.Value).Order().ToArray());
        Assert.Equal(2, harness.Store.GetDocLines(second.PrdDocId).Count);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void GetPrintRows_MixedPallet_ReturnsOneRowWithComposition()
    {
        var harness = CreateHarnessWithMixedOrderOnly();
        var service = new ProductionPalletService(harness.Store);
        service.PlanOrder(10);

        var row = Assert.Single(service.GetPrintRows(10));

        Assert.True(row.IsMixedPallet);
        Assert.Equal("Микс-паллета", row.ItemName);
        Assert.Equal(500, row.Qty);
        Assert.Contains("Товар", row.Composition);
        Assert.Contains("Добавка", row.Composition);
        Assert.Equal(2, row.Lines.Count);
    }

    [Fact]
    public void ScanAndFill_MixedPallet_ReturnsCompositionAndFillsWithoutLedger()
    {
        var harness = CreateHarnessWithMixedOrderOnly();
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        var hu = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Single().HuCode;

        var scan = service.Scan(10, plan.PrdDocId, hu);
        var firstFill = service.Fill(hu, "TSD-01");
        var secondFill = service.Fill(hu, "TSD-01");

        Assert.True(scan.Success);
        Assert.True(scan.IsMixedPallet);
        Assert.Equal(2, scan.Lines.Count);
        Assert.True(firstFill.Success);
        Assert.False(firstFill.AlreadyFilled);
        Assert.True(secondFill.Success);
        Assert.True(secondFill.AlreadyFilled);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void PlanOrder_RequiresPalletCapacity()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: null);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<InvalidOperationException>(() => service.PlanOrder(10));

        Assert.Equal("Не задано количество на паллете для номенклатуры", ex.Message);
        Assert.Empty(harness.Store.GetDocsByOrder(10));
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void GetFillingOrders_ReturnsOrderWithPreparedPallets_WhenHasUnfilledPallets()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 2);
        var service = new ProductionPalletService(harness.Store);

        var order = Assert.Single(service.GetFillingOrders());

        Assert.Equal(10, order.OrderId);
        Assert.Equal("056", order.OrderRef);
        Assert.Equal(20, order.PrdDocId);
        Assert.Equal(6, order.Summary.PlannedPalletCount);
        Assert.Equal(2, order.Summary.FilledPalletCount);
        Assert.Equal(4, order.Summary.RemainingPalletCount);
        Assert.Equal(2400, order.Summary.RemainingQty);
    }

    [Fact]
    public void GetFillingOrders_DoesNotReturnOrder_WhenAllPreparedPalletsFilled()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 6);
        var service = new ProductionPalletService(harness.Store);

        Assert.Empty(service.GetFillingOrders());
    }

    [Fact]
    public void GetFillingContext_ReturnsPreparedContext_WithoutCreatingPlan()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 2);
        var service = new ProductionPalletService(harness.Store);

        var context = service.GetFillingContext(10);

        Assert.Equal(10, context.OrderId);
        Assert.Equal("056", context.OrderRef);
        Assert.Equal(20, context.PrdDocId);
        Assert.Equal("PRD-2026-000001", context.PrdDocRef);
        Assert.Equal(6, context.Document.Summary.PlannedPalletCount);
        Assert.Equal(1, harness.Store.GetDocsByOrder(10).Count(doc => doc.Type == DocType.ProductionReceipt));
        Assert.Single(harness.Store.GetDocLines(20));
        Assert.Equal(6, harness.Store.GetProductionPalletsByDoc(20).Count);
    }

    [Fact]
    public void TsdFillingChain_PlannedOrder_ScansAndFillsAllPalletsWithoutDuplicates()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);

        var plan = service.PlanOrder(10);
        var plannedHus = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId)
            .OrderBy(pallet => pallet.Id)
            .Select(pallet => pallet.HuCode)
            .ToArray();
        var order = Assert.Single(service.GetFillingOrders());
        var context = service.GetFillingContext(order.OrderId);

        Assert.Equal(plan.PrdDocId, context.PrdDocId);
        Assert.Equal(2, context.Document.Summary.PlannedPalletCount);
        Assert.Equal(2, context.Document.Summary.RemainingPalletCount);
        Assert.Equal(plannedHus, context.Document.Pallets.Select(pallet => pallet.HuCode).ToArray());

        var firstScan = service.Scan(context.OrderId, context.PrdDocId, plannedHus[0]);
        var firstFill = service.Fill(plannedHus[0], "TSD-01", context.OrderId, context.PrdDocId);
        var afterFirst = service.GetFillingContext(context.OrderId);
        var secondScan = service.Scan(context.OrderId, context.PrdDocId, plannedHus[1]);
        var secondFill = service.Fill(plannedHus[1], "TSD-01", context.OrderId, context.PrdDocId);
        var duplicateFill = service.Fill(plannedHus[1], "TSD-01", context.OrderId, context.PrdDocId);

        Assert.True(firstScan.Success);
        Assert.Equal(1, firstScan.PalletIndex);
        Assert.Equal(2, firstScan.PalletCount);
        Assert.True(firstFill.Success);
        Assert.False(firstFill.AlreadyFilled);
        Assert.Equal(1, firstFill.Document?.Summary.FilledPalletCount);
        Assert.Equal(1, afterFirst.Document.Summary.RemainingPalletCount);
        Assert.True(secondScan.Success);
        Assert.Equal(2, secondScan.PalletIndex);
        Assert.True(secondFill.Success);
        Assert.False(secondFill.AlreadyFilled);
        Assert.Equal(2, secondFill.Document?.Summary.FilledPalletCount);
        Assert.True(duplicateFill.Success);
        Assert.True(duplicateFill.AlreadyFilled);
        Assert.Empty(service.GetFillingOrders());
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void TsdFillingChain_MixedOrder_ReturnsCompositionAndFillsWholeHu()
    {
        var harness = CreateHarnessWithMixedOrderOnly();
        var service = new ProductionPalletService(harness.Store);

        var plan = service.PlanOrder(10);
        var context = service.GetFillingContext(10);
        var hu = Assert.Single(context.Document.Pallets).HuCode;

        var scan = service.Scan(context.OrderId, context.PrdDocId, hu);
        var fill = service.Fill(hu, "TSD-01", context.OrderId, context.PrdDocId);
        var repeated = service.Fill(hu, "TSD-01", context.OrderId, context.PrdDocId);

        Assert.Equal(plan.PrdDocId, context.PrdDocId);
        Assert.True(scan.Success);
        Assert.True(scan.IsMixedPallet);
        Assert.Equal("Микс-паллета", scan.ItemName);
        Assert.Equal(2, scan.Lines.Count);
        Assert.Equal(500, scan.Lines.Sum(line => line.Qty));
        Assert.True(fill.Success);
        Assert.False(fill.AlreadyFilled);
        Assert.Equal(1, fill.Document?.Summary.FilledPalletCount);
        Assert.Equal(0, fill.Document?.Summary.RemainingPalletCount);
        Assert.True(repeated.Success);
        Assert.True(repeated.AlreadyFilled);
        Assert.Empty(service.GetFillingOrders());
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void GetFillingContext_WithoutPreparedPallets_ReturnsClearError()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<InvalidOperationException>(() => service.GetFillingContext(10));

        Assert.Equal(
            "Для заказа не сформирован план паллет. Сформируйте и напечатайте паллетные этикетки перед наполнением.",
            ex.Message);
        Assert.Empty(harness.Store.GetDocsByOrder(10));
    }

    [Fact]
    public void ScanPallet_KnownHu_ReturnsPreviewWithoutLedger()
    {
        var harness = CreateHarnessWithSinglePallet(ProductionPalletStatus.Planned);
        var service = new ProductionPalletService(harness.Store);

        var result = service.Scan(orderId: 10, prdDocId: 20, huCode: "HU-000001");

        Assert.True(result.Success);
        Assert.False(result.AlreadyFilled);
        Assert.Equal("056", result.OrderRef);
        Assert.Equal("PRD-2026-000001", result.PrdDocRef);
        Assert.Equal("HU-000001", result.HuCode);
        Assert.Equal("Товар", result.ItemName);
        Assert.Equal(600, result.PlannedQty);
        Assert.Equal(1, result.PalletIndex);
        Assert.Equal(1, result.PalletCount);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void ScanPallet_WrongSelectedOrderOrPrd_IsRejected()
    {
        var harness = CreateHarnessWithSinglePallet(ProductionPalletStatus.Planned);
        var service = new ProductionPalletService(harness.Store);

        var result = service.Scan(orderId: 999, prdDocId: 20, huCode: "HU-000001");

        Assert.False(result.Success);
        Assert.Equal("Эта паллета относится к другому заказу", result.Error);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void ScanPallet_FilledHu_ReturnsAlreadyFilledStateWithoutLedger()
    {
        var harness = CreateHarnessWithSinglePallet(ProductionPalletStatus.Filled);
        var service = new ProductionPalletService(harness.Store);

        var result = service.Scan(orderId: 10, prdDocId: 20, huCode: "HU-000001");

        Assert.True(result.Success);
        Assert.True(result.AlreadyFilled);
        Assert.Equal(ProductionPalletStatus.Filled, result.PalletStatus);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void ScanPallet_UnknownHu_IsRejectedWithoutLedger()
    {
        var harness = CreateHarnessWithSinglePallet(ProductionPalletStatus.Planned);
        var service = new ProductionPalletService(harness.Store);

        var result = service.Scan(orderId: 10, prdDocId: 20, huCode: "HU-404");

        Assert.False(result.Success);
        Assert.Equal("Паллета не найдена в плане выпуска", result.Error);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void ScanPallet_CancelledHu_IsRejectedWithoutLedger()
    {
        var harness = CreateHarnessWithSinglePallet(ProductionPalletStatus.Cancelled);
        var service = new ProductionPalletService(harness.Store);

        var result = service.Scan(orderId: 10, prdDocId: 20, huCode: "HU-000001");

        Assert.False(result.Success);
        Assert.Equal("Паллета отменена", result.Error);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void FillPallet_PostsLedgerOnce_AndRepeatedScanIsIdempotent()
    {
        var harness = CreateHarnessWithSinglePallet(ProductionPalletStatus.Planned);
        var service = new ProductionPalletService(harness.Store);

        var first = service.Fill("HU-000001", "TSD-01");
        var second = service.Fill("HU-000001", "TSD-01");

        Assert.True(first.Success);
        Assert.False(first.AlreadyFilled);
        Assert.True(second.Success);
        Assert.True(second.AlreadyFilled);
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(1, first.Document?.Summary.FilledPalletCount);
        Assert.Equal(600, first.Document?.Summary.FilledQty);
        Assert.Equal(0, first.Document?.Summary.RemainingQty);
    }

    [Fact]
    public void FillPallet_RejectsOverproduction()
    {
        var harness = new CloseDocumentHarness();
        SeedBase(harness, orderQty: 1000, plannedQty: 300, huCode: "HU-000002");
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 1,
            PrdDocId = 20,
            DocLineId = 201,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 100,
            ItemName = "Товар",
            HuCode = "HU-000001",
            PlannedQty = 800,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = ProductionPalletStatus.Filled,
            FilledAt = new DateTime(2026, 5, 13, 10, 0, 0),
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });
        harness.SeedProductionPallet(BuildPallet(id: 2, huCode: "HU-000002", plannedQty: 300));
        var service = new ProductionPalletService(harness.Store);

        var result = service.Fill("HU-000002", "TSD-01");

        Assert.False(result.Success);
        Assert.Equal("Выпуск превышает остаток по строке заказа", result.Error);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void Get_ReturnsProductionPalletSummary()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 2);
        var service = new ProductionPalletService(harness.Store);

        var document = service.Get(20);

        Assert.Equal(6, document.Summary.PlannedPalletCount);
        Assert.Equal(3600, document.Summary.PlannedQty);
        Assert.Equal(2, document.Summary.FilledPalletCount);
        Assert.Equal(1200, document.Summary.FilledQty);
        Assert.Equal(4, document.Summary.RemainingPalletCount);
        Assert.Equal(2400, document.Summary.RemainingQty);
        var line = Assert.Single(document.Lines);
        Assert.Equal(3600, line.OrderedQty);
        Assert.Equal(6, line.PlannedPalletCount);
        Assert.Equal(2, line.FilledPalletCount);
    }

    [Fact]
    public void GetPrintRows_ReturnsPreparedPalletLabelRows()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 0);
        var service = new ProductionPalletService(harness.Store);

        var rows = service.GetPrintRows(10);

        Assert.Equal(6, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("056", row.OrderRef);
            Assert.Equal("ПЕЧАГИН ПРОДУКТ", row.ClientName);
            Assert.Equal("PRD-2026-000001", row.PrdRef);
            Assert.Equal("Товар", row.ItemName);
            Assert.Equal("Печагин", row.Brand);
            Assert.Equal(600, row.Qty);
            Assert.Equal("шт", row.Uom);
            Assert.Equal("MAIN", row.StoragePlace);
            Assert.Equal(new DateTime(2026, 5, 13), row.ProductionDate);
        });
        Assert.Equal(1, rows[0].PalletNo);
        Assert.Equal(6, rows[0].PalletCount);
        Assert.Equal("HU-000001", rows[0].HuCode);
        Assert.Equal(6, rows[^1].PalletNo);
        Assert.Equal("HU-000006", rows[^1].HuCode);
    }

    [Fact]
    public void GetPrintRows_ShippedOrderWithoutOpenPrd_UsesLatestProductionReceiptWithPallets()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 6);
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "056",
            Type = OrderType.Internal,
            Status = OrderStatus.Shipped,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedDoc(new Doc
        {
            Id = 20,
            DocRef = "PRD-2026-000001",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Closed,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0),
            ClosedAt = new DateTime(2026, 5, 13, 12, 0, 0)
        });
        var service = new ProductionPalletService(harness.Store);

        var rows = service.GetPrintRows(10);

        Assert.Equal(6, rows.Count);
        Assert.All(rows, row => Assert.Equal("PRD-2026-000001", row.PrdRef));
        Assert.Equal("HU-000001", rows[0].HuCode);
        Assert.Equal("HU-000006", rows[^1].HuCode);
    }

    [Fact]
    public void GetPrintRows_WithoutPreparedPlan_ReturnsClearError()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<InvalidOperationException>(() => service.GetPrintRows(10));

        Assert.Equal("Сначала сформируйте план паллет", ex.Message);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void GetPrintRows_DoesNotCreateHuOrLedger()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 0);
        var service = new ProductionPalletService(harness.Store);
        var before = harness.Store.GetProductionPalletsByDoc(20).Select(pallet => pallet.HuCode).ToArray();

        _ = service.GetPrintRows(10);

        var after = harness.Store.GetProductionPalletsByDoc(20).Select(pallet => pallet.HuCode).ToArray();
        Assert.Equal(before, after);
        Assert.Equal(6, harness.Store.GetProductionPalletsByDoc(20).Count);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void MarkPrinted_ChangesOnlyPlannedPallets()
    {
        var harness = CreateHarnessWithSixPallets(filledCount: 1);
        var service = new ProductionPalletService(harness.Store);

        var updated = service.MarkPrinted(10, new DateTime(2026, 5, 13, 11, 0, 0));

        Assert.Equal(5, updated);
        var pallets = harness.Store.GetProductionPalletsByDoc(20);
        Assert.Equal(ProductionPalletStatus.Filled, pallets[0].Status);
        Assert.All(pallets.Skip(1), pallet => Assert.Equal(ProductionPalletStatus.Printed, pallet.Status));
        Assert.Empty(harness.LedgerEntries);
    }

    private static CloseDocumentHarness CreateHarnessWithSinglePallet(string status)
    {
        var harness = new CloseDocumentHarness();
        SeedBase(harness, orderQty: 600, plannedQty: 600, huCode: "HU-000001");
        harness.SeedProductionPallet(BuildPallet(id: 1, huCode: "HU-000001", plannedQty: 600, status: status));
        return harness;
    }

    private static CloseDocumentHarness CreateHarnessWithOrderOnly(double orderQty, double? maxQtyPerHu)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар",
            BaseUom = "шт",
            MaxQtyPerHu = maxQtyPerHu
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "056",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = orderQty
        });
        return harness;
    }

    [Fact]
    public void FillPallet_WrongSelectedOrder_IsRejected()
    {
        var harness = CreateHarnessWithSinglePallet(ProductionPalletStatus.Planned);
        var service = new ProductionPalletService(harness.Store);

        var result = service.Fill("HU-000001", "TSD-01", orderId: 999, prdDocId: 20);

        Assert.False(result.Success);
        Assert.Equal("Эта паллета относится к другому заказу", result.Error);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void PlanOrder_AfterPrintedPlan_DoesNotReassignHu()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        var huBeforePrint = harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Single().HuCode;

        service.MarkPrinted(10, new DateTime(2026, 5, 13, 11, 0, 0));
        var ex = Assert.Throws<InvalidOperationException>(() => service.PlanOrder(10));

        Assert.Equal("План паллет уже напечатан или наполнен. Переназначение HU запрещено.", ex.Message);
        Assert.Equal(huBeforePrint, harness.Store.GetProductionPalletsByDoc(plan.PrdDocId).Single().HuCode);
        Assert.Empty(harness.LedgerEntries);
    }

    [Fact]
    public void CancelOrderPlan_RemovesPlannedPallets_AndAllowsReplan()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);

        var cancel = service.CancelOrderPlan(10);

        Assert.Equal(plan.PrdDocId, cancel.PrdDocId);
        Assert.Equal(2, cancel.RemovedPalletCount);
        Assert.Equal(2, cancel.RemovedLineCount);
        Assert.False(harness.Store.HasProductionPallets(plan.PrdDocId));
        Assert.Empty(harness.Store.GetDocLines(plan.PrdDocId));
        Assert.Empty(harness.LedgerEntries);

        var replan = service.PlanOrder(10);
        Assert.Equal(plan.PrdDocId, replan.PrdDocId);
        Assert.Equal(2, replan.Summary.PlannedPalletCount);
        Assert.Equal(1200, replan.Summary.PlannedQty);
    }

    [Fact]
    public void CancelOrderPlan_AllowsPrintedPallets()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        service.MarkPrinted(10, new DateTime(2026, 5, 13, 11, 0, 0));

        var cancel = service.CancelOrderPlan(10);

        Assert.Equal(1, cancel.RemovedPalletCount);
        Assert.False(harness.Store.HasProductionPallets(plan.PrdDocId));
        var replan = service.PlanOrder(10);
        Assert.Equal(1, replan.Summary.PlannedPalletCount);
    }

    [Fact]
    public void CancelOrderPlan_RejectsFilledPallet()
    {
        var harness = CreateHarnessWithSinglePallet(ProductionPalletStatus.Filled);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<InvalidOperationException>(() => service.CancelOrderPlan(10));

        Assert.Equal("Нельзя удалить план паллет: есть уже наполненные паллеты.", ex.Message);
    }

    [Fact]
    public void CancelOrderPlan_RejectsClosedProductionReceipt()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 600, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        var doc = harness.Store.GetDoc(plan.PrdDocId)!;
        harness.Store.UpdateDocStatus(doc.Id, DocStatus.Closed, new DateTime(2026, 5, 13, 12, 0, 0));

        var ex = Assert.Throws<InvalidOperationException>(() => service.CancelOrderPlan(10));

        Assert.Equal("Нельзя удалить план паллет: выпуск уже закрыт.", ex.Message);
    }

    [Fact]
    public void CancelOrderPlan_AfterQtyChange_ReplansByCurrentQty()
    {
        var harness = CreateHarnessWithOrderOnly(orderQty: 1200, maxQtyPerHu: 600);
        var service = new ProductionPalletService(harness.Store);
        var plan = service.PlanOrder(10);
        service.CancelOrderPlan(10);

        var orderLine = harness.Store.GetOrderLines(10).Single();
        harness.Store.UpdateOrderLineQty(orderLine.Id, 600);

        var replan = service.PlanOrder(10);
        Assert.Equal(plan.PrdDocId, replan.PrdDocId);
        Assert.Equal(1, replan.Summary.PlannedPalletCount);
        Assert.Equal(600, replan.Summary.PlannedQty);
        Assert.Single(harness.Store.GetDocLines(plan.PrdDocId));
    }

    [Fact]
    public void AdoptPlanFromInternal_MovesPlannedPalletsToCustomer()
    {
        var harness = CreateHarnessForAdopt();
        var service = new ProductionPalletService(harness.Store);

        var result = service.AdoptPlanFromInternal(targetCustomerOrderId: 67, sourceInternalOrderId: 66);

        Assert.True(result.Success);
        Assert.Equal(162, result.SourcePrdDocId);
        Assert.True(result.TargetPrdDocId > 0);
        Assert.Equal(2, result.TransferredPalletCount);
        Assert.Equal(2, result.TransferredLineCount);
        Assert.Equal(new[] { "HU-0000462", "HU-0000463" }, result.TransferredHuCodes.Order().ToArray());
        Assert.False(harness.Store.HasProductionPallets(162));
        Assert.Empty(harness.Store.GetDocLines(162));
        Assert.Null(harness.Store.GetDoc(162));
        Assert.DoesNotContain(harness.Store.GetDocsByOrder(66), doc => doc.Id == 162);
        var targetPallets = harness.Store.GetProductionPalletsByDoc(result.TargetPrdDocId);
        Assert.Equal(2, targetPallets.Count);
        Assert.All(targetPallets, pallet =>
        {
            Assert.Equal(67, pallet.OrderId);
            Assert.Equal(172, pallet.OrderLineId);
            Assert.Equal(result.TargetPrdDocId, pallet.PrdDocId);
        });
        Assert.All(harness.Store.GetDocLines(result.TargetPrdDocId), line =>
        {
            Assert.Equal(172, line.OrderLineId);
            Assert.Equal(ProductionLinePurpose.CustomerOrder, line.ProductionPurpose);
        });
        Assert.Contains(harness.Store.GetActiveProductionPalletWorkItems(), item =>
            item.OrderId == 67
            && item.Summary.PlannedPalletCount == 2
            && item.Summary.FilledPalletCount == 0);
        Assert.Equal("MERGED", result.SourceOrderStatus);
        Assert.True(result.SourceOrderCommentUpdated);
        Assert.Equal(OrderStatus.Merged, harness.Store.GetOrder(66)?.Status);
        Assert.Contains("Объединён с заказом №067", harness.Store.GetOrder(66)?.Comment ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Store.GetActiveProductionPalletWorkItems(), item => item.OrderId == 66);
    }

    [Fact]
    public void AdoptPlanFromInternal_RejectsFilledPallet()
    {
        var harness = CreateHarnessForAdopt(ProductionPalletStatus.Filled);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<ProductionPalletPlanAdoptionException>(() => service.AdoptPlanFromInternal(67, 66));

        Assert.Equal("SOURCE_HAS_FILLED_PALLETS", ex.Code);
    }

    [Fact]
    public void AdoptPlanFromInternal_RejectsClosedSourcePrd()
    {
        var harness = CreateHarnessForAdopt(sourceDocStatus: DocStatus.Closed);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<ProductionPalletPlanAdoptionException>(() => service.AdoptPlanFromInternal(67, 66));

        Assert.Equal("SOURCE_PRD_CLOSED", ex.Code);
    }

    [Fact]
    public void AdoptPlanFromInternal_RejectsSourceLedger()
    {
        var harness = CreateHarnessForAdopt();
        harness.Store.AddLedgerEntry(new LedgerEntry
        {
            DocId = 162,
            ItemId = 100,
            LocationId = 1,
            QtyDelta = 600,
            HuCode = "HU-0000462",
            Timestamp = DateTime.Now
        });
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<ProductionPalletPlanAdoptionException>(() => service.AdoptPlanFromInternal(67, 66));

        Assert.Equal("SOURCE_HAS_LEDGER", ex.Code);
    }

    [Fact]
    public void AdoptPlanFromInternal_RejectsTargetExistingPlan()
    {
        var harness = CreateHarnessForAdopt(targetHasPlan: true);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<ProductionPalletPlanAdoptionException>(() => service.AdoptPlanFromInternal(67, 66));

        Assert.Equal("TARGET_ALREADY_HAS_PALLET_PLAN", ex.Code);
    }

    [Fact]
    public void AdoptPlanFromInternal_RejectsMissingTargetLine()
    {
        var harness = CreateHarnessForAdopt(targetHasMatchingLine: false);
        var service = new ProductionPalletService(harness.Store);

        var ex = Assert.Throws<ProductionPalletPlanAdoptionException>(() => service.AdoptPlanFromInternal(67, 66));

        Assert.Equal("TARGET_LINE_NOT_FOUND", ex.Code);
    }

    private static CloseDocumentHarness CreateHarnessWithFourLineTwoMixedGroups()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар A",
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedItem(new Item
        {
            Id = 200,
            Name = "Товар B",
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = 400
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "056",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine { Id = 101, OrderId = 10, ItemId = 100, QtyOrdered = 700, ProductionPalletGroup = "MIX-1" });
        harness.SeedOrderLine(new OrderLine { Id = 102, OrderId = 10, ItemId = 200, QtyOrdered = 500, ProductionPalletGroup = "MIX-1" });
        harness.SeedOrderLine(new OrderLine { Id = 103, OrderId = 10, ItemId = 100, QtyOrdered = 700, ProductionPalletGroup = "MIX-2" });
        harness.SeedOrderLine(new OrderLine { Id = 104, OrderId = 10, ItemId = 200, QtyOrdered = 500, ProductionPalletGroup = "MIX-2" });
        return harness;
    }

    private static CloseDocumentHarness CreateHarnessWithFourLineSingleMixedGroupOverCapacity()
    {
        var harness = CreateHarnessWithFourLineTwoMixedGroups();
        harness.Store.UpdateOrderLineProductionPalletGroup(103, "MIX-1");
        harness.Store.UpdateOrderLineProductionPalletGroup(104, "MIX-1");
        return harness;
    }

    private static CloseDocumentHarness CreateHarnessWithMixedOrderOnly()
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар",
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedItem(new Item
        {
            Id = 200,
            Name = "Добавка",
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = 400
        });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "056",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = 300,
            ProductionPalletGroup = "MIX-1"
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 102,
            OrderId = 10,
            ItemId = 200,
            QtyOrdered = 200,
            ProductionPalletGroup = "MIX-1"
        });
        return harness;
    }

    private static CloseDocumentHarness CreateHarnessForAdopt(
        string sourcePalletStatus = ProductionPalletStatus.Planned,
        DocStatus sourceDocStatus = DocStatus.Draft,
        bool targetHasPlan = false,
        bool targetHasMatchingLine = true)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Товар",
            Brand = "Печагин",
            BaseUom = "шт",
            MaxQtyPerHu = 600
        });
        harness.SeedOrder(new Order
        {
            Id = 66,
            OrderRef = "066",
            Type = OrderType.Internal,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 18, 16, 58, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 171,
            OrderId = 66,
            ItemId = 100,
            QtyOrdered = 0
        });
        harness.SeedOrder(new Order
        {
            Id = 67,
            OrderRef = "067",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            UseReservedStock = true,
            CreatedAt = new DateTime(2026, 5, 18, 16, 58, 34)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 172,
            OrderId = 67,
            ItemId = targetHasMatchingLine ? 100 : 200,
            QtyOrdered = 2400
        });
        harness.SeedDoc(new Doc
        {
            Id = 162,
            DocRef = "PRD-2026-000156",
            Type = DocType.ProductionReceipt,
            Status = sourceDocStatus,
            OrderId = 66,
            OrderRef = "066",
            CreatedAt = new DateTime(2026, 5, 18, 16, 58, 10)
        });
        harness.SeedLine(new DocLine
        {
            Id = 1752,
            DocId = 162,
            OrderLineId = 171,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ItemId = 100,
            Qty = 600,
            ToLocationId = 1,
            ToHu = "HU-0000462",
            PackSingleHu = true
        });
        harness.SeedLine(new DocLine
        {
            Id = 1753,
            DocId = 162,
            OrderLineId = 171,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ItemId = 100,
            Qty = 600,
            ToLocationId = 1,
            ToHu = "HU-0000463",
            PackSingleHu = true
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 35,
            PrdDocId = 162,
            DocLineId = 1752,
            OrderId = 66,
            OrderLineId = 171,
            ItemId = 100,
            ItemName = "Товар",
            HuCode = "HU-0000462",
            PlannedQty = 600,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = sourcePalletStatus,
            CreatedAt = new DateTime(2026, 5, 18, 16, 58, 10)
        });
        harness.SeedProductionPallet(new ProductionPallet
        {
            Id = 36,
            PrdDocId = 162,
            DocLineId = 1753,
            OrderId = 66,
            OrderLineId = 171,
            ItemId = 100,
            ItemName = "Товар",
            HuCode = "HU-0000463",
            PlannedQty = 600,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = sourcePalletStatus,
            CreatedAt = new DateTime(2026, 5, 18, 16, 58, 10)
        });

        if (targetHasPlan)
        {
            harness.SeedDoc(new Doc
            {
                Id = 163,
                DocRef = "PRD-2026-000157",
                Type = DocType.ProductionReceipt,
                Status = DocStatus.Draft,
                OrderId = 67,
                OrderRef = "067",
                CreatedAt = new DateTime(2026, 5, 18, 17, 13, 7)
            });
            harness.SeedLine(new DocLine
            {
                Id = 1754,
                DocId = 163,
                OrderLineId = 172,
                ProductionPurpose = ProductionLinePurpose.CustomerOrder,
                ItemId = 100,
                Qty = 600,
                ToLocationId = 1,
                ToHu = "HU-0000464",
                PackSingleHu = true
            });
            harness.SeedProductionPallet(new ProductionPallet
            {
                Id = 37,
                PrdDocId = 163,
                DocLineId = 1754,
                OrderId = 67,
                OrderLineId = 172,
                ItemId = 100,
                ItemName = "Товар",
                HuCode = "HU-0000464",
                PlannedQty = 600,
                ToLocationId = 1,
                ToLocationCode = "MAIN",
                Status = ProductionPalletStatus.Planned,
                CreatedAt = new DateTime(2026, 5, 18, 17, 13, 7)
            });
        }

        return harness;
    }

    private static CloseDocumentHarness CreateHarnessWithSixPallets(int filledCount)
    {
        var harness = new CloseDocumentHarness();
        SeedBase(harness, orderQty: 3600, plannedQty: 600, huCode: "HU-000001");
        for (var i = 1; i <= 6; i++)
        {
            harness.SeedProductionPallet(BuildPallet(
                id: i,
                huCode: $"HU-00000{i}",
                plannedQty: 600,
                status: i <= filledCount ? ProductionPalletStatus.Filled : ProductionPalletStatus.Planned));
        }

        return harness;
    }

    private static void SeedBase(CloseDocumentHarness harness, double orderQty, double plannedQty, string huCode)
    {
        harness.SeedLocation(new Location { Id = 1, Code = "MAIN", Name = "Основной склад" });
        harness.SeedItem(new Item { Id = 100, Name = "Товар", Brand = "Печагин", BaseUom = "шт" });
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "056",
            Type = OrderType.Customer,
            PartnerName = "ПЕЧАГИН ПРОДУКТ",
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 5, 13, 8, 0, 0)
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 101,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = orderQty
        });
        harness.SeedDoc(new Doc
        {
            Id = 20,
            DocRef = "PRD-2026-000001",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = 10,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        });
        harness.SeedLine(new DocLine
        {
            Id = 201,
            DocId = 20,
            OrderLineId = 101,
            ItemId = 100,
            Qty = plannedQty,
            ToLocationId = 1,
            ToHu = huCode
        });
    }

    private static ProductionPallet BuildPallet(
        long id,
        string huCode,
        double plannedQty,
        string status = ProductionPalletStatus.Planned)
    {
        return new ProductionPallet
        {
            Id = id,
            PrdDocId = 20,
            DocLineId = 201,
            OrderId = 10,
            OrderLineId = 101,
            ItemId = 100,
            ItemName = "Товар",
            HuCode = huCode,
            PlannedQty = plannedQty,
            ToLocationId = 1,
            ToLocationCode = "MAIN",
            Status = status,
            FilledAt = status == ProductionPalletStatus.Filled ? new DateTime(2026, 5, 13, 10, 0, 0) : null,
            CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0)
        };
    }
}

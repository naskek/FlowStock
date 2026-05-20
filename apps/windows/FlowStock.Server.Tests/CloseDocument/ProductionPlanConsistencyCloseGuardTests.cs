using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Server.Tests.Diagnostics;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class ProductionPlanConsistencyCloseGuardTests
{
    [Fact]
    public void CloseProductionReceipt_BlocksWhenPalletPlanExceedsOrderLines()
    {
        var harness = ProductionPlanConsistencyDiagnosticsEndpointTests.CreateHarness(
            orderId: 67,
            orderRef: "067",
            orderQty: 0);
        ProductionPlanConsistencyDiagnosticsEndpointTests.SeedProductionPalletPrd(
            harness,
            orderId: 67,
            orderLineId: 6701,
            prdDocId: 670,
            palletCount: 1,
            palletQty: 600,
            palletStatus: ProductionPalletStatus.Filled,
            seedLedger: true);

        var result = new DocumentService(harness.Store).TryCloseDoc(670, allowNegative: false);

        Assert.False(result.Success);
        Assert.Contains(
            "План паллет не соответствует строкам заказа. Запустите диагностику production-plan-consistency.",
            result.Errors);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(670).Status);
    }

    [Fact]
    public void CloseProductionReceipt_AllowsAlignedPalletPlan()
    {
        var harness = ProductionPlanConsistencyDiagnosticsEndpointTests.CreateHarness(
            orderId: 72,
            orderRef: "072",
            orderQty: 1200);
        ProductionPlanConsistencyDiagnosticsEndpointTests.SeedProductionPalletPrd(
            harness,
            orderId: 72,
            orderLineId: 7201,
            prdDocId: 720,
            palletCount: 2,
            palletQty: 600,
            palletStatus: ProductionPalletStatus.Filled,
            seedLedger: true);

        var result = new DocumentService(harness.Store).TryCloseDoc(720, allowNegative: false);

        Assert.True(result.Success);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(720).Status);
    }

    [Fact]
    public void Diagnostics_ShippedCustomerWithStaleOpenPrdDraft_IsWarning()
    {
        var harness = ProductionPlanConsistencyDiagnosticsEndpointTests.CreateHarness(
            orderId: 23,
            orderRef: "023",
            orderQty: 4800,
            orderType: OrderType.Customer,
            status: OrderStatus.Shipped);
        ProductionPlanConsistencyDiagnosticsEndpointTests.SeedStaleOpenPrdDraft(
            harness,
            orderId: 23,
            orderLineId: 2301,
            prdDocId: 230,
            prdDocQty: 2400);

        var item = Assert.Single(new ProductionPlanConsistencyDiagnosticsService(harness.Store).GetItems());
        Assert.Equal(ProductionPlanConsistencyProblemCode.ShippedCustomerWithOpenPrd, item.ProblemCode);
        Assert.Equal(ProductionPlanConsistencySeverity.Warning, item.Severity);
        Assert.Equal(2400, item.OpenPrdDocQty);
        Assert.Equal(0, item.OpenPalletPlannedQty);
        Assert.Equal(0, item.PalletFilledQty);
        Assert.Equal(0, item.LedgerOpenPrdQty);
        Assert.False(new ProductionPlanConsistencyDiagnosticsService(harness.Store).BlocksPrdClose(230));
    }

    [Fact]
    public void CloseProductionReceipt_DoesNotBlockStaleOpenPrdDraftOnShippedCustomer()
    {
        var harness = ProductionPlanConsistencyDiagnosticsEndpointTests.CreateHarness(
            orderId: 26,
            orderRef: "026",
            orderQty: 4800,
            orderType: OrderType.Customer,
            status: OrderStatus.Shipped);
        ProductionPlanConsistencyDiagnosticsEndpointTests.SeedStaleOpenPrdDraft(
            harness,
            orderId: 26,
            orderLineId: 2601,
            prdDocId: 260,
            prdDocQty: 1200);

        Assert.False(new ProductionPlanConsistencyDiagnosticsService(harness.Store).BlocksPrdClose(260));
    }

    [Fact]
    public void Diagnostics_ShippedCustomerWithOpenPalletInconsistency_IsError()
    {
        var harness = ProductionPlanConsistencyDiagnosticsEndpointTests.CreateHarness(
            orderId: 27,
            orderRef: "027",
            orderQty: 4800,
            orderType: OrderType.Customer,
            status: OrderStatus.Shipped);
        ProductionPlanConsistencyDiagnosticsEndpointTests.SeedProductionPalletPrd(
            harness,
            orderId: 27,
            orderLineId: 2701,
            prdDocId: 271,
            palletCount: 2,
            palletQty: 600,
            palletStatus: ProductionPalletStatus.Printed);
        harness.SeedLine(new DocLine
        {
            Id = 27199,
            DocId = 271,
            OrderLineId = 2701,
            ProductionPurpose = ProductionLinePurpose.InternalStock,
            ItemId = 6,
            Qty = 1200,
            ToLocationId = 10,
            PackSingleHu = false
        });

        var item = Assert.Single(new ProductionPlanConsistencyDiagnosticsService(harness.Store).GetItems());
        Assert.Equal(ProductionPlanConsistencyProblemCode.ShippedCustomerWithOpenPrd, item.ProblemCode);
        Assert.Equal(ProductionPlanConsistencySeverity.Error, item.Severity);
        Assert.False(new ProductionPlanConsistencyDiagnosticsService(harness.Store).BlocksPrdClose(271));
    }

    [Fact]
    public void CloseProductionReceipt_AllowsShippedCustomerWithInternallyConsistentOpenPrd()
    {
        var harness = ProductionPlanConsistencyDiagnosticsEndpointTests.CreateHarness(
            orderId: 72,
            orderRef: "072",
            orderQty: 4800,
            orderType: OrderType.Customer,
            status: OrderStatus.Shipped);
        ProductionPlanConsistencyDiagnosticsEndpointTests.SeedProductionPalletPrd(
            harness,
            orderId: 72,
            orderLineId: 7201,
            prdDocId: 170,
            palletCount: 4,
            palletQty: 600,
            palletStatus: ProductionPalletStatus.Filled,
            seedLedger: true);

        var diagnostics = new ProductionPlanConsistencyDiagnosticsService(harness.Store).GetItems();
        var warning = Assert.Single(diagnostics);
        Assert.Equal(ProductionPlanConsistencyProblemCode.ShippedCustomerWithOpenPrd, warning.ProblemCode);
        Assert.Equal(ProductionPlanConsistencySeverity.Warning, warning.Severity);
        Assert.False(ProductionPlanConsistencyDiagnosticsService.IsBlockingProblem(warning.ProblemCode));

        var result = new DocumentService(harness.Store).TryCloseDoc(170, allowNegative: false);

        Assert.True(result.Success);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(170).Status);
    }

    [Fact]
    public void CloseProductionReceipt_AllowsLegacyClosedPrdWhenClosedQtyMatchesLedger()
    {
        var harness = ProductionPlanConsistencyDiagnosticsEndpointTests.CreateHarness(
            orderId: 56,
            orderRef: "056",
            orderQty: 3600,
            status: OrderStatus.Shipped);
        ProductionPlanConsistencyDiagnosticsEndpointTests.SeedLegacyClosedPrdWithPartialPalletPlan(
            harness,
            orderId: 56,
            orderLineId: 5601,
            closedPrdDocId: 560,
            closedPrdQty: 3600,
            palletPlannedQty: 3000,
            seedLedger: true);

        var diagnostics = new ProductionPlanConsistencyDiagnosticsService(harness.Store).GetItems();
        Assert.DoesNotContain(
            diagnostics,
            item => item.ProblemCode == ProductionPlanConsistencyProblemCode.ClosedPrdLedgerMismatch);
        Assert.False(new ProductionPlanConsistencyDiagnosticsService(harness.Store).BlocksPrdClose(560));
    }

    [Fact]
    public void CloseProductionReceipt_BlocksWhenActivePalletsExceedOrderQty()
    {
        var harness = ProductionPlanConsistencyDiagnosticsEndpointTests.CreateHarness(
            orderId: 73,
            orderRef: "073",
            orderQty: 1200);
        ProductionPlanConsistencyDiagnosticsEndpointTests.SeedProductionPalletPrd(
            harness,
            orderId: 73,
            orderLineId: 7301,
            prdDocId: 730,
            palletCount: 4,
            palletQty: 600);

        var result = new DocumentService(harness.Store).TryCloseDoc(730, allowNegative: false);

        Assert.False(result.Success);
        Assert.Contains(
            "План паллет не соответствует строкам заказа. Запустите диагностику production-plan-consistency.",
            result.Errors);
    }

    [Fact]
    public void Diagnostics_ExcludesReplacedDocLinesFromOpenPrdQty()
    {
        var harness = ProductionPlanConsistencyDiagnosticsEndpointTests.CreateHarness(
            orderId: 80,
            orderRef: "080",
            orderQty: 1200);
        ProductionPlanConsistencyDiagnosticsEndpointTests.SeedProductionPalletPrd(
            harness,
            orderId: 80,
            orderLineId: 8001,
            prdDocId: 800,
            palletCount: 2,
            palletQty: 600);
        ProductionPlanConsistencyDiagnosticsEndpointTests.SeedReplacedPrdLine(
            harness,
            prdDocId: 800,
            orderLineId: 8001,
            supersededLineId: 80991,
            activeLineId: 80992,
            supersededQty: 4800,
            activeQty: 0);

        var diagnostics = new ProductionPlanConsistencyDiagnosticsService(harness.Store).GetItems();

        Assert.DoesNotContain(
            diagnostics,
            item => item.ProblemCode == ProductionPlanConsistencyProblemCode.PrdLinesExceedOrderQty);
        Assert.DoesNotContain(
            diagnostics,
            item => item.ProblemCode == ProductionPlanConsistencyProblemCode.PalletsExceedOrderQty);
    }
}

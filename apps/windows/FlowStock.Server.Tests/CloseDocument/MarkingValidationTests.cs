using FlowStock.Core.Models;
using FlowStock.Server.Tests.CloseDocument.Infrastructure;

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class MarkingValidationTests
{
    [Fact]
    public void ProductionReceiptWithNonMarkableItems_ClosesNormally()
    {
        var harness = CreateHarnessWithDraftProductionReceipt(orderId: null);
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Обычный товар",
            Gtin = "04601234567890",
            IsMarked = true,
            ItemTypeEnableMarking = false
        });
        harness.SeedLine(CreateReceiptLine(itemId: 100, orderLineId: null));

        var result = harness.CreateService().TryCloseDoc(1, allowNegative: false);

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Single(harness.LedgerEntries);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(1).Status);
    }

    [Fact]
    public void ProductionReceiptWithMarkableItems_ClosesWhenOrderMarkingPrinted()
    {
        var harness = CreateHarnessWithOrder(MarkingStatus.Printed);

        var result = harness.CreateService().TryCloseDoc(1, allowNegative: false);

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Single(harness.LedgerEntries);
        Assert.Equal(DocStatus.Closed, harness.GetDoc(1).Status);
    }

    [Theory]
    [InlineData(MarkingStatus.Required)]
    [InlineData(MarkingStatus.ExcelGenerated)]
    [InlineData(MarkingStatus.NotRequired)]
    public void ProductionReceiptWithMarkableItems_RejectsUntilOrderMarkingPrinted(MarkingStatus markingStatus)
    {
        var harness = CreateHarnessWithOrder(markingStatus);
        var docCountBefore = harness.DocCount;
        var docLineCountBefore = harness.TotalDocLineCount;

        var result = harness.CreateService().TryCloseDoc(1, allowNegative: false);

        Assert.False(result.Success);
        Assert.Contains(
            "Нельзя закрыть выпуск маркируемой продукции: по заказу не проведена маркировка ЧЗ.",
            result.Errors);
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(1).Status);
        Assert.Equal(docCountBefore, harness.DocCount);
        Assert.Equal(docLineCountBefore, harness.TotalDocLineCount);
    }

    [Fact]
    public void ProductionReceiptWithMarkableItems_RejectsMissingOrderId()
    {
        var harness = CreateHarnessWithDraftProductionReceipt(orderId: null);
        harness.SeedItem(CreateMarkableItem());
        harness.SeedLine(CreateReceiptLine(itemId: 100, orderLineId: null));
        var docCountBefore = harness.DocCount;
        var docLineCountBefore = harness.TotalDocLineCount;

        var result = harness.CreateService().TryCloseDoc(1, allowNegative: false);

        Assert.False(result.Success);
        Assert.Contains(
            "Нельзя закрыть выпуск маркируемой продукции без связанного заказа ЧЗ.",
            result.Errors);
        Assert.Empty(harness.LedgerEntries);
        Assert.Equal(DocStatus.Draft, harness.GetDoc(1).Status);
        Assert.Equal(docCountBefore, harness.DocCount);
        Assert.Equal(docLineCountBefore, harness.TotalDocLineCount);
    }

    [Fact]
    public void ProductionReceiptMarkingValidation_UsesEnableMarkingAndGtin_NotLegacyIsMarked()
    {
        var harness = CreateHarnessWithDraftProductionReceipt(orderId: null);
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Legacy mark only",
            Gtin = "04601234567890",
            IsMarked = true,
            ItemTypeEnableMarking = false
        });
        harness.SeedLine(CreateReceiptLine(itemId: 100, orderLineId: null));

        var result = harness.CreateService().TryCloseDoc(1, allowNegative: false);

        Assert.True(result.Success);
        Assert.Single(harness.LedgerEntries);
    }

    [Fact]
    public void ProductionReceiptMarkingValidation_EmptyGtinIsNotMarkable()
    {
        var harness = CreateHarnessWithDraftProductionReceipt(orderId: null);
        harness.SeedItem(new Item
        {
            Id = 100,
            Name = "Тип ЧЗ без GTIN",
            Gtin = "",
            IsMarked = true,
            ItemTypeEnableMarking = true
        });
        harness.SeedLine(CreateReceiptLine(itemId: 100, orderLineId: null));

        var result = harness.CreateService().TryCloseDoc(1, allowNegative: false);

        Assert.True(result.Success);
        Assert.Single(harness.LedgerEntries);
    }

    private static CloseDocumentHarness CreateHarnessWithOrder(MarkingStatus markingStatus)
    {
        var harness = CreateHarnessWithDraftProductionReceipt(orderId: 10);
        harness.SeedItem(CreateMarkableItem());
        harness.SeedOrder(new Order
        {
            Id = 10,
            OrderRef = "CO-2026-000010",
            Type = OrderType.Customer,
            Status = OrderStatus.InProgress,
            CreatedAt = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc),
            MarkingStatus = markingStatus
        });
        harness.SeedOrderLine(new OrderLine
        {
            Id = 1000,
            OrderId = 10,
            ItemId = 100,
            QtyOrdered = 10
        });
        harness.SeedOrderReceiptRemaining(10, new OrderReceiptLine
        {
            OrderLineId = 1000,
            OrderId = 10,
            ItemId = 100,
            ItemName = "Маркируемый товар",
            QtyOrdered = 10,
            QtyReceived = 0,
            QtyRemaining = 10
        });
        harness.SeedLine(CreateReceiptLine(itemId: 100, orderLineId: 1000));
        return harness;
    }

    private static CloseDocumentHarness CreateHarnessWithDraftProductionReceipt(long? orderId)
    {
        var harness = new CloseDocumentHarness();
        harness.SeedDoc(new Doc
        {
            Id = 1,
            DocRef = "PRD-2026-000010",
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            OrderId = orderId,
            OrderRef = orderId.HasValue ? "CO-2026-000010" : null,
            CreatedAt = new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc)
        });
        harness.SeedLocation(new Location { Id = 10, Code = "01", Name = "Склад 01" });
        return harness;
    }

    private static Item CreateMarkableItem()
    {
        return new Item
        {
            Id = 100,
            Name = "Маркируемый товар",
            Gtin = "04601234567890",
            IsMarked = false,
            ItemTypeEnableMarking = true
        };
    }

    private static DocLine CreateReceiptLine(long itemId, long? orderLineId)
    {
        return new DocLine
        {
            Id = 100,
            DocId = 1,
            ItemId = itemId,
            OrderLineId = orderLineId,
            Qty = 5,
            ToLocationId = 10,
            ToHu = "HU-CZ-001"
        };
    }
}

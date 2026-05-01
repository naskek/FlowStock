using System.IO.Compression;
using System.Xml.Linq;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Marking;

public sealed class SimpleMarkingExcelServiceTests
{
    [Theory]
    [InlineData(false, MarkingStatus.NotRequired, "Не требуется")]
    [InlineData(true, MarkingStatus.NotRequired, "Требуется")]
    [InlineData(true, MarkingStatus.Required, "Требуется")]
    [InlineData(true, MarkingStatus.Printed, "Готов к нанесению")]
    [InlineData(false, MarkingStatus.Printed, "Готов к нанесению")]
    public void OrderList_UsesEffectiveShortMarkingStatusLabels(bool markingRequired, MarkingStatus status, string expected)
    {
        var order = new Order
        {
            MarkingRequired = markingRequired,
            MarkingStatus = status
        };

        Assert.Equal(expected, order.MarkingStatusShortDisplay);
    }

    [Theory]
    [InlineData(true, "04601234567890", true, MarkingStatus.NotRequired, "Требуется файл ЧЗ")]
    [InlineData(true, "04601234567890", true, MarkingStatus.Printed, "ЧЗ готов к нанесению")]
    [InlineData(true, "", false, MarkingStatus.NotRequired, "Маркировка не требуется")]
    [InlineData(false, "04601234567890", false, MarkingStatus.NotRequired, "Маркировка не требуется")]
    public void OrderLabel_UsesMarkableOrderLinesRequirement(
        bool itemTypeEnableMarking,
        string? gtin,
        bool markingRequired,
        MarkingStatus status,
        string expected)
    {
        var item = new Item
        {
            ItemTypeEnableMarking = itemTypeEnableMarking,
            Gtin = gtin
        };
        var order = new Order
        {
            MarkingRequired = item.IsChestnyZnakMarkingRequired && markingRequired,
            MarkingStatus = status
        };

        Assert.Equal(expected, order.MarkingStatusDisplay);
    }

    [Fact]
    public void LegacyExcelGeneratedRawStatus_ParsesAsPrinted()
    {
        Assert.Equal(MarkingStatus.Printed, MarkingStatusMapper.FromString("EXCEL_GENERATED"));
        Assert.Equal("PRINTED", MarkingStatusMapper.ToString(MarkingStatusMapper.FromString("EXCEL_GENERATED")));
        Assert.Equal("ЧЗ готов к нанесению", MarkingStatusMapper.ToDisplayName(MarkingStatusMapper.FromString("EXCEL_GENERATED")));
    }

    [Fact]
    public void Export_UsesEnableMarkingAndGtinAsEligibility()
    {
        var store = CreateStore(
            new MarkingOrderLineCandidate
            {
                OrderId = 1,
                OrderLineId = 10,
                ItemName = "Маркируемый",
                Gtin = "04601234567890",
                ItemTypeEnableMarking = true,
                QtyOrdered = 5
            },
            new MarkingOrderLineCandidate
            {
                OrderId = 1,
                OrderLineId = 11,
                ItemName = "Тип не маркируется",
                Gtin = "04600000000000",
                ItemTypeEnableMarking = false,
                QtyOrdered = 7
            },
            new MarkingOrderLineCandidate
            {
                OrderId = 1,
                OrderLineId = 12,
                ItemName = "Без GTIN",
                Gtin = "",
                ItemTypeEnableMarking = true,
                QtyOrdered = 9
            });

        var result = new MarkingExcelService(store.Object).Export(new[] { 1L }, DateTime.Parse("2026-04-30T10:00:00"));

        Assert.True(result.IsSuccess);
        var row = Assert.Single(result.Rows);
        Assert.Equal("Маркируемый", row.ItemName);
        Assert.Equal("04601234567890", row.Gtin);
        Assert.Equal(5, row.Qty);
        store.Verify(s => s.MarkOrdersPrinted(It.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new[] { 1L })), It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public void Export_CalculatesQtyAsOrderedMinusShippedMinusReserved()
    {
        var store = CreateStore(new MarkingOrderLineCandidate
        {
            OrderId = 1,
            OrderLineId = 10,
            ItemName = "Крем",
            Gtin = "04601234567890",
            ItemTypeEnableMarking = true,
            QtyOrdered = 20,
            ShippedQty = 3,
            ReservedQty = 4,
            QtyForMarking = 999
        });

        var result = new MarkingExcelService(store.Object).Export(new[] { 1L }, DateTime.Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(13, Assert.Single(result.Rows).Qty);
    }

    [Fact]
    public void Export_ReservedReadyStockDoesNotRequireNewCodes()
    {
        var store = CreateStore(new MarkingOrderLineCandidate
        {
            OrderId = 1,
            OrderLineId = 10,
            ItemName = "Крем",
            Gtin = "04601234567890",
            ItemTypeEnableMarking = true,
            QtyOrdered = 10,
            ReservedQty = 10
        });

        var result = new MarkingExcelService(store.Object).Export(new[] { 1L }, DateTime.Now);

        Assert.False(result.IsSuccess);
        Assert.Equal("Нет строк для формирования файла ЧЗ.", result.Error);
        store.Verify(s => s.MarkOrdersPrinted(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public void Export_AggregatesSeveralOrdersIntoOneExcelByGtinAndName()
    {
        var store = CreateStore(
            new MarkingOrderLineCandidate
            {
                OrderId = 1,
                OrderLineId = 10,
                ItemName = "Крем",
                Gtin = "04601234567890",
                ItemTypeEnableMarking = true,
                QtyOrdered = 4
            },
            new MarkingOrderLineCandidate
            {
                OrderId = 2,
                OrderLineId = 20,
                ItemName = "крем",
                Gtin = "04601234567890",
                ItemTypeEnableMarking = true,
                QtyOrdered = 6
            });

        var result = new MarkingExcelService(store.Object).Export(new[] { 1L, 2L }, DateTime.Now);

        Assert.True(result.IsSuccess);
        var row = Assert.Single(result.Rows);
        Assert.Equal(10, row.Qty);
        Assert.Contains(1L, result.MarkedOrderIds);
        Assert.Contains(2L, result.MarkedOrderIds);
        AssertExcelHasOnlyThreeColumns(result.FileBytes!);
    }

    [Fact]
    public void Export_ExcelHasNoHeaderRowAndStartsWithData()
    {
        var store = CreateStore(new MarkingOrderLineCandidate
        {
            OrderId = 1,
            OrderLineId = 10,
            ItemName = "Крем",
            Gtin = "04601234567890",
            ItemTypeEnableMarking = true,
            QtyOrdered = 4
        });

        var result = new MarkingExcelService(store.Object).Export(new[] { 1L }, DateTime.Now);

        Assert.True(result.IsSuccess);
        var cells = ReadWorksheetCells(result.FileBytes!);
        Assert.Equal("Крем", cells["A1"]);
        Assert.Equal("04601234567890", cells["B1"]);
        Assert.Equal("4", cells["C1"]);
        Assert.DoesNotContain("Наименование", cells.Values);
        Assert.DoesNotContain("GTIN", cells.Values);
        Assert.DoesNotContain("Кол-во", cells.Values);
        Assert.False(cells.ContainsKey("D1"));
    }

    [Fact]
    public void Export_OrderWithoutRowsDoesNotBlockOtherOrdersAndIsNotMarkedPrinted()
    {
        var store = CreateStore(new MarkingOrderLineCandidate
        {
            OrderId = 1,
            OrderLineId = 10,
            ItemName = "Крем",
            Gtin = "04601234567890",
            ItemTypeEnableMarking = true,
            QtyOrdered = 4
        });

        var result = new MarkingExcelService(store.Object).Export(new[] { 1L, 2L }, DateTime.Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { 1L }, result.MarkedOrderIds);
        store.Verify(s => s.MarkOrdersPrinted(It.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new[] { 1L })), It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public void Export_NoRowsReturnsMessageWithoutMutations()
    {
        var store = CreateStore();

        var result = new MarkingExcelService(store.Object).Export(new[] { 1L }, DateTime.Now);

        Assert.False(result.IsSuccess);
        Assert.Equal("Нет строк для формирования файла ЧЗ.", result.Error);
        Assert.Null(result.FileBytes);
        store.Verify(s => s.MarkOrdersPrinted(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<DateTime>()), Times.Never);
        store.Verify(s => s.AddLedgerEntry(It.IsAny<LedgerEntry>()), Times.Never);
        store.Verify(s => s.UpdateDocStatus(It.IsAny<long>(), It.IsAny<DocStatus>(), It.IsAny<DateTime?>()), Times.Never);
    }

    [Fact]
    public void Queue_DelegatesIncludeCompletedFlagToStore()
    {
        var store = CreateStore();

        new MarkingExcelService(store.Object).GetOrderQueue(includeCompleted: true);

        store.Verify(s => s.GetMarkingOrderQueue(true), Times.Once);
    }

    [Fact]
    public void Queue_ShowsRequiredWhenOrderHasMarkingRows()
    {
        var store = CreateStore();
        store.Setup(s => s.GetMarkingOrderQueue(false))
            .Returns(new[]
            {
                new MarkingOrderQueueRow
                {
                    OrderId = 1,
                    OrderRef = "38",
                    OrderStatus = OrderStatus.InProgress,
                    MarkingStatus = MarkingStatus.NotRequired,
                    MarkingLineCount = 1,
                    MarkingCodeCount = 2
                }
            });

        var row = Assert.Single(new MarkingExcelService(store.Object).GetOrderQueue(includeCompleted: false));

        Assert.Equal(MarkingStatus.Required, row.MarkingStatus);
    }

    [Fact]
    public void Queue_UsesSameDisplayLabelAsOrderApiForRequiredStatus()
    {
        var store = CreateStore();
        store.Setup(s => s.GetMarkingOrderQueue(false))
            .Returns(new[]
            {
                new MarkingOrderQueueRow
                {
                    OrderId = 1,
                    OrderRef = "38",
                    OrderStatus = OrderStatus.InProgress,
                    MarkingStatus = MarkingStatus.NotRequired,
                    MarkingLineCount = 1,
                    MarkingCodeCount = 2
                }
            });

        var queueRow = Assert.Single(new MarkingExcelService(store.Object).GetOrderQueue(includeCompleted: false));
        var order = new Order
        {
            Id = queueRow.OrderId,
            OrderRef = queueRow.OrderRef,
            Status = queueRow.OrderStatus,
            MarkingStatus = MarkingStatus.NotRequired,
            MarkingRequired = true,
            CreatedAt = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc)
        };

        Assert.Equal(order.MarkingStatusDisplay, MarkingStatusMapper.ToDisplayName(queueRow.MarkingStatus));
    }

    [Fact]
    public void Queue_KeepsPrintedPriorityWhenCurrentNeedIsZero()
    {
        var store = CreateStore();
        store.Setup(s => s.GetMarkingOrderQueue(true))
            .Returns(new[]
            {
                new MarkingOrderQueueRow
                {
                    OrderId = 1,
                    OrderRef = "38",
                    OrderStatus = OrderStatus.Shipped,
                    MarkingStatus = MarkingStatus.Printed,
                    MarkingLineCount = 0
                }
            });

        var row = Assert.Single(new MarkingExcelService(store.Object).GetOrderQueue(includeCompleted: true));

        Assert.Equal(MarkingStatus.Printed, row.MarkingStatus);
        Assert.Equal("ЧЗ готов к нанесению", MarkingStatusMapper.ToDisplayName(row.MarkingStatus));
    }

    [Fact]
    public void Export_DoesNotUseLegacyKmMethods()
    {
        var store = CreateStore(new MarkingOrderLineCandidate
        {
            OrderId = 1,
            OrderLineId = 10,
            ItemName = "Крем",
            Gtin = "04601234567890",
            ItemTypeEnableMarking = true,
            QtyOrdered = 1
        });

        new MarkingExcelService(store.Object).Export(new[] { 1L }, DateTime.Now);

        store.Verify(s => s.GetKmCodeBatches(), Times.Never);
        store.Verify(s => s.GetAvailableKmCodeIds(It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
        store.Verify(s => s.FindMarkingOrderByRequestNumber(It.IsAny<string>()), Times.Never);
    }

    private static Mock<IDataStore> CreateStore(params MarkingOrderLineCandidate[] lines)
    {
        var store = new Mock<IDataStore>(MockBehavior.Loose);
        store.Setup(s => s.GetMarkingOrderLineCandidates(It.IsAny<IReadOnlyCollection<long>>()))
            .Returns((IReadOnlyCollection<long> ids) => lines.Where(line => ids.Contains(line.OrderId)).ToList());
        store.Setup(s => s.GetMarkingOrderQueue(It.IsAny<bool>()))
            .Returns(Array.Empty<MarkingOrderQueueRow>());
        return store;
    }

    private static void AssertExcelHasOnlyThreeColumns(byte[] bytes)
    {
        var cells = ReadWorksheetCells(bytes);
        Assert.Contains("A1", cells.Keys);
        Assert.Contains("B1", cells.Keys);
        Assert.Contains("C1", cells.Keys);
        Assert.All(cells.Keys, cellRef => Assert.DoesNotMatch(@"^D\d+$", cellRef));
    }

    private static Dictionary<string, string> ReadWorksheetCells(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var sheet = archive.GetEntry("xl/worksheets/sheet1.xml");
        Assert.NotNull(sheet);
        using var reader = new StreamReader(sheet.Open());
        var document = XDocument.Load(reader);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return document
            .Descendants(ns + "c")
            .ToDictionary(
                cell => cell.Attribute("r")?.Value ?? string.Empty,
                cell =>
                {
                    if (string.Equals(cell.Attribute("t")?.Value, "inlineStr", StringComparison.OrdinalIgnoreCase))
                    {
                        return cell.Element(ns + "is")?.Element(ns + "t")?.Value ?? string.Empty;
                    }

                    return cell.Element(ns + "v")?.Value ?? string.Empty;
                });
    }
}

using System.IO.Compression;
using System.Xml.Linq;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Marking;

public sealed class SimpleMarkingExcelServiceTests
{
    [Theory]
    [InlineData(false, MarkingStatus.NotRequired, "")]
    [InlineData(true, MarkingStatus.NotRequired, "Маркировка не проведена")]
    [InlineData(true, MarkingStatus.Required, "Маркировка не проведена")]
    [InlineData(true, MarkingStatus.Printed, "Маркировка проведена")]
    [InlineData(false, MarkingStatus.Printed, "Маркировка проведена")]
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
    [InlineData(true, "04601234567890", true, MarkingStatus.NotRequired, "Маркировка не проведена")]
    [InlineData(true, "04601234567890", true, MarkingStatus.Printed, "Маркировка проведена")]
    [InlineData(true, "", false, MarkingStatus.NotRequired, "")]
    [InlineData(false, "04601234567890", false, MarkingStatus.NotRequired, "")]
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
        Assert.Equal("Маркировка проведена", MarkingStatusMapper.ToDisplayName(MarkingStatusMapper.FromString("EXCEL_GENERATED")));
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
    public void Export_ByProductionNeedMarkingTask_CreatesExcelRows()
    {
        var taskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var store = CreateTaskStore(new MarkingOrder
        {
            Id = taskId,
            OrderId = null,
            ItemId = 1001,
            Gtin = "04607186951520",
            RequestedQuantity = 4800,
            RequestNumber = "PN-1001",
            SourceType = MarkingNeedCreationService.ProductionNeedSourceType,
            Status = MarkingOrderStatus.WaitingForCodes,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        });

        var result = new MarkingExcelService(store.Object).Export(new[] { taskId }, Array.Empty<long>(), DateTime.Now);

        Assert.True(result.IsSuccess);
        var row = Assert.Single(result.Rows);
        Assert.Equal("Горчица", row.ItemName);
        Assert.Equal("04607186951520", row.Gtin);
        Assert.Equal(4800, row.Qty);
        Assert.Contains(taskId, result.MarkedMarkingOrderIds);
        store.Verify(s => s.MarkMarkingOrdersPrinted(It.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { taskId })), It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public void Export_ByProductionOrderMarkingTask_CreatesExcelRows()
    {
        var taskId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var store = CreateTaskStore(new MarkingOrder
        {
            Id = taskId,
            OrderId = null,
            ItemId = 1001,
            Gtin = "04607186951520",
            RequestedQuantity = 3600,
            RequestNumber = "PO-1001",
            SourceType = MarkingNeedCreationService.ProductionOrderSourceType,
            Status = MarkingOrderStatus.WaitingForCodes,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        });

        var result = new MarkingExcelService(store.Object).Export(new[] { taskId }, Array.Empty<long>(), DateTime.Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(3600, Assert.Single(result.Rows).Qty);
    }

    [Fact]
    public void Export_ByProductionNeedMarkingTask_CreatesTemporaryCodesWhenMissing()
    {
        var taskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        IReadOnlyList<MarkingCode>? createdCodes = null;
        MarkingCodeImport? createdImport = null;
        var store = CreateTaskStore(new MarkingOrder
        {
            Id = taskId,
            OrderId = null,
            ItemId = 1001,
            Gtin = "04607186951520",
            RequestedQuantity = 600,
            RequestNumber = "PN-1001",
            SourceType = MarkingNeedCreationService.ProductionNeedSourceType,
            Status = MarkingOrderStatus.Printed,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        });
        store.Setup(s => s.CountMarkingCodesByMarkingOrder(taskId)).Returns(0);
        store.Setup(s => s.AddMarkingCodeImport(It.IsAny<MarkingCodeImport>()))
            .Callback<MarkingCodeImport>(import => createdImport = import)
            .Returns<MarkingCodeImport>(import => import.Id);
        store.Setup(s => s.AddMarkingCodes(It.IsAny<IReadOnlyList<MarkingCode>>()))
            .Callback<IReadOnlyList<MarkingCode>>(codes => createdCodes = codes);

        var result = new MarkingExcelService(store.Object).Export(new[] { taskId }, Array.Empty<long>(), DateTime.Parse("2026-05-01T10:00:00"));

        Assert.True(result.IsSuccess);
        Assert.NotNull(createdImport);
        Assert.Equal(MarkingCodeImportStatus.Bound, createdImport!.Status);
        Assert.Equal(taskId, createdImport.MatchedMarkingOrderId);
        Assert.NotNull(createdCodes);
        Assert.Equal(600, createdCodes!.Count);
        Assert.All(createdCodes, code =>
        {
            Assert.Equal(taskId, code.MarkingOrderId);
            Assert.Equal(createdImport.Id, code.ImportId);
            Assert.Equal("04607186951520", code.Gtin);
            Assert.Equal(MarkingCodeStatus.Reserved, code.Status);
            Assert.StartsWith($"TEMP-CHZ-{taskId:D}-", code.Code, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(code.CodeHash));
        });
    }

    [Fact]
    public void Export_ByProductionNeedMarkingTask_DoesNotDuplicateTemporaryCodes()
    {
        var taskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var store = CreateTaskStore(new MarkingOrder
        {
            Id = taskId,
            OrderId = null,
            ItemId = 1001,
            Gtin = "04607186951520",
            RequestedQuantity = 600,
            RequestNumber = "PN-1001",
            SourceType = MarkingNeedCreationService.ProductionNeedSourceType,
            Status = MarkingOrderStatus.Printed,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        });
        store.Setup(s => s.CountMarkingCodesByMarkingOrder(taskId)).Returns(600);

        var result = new MarkingExcelService(store.Object).Export(new[] { taskId }, Array.Empty<long>(), DateTime.Now);

        Assert.True(result.IsSuccess);
        store.Verify(s => s.AddMarkingCodeImport(It.IsAny<MarkingCodeImport>()), Times.Never);
        store.Verify(s => s.AddMarkingCodes(It.IsAny<IReadOnlyList<MarkingCode>>()), Times.Never);
    }

    [Fact]
    public void Export_ByProductionNeedMarkingTask_CreatesOnlyMissingTemporaryCodes()
    {
        var taskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        IReadOnlyList<MarkingCode>? createdCodes = null;
        var store = CreateTaskStore(new MarkingOrder
        {
            Id = taskId,
            OrderId = null,
            ItemId = 1001,
            Gtin = "04607186951520",
            RequestedQuantity = 600,
            RequestNumber = "PN-1001",
            SourceType = MarkingNeedCreationService.ProductionNeedSourceType,
            Status = MarkingOrderStatus.Printed,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        });
        store.Setup(s => s.CountMarkingCodesByMarkingOrder(taskId)).Returns(300);
        store.Setup(s => s.AddMarkingCodeImport(It.IsAny<MarkingCodeImport>()))
            .Returns<MarkingCodeImport>(import => import.Id);
        store.Setup(s => s.AddMarkingCodes(It.IsAny<IReadOnlyList<MarkingCode>>()))
            .Callback<IReadOnlyList<MarkingCode>>(codes => createdCodes = codes);

        var result = new MarkingExcelService(store.Object).Export(new[] { taskId }, Array.Empty<long>(), DateTime.Now);

        Assert.True(result.IsSuccess);
        Assert.NotNull(createdCodes);
        Assert.Equal(300, createdCodes!.Count);
        Assert.Equal(301, createdCodes.First().SourceRowNumber);
        Assert.Equal(600, createdCodes.Last().SourceRowNumber);
    }

    [Fact]
    public void Export_ByOrderBasedMarkingTask_MarksTaskAndOrderPrinted()
    {
        var taskId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var store = CreateTaskStore(new MarkingOrder
        {
            Id = taskId,
            OrderId = 42,
            ItemId = 1001,
            Gtin = "04607186951520",
            RequestedQuantity = 7,
            RequestNumber = "SO-42",
            Status = MarkingOrderStatus.WaitingForCodes,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        });

        var result = new MarkingExcelService(store.Object).Export(new[] { taskId }, Array.Empty<long>(), DateTime.Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, Assert.Single(result.Rows).Qty);
        Assert.Contains(42, result.MarkedOrderIds);
        store.Verify(s => s.MarkOrdersPrinted(It.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new[] { 42L })), It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public void Export_WithoutSelection_AsksToSelectMarkingTask()
    {
        var store = CreateStore();

        var result = new MarkingExcelService(store.Object).Export(Array.Empty<Guid>(), Array.Empty<long>(), DateTime.Now);

        Assert.False(result.IsSuccess);
        Assert.Equal("Выберите хотя бы одну задачу маркировки.", result.Error);
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
            Id = queueRow.OrderId!.Value,
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
        Assert.Equal("Маркировка проведена", MarkingStatusMapper.ToDisplayName(row.MarkingStatus));
    }

    [Fact]
    public void Queue_DoesNotHidePrintedProductionNeedTaskWithoutOrderId()
    {
        var taskId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var store = CreateStore();
        store.Setup(s => s.GetMarkingOrderQueue(false))
            .Returns(new[]
            {
                new MarkingOrderQueueRow
                {
                    MarkingOrderId = taskId,
                    OrderId = null,
                    OrderRef = "Потребность производства",
                    PartnerName = "Потребность производства",
                    SourceType = MarkingNeedCreationService.ProductionNeedSourceType,
                    ItemId = 100,
                    ItemName = "Горчица",
                    Gtin = "04601234567890",
                    RequestedQuantity = 600,
                    TaskStatus = MarkingOrderStatus.Printed,
                    CodesTotal = 0,
                    CodesFree = 0,
                    CodesBound = 0,
                    OrderStatus = OrderStatus.InProgress,
                    MarkingStatus = MarkingStatus.Printed,
                    MarkingLineCount = 1,
                    MarkingCodeCount = 600
                }
            });

        var row = Assert.Single(new MarkingExcelService(store.Object).GetOrderQueue(includeCompleted: false));

        Assert.Equal(taskId, row.MarkingOrderId);
        Assert.Null(row.OrderId);
        Assert.Equal(MarkingNeedCreationService.ProductionNeedSourceType, row.SourceType);
        Assert.Equal(100, row.ItemId);
        Assert.Equal("Горчица", row.ItemName);
        Assert.Equal("04601234567890", row.Gtin);
        Assert.Equal(600, row.RequestedQuantity);
        Assert.Equal(MarkingOrderStatus.Printed, row.TaskStatus);
        Assert.Equal(0, row.CodesTotal);
        Assert.Equal(0, row.CodesFree);
        Assert.Equal(0, row.CodesBound);
        Assert.Equal(MarkingStatus.Printed, row.MarkingStatus);
    }

    [Fact]
    public void Queue_DoesNotHideProductionOrderTaskWithoutOrderId()
    {
        var taskId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var store = CreateStore();
        store.Setup(s => s.GetMarkingOrderQueue(false))
            .Returns(new[]
            {
                new MarkingOrderQueueRow
                {
                    MarkingOrderId = taskId,
                    OrderId = null,
                    SourceOrderId = 77,
                    OrderRef = "Производственный заказ",
                    SourceType = MarkingNeedCreationService.ProductionOrderSourceType,
                    DisplaySource = "Производственный заказ",
                    OrderStatus = OrderStatus.InProgress,
                    MarkingStatus = MarkingStatus.Required,
                    MarkingLineCount = 1,
                    MarkingCodeCount = 100
                }
            });

        var row = Assert.Single(new MarkingExcelService(store.Object).GetOrderQueue(includeCompleted: false));

        Assert.Equal(taskId, row.MarkingOrderId);
        Assert.Null(row.OrderId);
        Assert.Equal(77, row.SourceOrderId);
        Assert.Equal(MarkingNeedCreationService.ProductionOrderSourceType, row.SourceType);
        Assert.Equal("Производственный заказ", row.DisplaySource);
    }

    [Fact]
    public void Queue_KeepsOrderBasedMarkingTaskVisible()
    {
        var taskId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var store = CreateStore();
        store.Setup(s => s.GetMarkingOrderQueue(false))
            .Returns(new[]
            {
                new MarkingOrderQueueRow
                {
                    MarkingOrderId = taskId,
                    OrderId = 10,
                    OrderRef = "CO-10",
                    SourceType = null,
                    OrderStatus = OrderStatus.InProgress,
                    MarkingStatus = MarkingStatus.Required,
                    MarkingLineCount = 1,
                    MarkingCodeCount = 50
                }
            });

        var row = Assert.Single(new MarkingExcelService(store.Object).GetOrderQueue(includeCompleted: false));

        Assert.Equal(taskId, row.MarkingOrderId);
        Assert.Equal(10, row.OrderId);
        Assert.Equal("CO-10", row.OrderRef);
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
        store.Setup(s => s.ExecuteInTransaction(It.IsAny<Action<IDataStore>>()))
            .Callback<Action<IDataStore>>(work => work(store.Object));
        store.Setup(s => s.GetMarkingOrderLineCandidates(It.IsAny<IReadOnlyCollection<long>>()))
            .Returns((IReadOnlyCollection<long> ids) => lines.Where(line => ids.Contains(line.OrderId)).ToList());
        store.Setup(s => s.GetMarkingOrderQueue(It.IsAny<bool>()))
            .Returns(Array.Empty<MarkingOrderQueueRow>());
        return store;
    }

    private static Mock<IDataStore> CreateTaskStore(params MarkingOrder[] tasks)
    {
        var store = CreateStore();
        store.Setup(s => s.GetMarkingOrdersByIds(It.IsAny<IReadOnlyCollection<Guid>>()))
            .Returns((IReadOnlyCollection<Guid> ids) => tasks.Where(task => ids.Contains(task.Id)).ToList());
        store.Setup(s => s.FindItemById(1001))
            .Returns(new Item
            {
                Id = 1001,
                Name = "Горчица",
                Gtin = "04607186951520",
                ItemTypeEnableMarking = true
            });
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

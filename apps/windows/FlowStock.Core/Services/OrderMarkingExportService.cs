using System.Globalization;
using System.Text;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Services;

public sealed class OrderMarkingExportService
{
    private const double QtyTolerance = 0.000001;
    private readonly IDataStore _data;
    private readonly MarkingExcelService _markingExcel;

    public OrderMarkingExportService(IDataStore data, MarkingExcelService markingExcel)
    {
        _data = data;
        _markingExcel = markingExcel;
    }

    public OrderMarkingExportResult Export(long orderId, DateTime generatedAt)
    {
        var order = _data.GetOrder(orderId);
        if (order == null)
        {
            return OrderMarkingExportResult.Failure("Заказ не найден.");
        }

        if (order.Status == OrderStatus.Shipped)
        {
            return OrderMarkingExportResult.Failure("Нельзя формировать Excel ЧЗ для выполненного заказа.");
        }

        var lines = BuildLineSummaries(order).ToList();
        if (lines.Count == 0)
        {
            return new OrderMarkingExportResult(
                true,
                "В заказе нет маркируемых строк.",
                null,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                0,
                Array.Empty<OrderMarkingExportLineSummary>());
        }

        var itemIds = lines.Select(line => line.ItemId).Distinct().ToArray();
        var activeTasks = _data.GetMarkingOrdersByItemIds(itemIds)
            .Where(task => task.ItemId.HasValue
                           && !IsTerminalFailed(task.Status)
                           && IsOrderLinkedTask(task, order.Id))
            .ToList();
        var tasksByItem = activeTasks
            .GroupBy(task => task.ItemId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        var taskIdsToExport = new List<Guid>();
        var taskIdsAvailableForReexport = new List<Guid>();
        var createdCodeQty = 0d;
        var reusedCodeQty = 0d;
        var exportLineCount = 0;
        var sequence = 1;

        foreach (var group in lines.GroupBy(line => line.ItemId))
        {
            var itemRequiredQty = group.Sum(line => Math.Max(0, line.ExportQty + line.ExistingCodeQty));
            if (itemRequiredQty <= QtyTolerance)
            {
                continue;
            }

            var itemTasks = tasksByItem.TryGetValue(group.Key, out var existing)
                ? existing
                : new List<MarkingOrder>();
            var taskRequestedQty = itemTasks.Sum(task => Math.Max(0, task.RequestedQuantity));
            var taskCodeQtyById = itemTasks.ToDictionary(task => task.Id, task => _data.CountMarkingCodesByMarkingOrder(task.Id));
            var taskCodeQty = taskCodeQtyById.Sum(pair => pair.Value);
            reusedCodeQty += Math.Min(itemRequiredQty, taskCodeQty);
            taskIdsAvailableForReexport.AddRange(itemTasks
                .Where(task => taskCodeQtyById.TryGetValue(task.Id, out var codes) && codes > 0)
                .Select(task => task.Id));

            foreach (var task in itemTasks.Where(task => taskCodeQtyById.TryGetValue(task.Id, out var codes)
                                                         && codes + QtyTolerance < task.RequestedQuantity))
            {
                taskIdsToExport.Add(task.Id);
                createdCodeQty += Math.Max(0, task.RequestedQuantity - taskCodeQtyById[task.Id]);
            }

            var missingTaskQty = Math.Max(0, itemRequiredQty - taskRequestedQty);
            if (missingTaskQty <= QtyTolerance)
            {
                continue;
            }

            var line = group.First();
            var requestedQty = (int)Math.Ceiling(missingTaskQty);
            var newTask = CreateMarkingOrder(order, line, requestedQty, generatedAt, sequence++);
            _data.AddMarkingOrder(newTask);
            taskIdsToExport.Add(newTask.Id);
            createdCodeQty += requestedQty;
        }

        taskIdsToExport = taskIdsToExport.Distinct().ToList();
        if (taskIdsToExport.Count == 0)
        {
            taskIdsToExport = taskIdsAvailableForReexport
                .Distinct()
                .ToList();
        }

        MarkingExcelExportResult? excelResult = null;
        if (taskIdsToExport.Count > 0)
        {
            excelResult = _markingExcel.Export(taskIdsToExport, Array.Empty<long>(), generatedAt);
            if (!excelResult.IsSuccess || excelResult.FileBytes == null)
            {
                return OrderMarkingExportResult.Failure(excelResult.Error ?? "Нет строк для формирования файла ЧЗ.");
            }

            exportLineCount = excelResult.Rows.Count;
        }

        var requiredQty = lines.Sum(line => line.RequiredQty);
        var coveredQty = lines.Sum(line => line.CoveredQty) + reusedCodeQty + createdCodeQty;
        var message = excelResult?.FileBytes != null
            ? $"Excel ЧЗ сформирован из заказа. Строк: {lines.Count}, строк Excel: {exportLineCount}, кодов создано: {createdCodeQty:0.###}, переиспользовано: {reusedCodeQty:0.###}."
            : "Маркировка по заказу уже проведена: новых кодов создавать не нужно.";

        return new OrderMarkingExportResult(
            true,
            message,
            excelResult?.FileBytes,
            BuildFileName(order, generatedAt),
            lines.Count,
            exportLineCount,
            requiredQty,
            coveredQty,
            createdCodeQty,
            reusedCodeQty,
            lines);
    }

    private IEnumerable<OrderMarkingExportLineSummary> BuildLineSummaries(Order order)
    {
        var orderLines = _data.GetOrderLines(order.Id);
        var shippedByLine = order.Type == OrderType.Customer
            ? _data.GetShippedTotalsByOrderLine(order.Id)
            : new Dictionary<long, double>();
        var reservedByLine = order.Type == OrderType.Customer
            ? _data.GetOrderReceiptPlanLines(order.Id)
                .Where(line => line.QtyPlanned > 0)
                .GroupBy(line => line.OrderLineId)
                .ToDictionary(group => group.Key, group => group.Sum(line => line.QtyPlanned))
            : new Dictionary<long, double>();

        var markableLines = orderLines
            .Select(line => (Line: line, Item: _data.FindItemById(line.ItemId)))
            .Where(pair => pair.Item?.ItemTypeEnableMarking == true
                           && !string.IsNullOrWhiteSpace(pair.Item.Gtin))
            .ToList();
        if (markableLines.Count == 0)
        {
            yield break;
        }

        var itemIds = markableLines.Select(pair => pair.Line.ItemId).Distinct().ToArray();
        var existingOrderCodesByItem = _data.GetMarkingOrdersByItemIds(itemIds)
            .Where(task => task.ItemId.HasValue
                           && !IsTerminalFailed(task.Status)
                           && IsOrderLinkedTask(task, order.Id))
            .GroupBy(task => task.ItemId!.Value)
            .ToDictionary(
                group => group.Key,
                group => (double)group.Sum(task => _data.CountMarkingCodesByMarkingOrder(task.Id)));

        var remainingCodeCoverByItem = new Dictionary<long, double>(existingOrderCodesByItem);
        foreach (var pair in markableLines)
        {
            var line = pair.Line;
            var item = pair.Item!;
            var requiredQty = Math.Max(0, line.QtyOrdered);
            var stockCoveredQty = 0d;
            if (order.Type == OrderType.Customer)
            {
                shippedByLine.TryGetValue(line.Id, out var shippedQty);
                reservedByLine.TryGetValue(line.Id, out var reservedQty);
                stockCoveredQty = Math.Min(requiredQty, Math.Max(0, shippedQty) + Math.Max(0, reservedQty));
            }

            remainingCodeCoverByItem.TryGetValue(line.ItemId, out var codeCover);
            var codeCoveredQty = Math.Min(Math.Max(0, requiredQty - stockCoveredQty), codeCover);
            remainingCodeCoverByItem[line.ItemId] = Math.Max(0, codeCover - codeCoveredQty);
            var coveredQty = stockCoveredQty + codeCoveredQty;
            var exportQty = order.Type == OrderType.Internal
                ? Math.Max(0, requiredQty - codeCoveredQty)
                : Math.Max(0, requiredQty - coveredQty);

            yield return new OrderMarkingExportLineSummary(
                line.Id,
                line.ItemId,
                item.Name,
                item.Gtin!.Trim(),
                requiredQty,
                coveredQty,
                codeCoveredQty,
                exportQty);
        }
    }

    private static MarkingOrder CreateMarkingOrder(
        Order order,
        OrderMarkingExportLineSummary line,
        int requestedQty,
        DateTime generatedAt,
        int sequence)
    {
        return new MarkingOrder
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            ItemId = line.ItemId,
            Gtin = line.Gtin,
            RequestedQuantity = requestedQty,
            RequestNumber = BuildRequestNumber(order.Id, line.ItemId, generatedAt, sequence),
            Status = MarkingOrderStatus.WaitingForCodes,
            Notes = order.Type == OrderType.Internal
                ? "Order-based ЧЗ для внутреннего производственного заказа."
                : "Order-based ЧЗ для клиентского заказа.",
            SourceType = MarkingNeedCreationService.ProductionOrderSourceType,
            SourceOrderId = order.Id,
            RequestedAt = generatedAt,
            CreatedAt = generatedAt,
            UpdatedAt = generatedAt
        };
    }

    private static bool IsOrderLinkedTask(MarkingOrder task, long orderId)
    {
        return task.OrderId == orderId || task.SourceOrderId == orderId;
    }

    private static bool IsTerminalFailed(string? status)
    {
        return string.Equals(status, MarkingOrderStatus.Cancelled, StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, MarkingOrderStatus.Failed, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRequestNumber(long orderId, long itemId, DateTime generatedAt, int sequence)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"ORDER-{orderId}-{itemId}-{generatedAt:yyyyMMddHHmmssfff}-{sequence:000}");
    }

    private static string BuildFileName(Order order, DateTime generatedAt)
    {
        var normalizedRef = NormalizeFilePart(order.OrderRef);
        return $"chestny_znak_order_{normalizedRef}_{generatedAt:yyyyMMdd_HHmmss}.xlsx";
    }

    private static string NormalizeFilePart(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        var result = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "order" : result;
    }
}

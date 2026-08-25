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

    public OrderMarkingExportService(IDataStore data)
    {
        _data = data;
    }

    public OrderMarkingExportPreviewResult Preview(long orderId)
    {
        if (_data is IMarkingCutoverRuntimeGuard cutoverGuard)
        {
            try
            {
                cutoverGuard.RequireEnforcedMarkingWorkflow("marking_export_preview");
            }
            catch (InvalidOperationException ex)
            {
                return OrderMarkingExportPreviewResult.Failure(ex.Message);
            }
        }

        var order = _data.GetOrder(orderId);
        if (order == null)
        {
            return OrderMarkingExportPreviewResult.Failure("Заказ не найден.");
        }

        if (order.Status == OrderStatus.Shipped)
        {
            return OrderMarkingExportPreviewResult.Failure("Нельзя формировать Excel ЧЗ для выполненного заказа.");
        }

        var configurationError = FindMarkingConfigurationError(order.Id);
        if (configurationError != null)
        {
            return OrderMarkingExportPreviewResult.Failure(configurationError);
        }

        var huCodesByLine = order.Type == OrderType.Customer
            ? BuildProductionHuCodesByOrderLine(order.Id)
            : new Dictionary<long, IReadOnlyList<string>>();
        var plannedQtyByLine = BuildActiveProductionPalletQtyByOrderLine(order.Id);
        var activeScopedQtyByItem = _data is IMarkingAggregateStore scopeAggregateStore
            ? scopeAggregateStore.GetActiveMarkingRequestScopeQuantityByItem(order.Id)
            : new Dictionary<long, double>();
        var tasksByItem = _data.GetMarkingOrdersByItemIds(
                _data.GetOrderLines(order.Id).Select(line => line.ItemId).Distinct().ToArray())
            .Where(task => task.ItemId.HasValue && !IsTerminalFailed(task.Status) && IsOrderLinkedTask(task, order.Id))
            .GroupBy(task => task.ItemId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var lines = BuildLineSummaries(order)
            .Select(summary =>
            {
                huCodesByLine.TryGetValue(summary.OrderLineId, out var huCodes);
                huCodes ??= Array.Empty<string>();
                plannedQtyByLine.TryGetValue(summary.OrderLineId, out var plannedQty);
                tasksByItem.TryGetValue(summary.ItemId, out var tasks);
                tasks ??= Array.Empty<MarkingOrder>();
                var scopedQty = _data is IMarkingAggregateStore
                                && activeScopedQtyByItem.TryGetValue(summary.ItemId, out var activeScopedQty)
                    ? activeScopedQty
                    : tasks.Sum(task => Math.Max(0, task.RequiredQuantity));
                var requestedQty = tasks.Sum(task => Math.Max(0, task.RequestedQuantity));
                var importedQty = tasks.Sum(task => _data.CountMarkingCodesByMarkingOrder(task.Id));
                var reserveQty = tasks.Sum(task => Math.Max(0, task.ReserveQuantity));
                var remainingToProduce = Math.Max(0, summary.ExportQty + summary.ExistingCodeQty);
                var previewQty = Math.Max(0, summary.ExportQty) + Math.Max(0, summary.ExistingCodeQty);
                return new OrderMarkingExportPreviewLine(
                    summary.OrderLineId,
                    summary.ItemId,
                    summary.ItemName,
                    summary.Gtin,
                    previewQty,
                    huCodes.Count,
                    huCodes,
                    MarkingApplicable: true,
                    RequiredQty: summary.RequiredQty,
                    CoveredQty: summary.CoveredQty,
                    RemainingToProduce: remainingToProduce,
                    PlannedQty: plannedQty,
                    UnplannedQty: Math.Max(0, remainingToProduce - plannedQty),
                    ScopedQty: scopedQty,
                    RequestedQty: requestedQty,
                    ImportedQty: importedQty,
                    ReserveQty: reserveQty);
            })
            .Where(line => line.Qty > QtyTolerance)
            .ToList();

        return new OrderMarkingExportPreviewResult(
            true,
            lines.Count == 0
                ? "В заказе нет строк для формирования Excel ЧЗ."
                : "Предпросмотр Excel ЧЗ.",
            order.Id,
            order.OrderRef,
            lines.Count,
            lines.Sum(line => line.Qty),
            lines);
    }

    public OrderMarkingExportResult Export(long orderId, DateTime generatedAt)
    {
        OrderMarkingExportResult? result = null;
        try
        {
            _data.ExecuteInTransaction(scopedStore =>
            {
                if (!scopedStore.LockOrdersForUpdate(new[] { orderId }))
                {
                    result = OrderMarkingExportResult.Failure("Заказ не найден.");
                    return;
                }

                result = new OrderMarkingExportService(scopedStore).ExportLocked(orderId, generatedAt, generateExcel: true);
            });
        }
        catch (OrderMarkingExportRollbackException ex)
        {
            return OrderMarkingExportResult.Failure(ex.Message);
        }

        return result ?? OrderMarkingExportResult.Failure("Не удалось сформировать Excel ЧЗ.");
    }

    public OrderMarkingExportResult EnsureCustomerImportEnvelope(long orderId, DateTime createdAt)
    {
        OrderMarkingExportResult? result = null;
        try
        {
            _data.ExecuteInTransaction(scopedStore =>
            {
                if (!scopedStore.LockOrdersForUpdate(new[] { orderId }))
                {
                    result = OrderMarkingExportResult.Failure("Заказ не найден.");
                    return;
                }

                var order = scopedStore.GetOrder(orderId);
                result = order == null
                    ? OrderMarkingExportResult.Failure("Заказ не найден.")
                    : !string.Equals(order.MarkingResponsibility, MarkingResponsibility.Customer, StringComparison.OrdinalIgnoreCase)
                        ? new OrderMarkingExportResult(
                            true, "Customer import envelope не требуется.", null, string.Empty,
                            0, 0, 0, 0, 0, 0, Array.Empty<OrderMarkingExportLineSummary>())
                        : new OrderMarkingExportService(scopedStore).ExportLocked(orderId, createdAt, generateExcel: false);
            });
        }
        catch (OrderMarkingExportRollbackException ex)
        {
            return OrderMarkingExportResult.Failure(ex.Message);
        }

        return result ?? OrderMarkingExportResult.Failure("Не удалось подготовить customer import scope.");
    }

    private OrderMarkingExportResult ExportLocked(long orderId, DateTime generatedAt, bool generateExcel)
    {
        if (_data is IMarkingCutoverRuntimeGuard cutoverGuard)
        {
            try
            {
                cutoverGuard.RequireEnforcedMarkingWorkflow(
                    generateExcel ? "marking_export" : "customer_marking_import_envelope");
            }
            catch (InvalidOperationException ex)
            {
                throw new OrderMarkingExportRollbackException(ex.Message);
            }
        }

        var order = _data.GetOrder(orderId);
        if (order == null)
        {
            return OrderMarkingExportResult.Failure("Заказ не найден.");
        }

        if (order.Status == OrderStatus.Shipped)
        {
            return OrderMarkingExportResult.Failure("Нельзя формировать Excel ЧЗ для выполненного заказа.");
        }

        if (generateExcel
            && string.Equals(order.MarkingResponsibility, MarkingResponsibility.Customer, StringComparison.OrdinalIgnoreCase))
        {
            return OrderMarkingExportResult.Failure(
                "Для marking_responsibility=CUSTOMER Excel FlowStock недоступен; загрузите ответ КМ в карточке заказа.");
        }

        var configurationError = FindMarkingConfigurationError(order.Id);
        if (configurationError != null)
        {
            return OrderMarkingExportResult.Failure(configurationError);
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
        var activeScopedQtyByItem = _data is IMarkingAggregateStore scopeAggregateStore
            ? scopeAggregateStore.GetActiveMarkingRequestScopeQuantityByItem(order.Id)
            : new Dictionary<long, double>();

        var taskIdsToExport = new List<Guid>();
        var taskIdsAvailableForReexport = new List<Guid>();
        var createdCodeQty = 0d;
        var reusedCodeQty = 0d;
        var exportLineCount = 0;
        var sequence = 1;
        var reserveQuantity = generateExcel
            ? _data is IMarkingAggregateStore aggregateStore
                ? aggregateStore.GetDefaultMarkingReserveQuantity()
                : 5
            : 0;

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
            var taskScopedQty = _data is IMarkingAggregateStore
                                && activeScopedQtyByItem.TryGetValue(group.Key, out var activeScopedQty)
                ? activeScopedQty
                : itemTasks.Sum(task => Math.Max(
                    0,
                    task.RequiredQuantity > 0 ? task.RequiredQuantity : task.RequestedQuantity));
            var taskCodeQtyById = itemTasks.ToDictionary(task => task.Id, task => _data.CountMarkingCodesByMarkingOrder(task.Id));
            var taskCodeQty = taskCodeQtyById.Sum(pair => pair.Value);
            taskIdsAvailableForReexport.AddRange(itemTasks
                .Where(task => taskCodeQtyById.TryGetValue(task.Id, out var codes) && codes > 0)
                .Select(task => task.Id));

            foreach (var task in itemTasks.Where(task => taskCodeQtyById.TryGetValue(task.Id, out var codes)
                                                         && codes + QtyTolerance < task.RequestedQuantity))
            {
                taskIdsToExport.Add(task.Id);
            }

            var missingTaskQty = Math.Max(0, itemRequiredQty - taskScopedQty);
            if (missingTaskQty <= QtyTolerance)
            {
                continue;
            }

            var line = group.First();
            var scopedRequiredQty = (int)Math.Ceiling(missingTaskQty);
            var newTask = CreateMarkingOrder(
                order,
                line,
                scopedRequiredQty,
                reserveQuantity,
                generatedAt,
                sequence++);
            _data.AddMarkingOrder(newTask);
            if (_data is IMarkingAggregateStore scopeStore)
            {
                try
                {
                    scopeStore.CreateImmutableRequestScopes(
                        newTask.Id,
                        order.Id,
                        line.ItemId,
                        line.Gtin,
                        scopedRequiredQty,
                        generatedAt);
                }
                catch (InvalidOperationException ex)
                {
                    throw new OrderMarkingExportRollbackException(ex.Message);
                }
            }
            taskIdsToExport.Add(newTask.Id);
        }

        taskIdsToExport = taskIdsToExport.Distinct().ToList();
        if (taskIdsToExport.Count == 0)
        {
            taskIdsToExport = taskIdsAvailableForReexport
                .Distinct()
                .ToList();
        }

        MarkingExcelExportResult? excelResult = null;
        if (generateExcel && taskIdsToExport.Count > 0)
        {
            excelResult = new MarkingExcelService(_data).Export(taskIdsToExport, Array.Empty<long>(), generatedAt);
            if (!excelResult.IsSuccess || excelResult.FileBytes == null)
            {
                throw new OrderMarkingExportRollbackException(
                    excelResult.Error ?? "Нет строк для формирования файла ЧЗ.");
            }

            exportLineCount = excelResult.Rows.Count;
        }

        var requiredQty = lines.Sum(line => line.RequiredQty);
        var coveredQty = lines.Sum(line => line.CoveredQty);
        var message = !generateExcel
            ? "Customer import envelope подготовлен: immutable request scopes созданы с reserve 0, Excel не формировался."
            : excelResult?.FileBytes != null
            ? $"Excel-заявка ЧЗ сформирована. Строк заказа: {lines.Count}, строк Excel: {exportLineCount}. Коды маркировки при экспорте не создаются."
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
            ? CustomerOutboundBoundHuService.BuildUnshippedBoundHuQtyByOrderLine(_data, order.Id)
            : new Dictionary<long, double>();
        var activeProductionPalletQtyByLine = order.Type == OrderType.Customer
            ? BuildActiveProductionPalletQtyByOrderLine(order.Id)
            : new Dictionary<long, double>();

        var markableLines = orderLines
            .Select(line => (Line: line, Item: _data.FindItemById(line.ItemId)))
            .Where(pair => pair.Item?.ChzMarkingApplicable == true
                           && !string.IsNullOrWhiteSpace(pair.Item.Gtin))
            .ToList();
        if (markableLines.Count == 0)
        {
            yield break;
        }

        var aggregateCoverageByLine = _data is IMarkingAggregateStore aggregateStore
            ? aggregateStore.GetAggregateMarkingCoverageByOrderLine(order.Id)
            : new Dictionary<long, MarkingLineAggregateCoverage>();
        var legacyExemptByLine = _data is IMarkingAggregateStore legacyStore
            ? legacyStore.GetLegacyExemptQuantityByOrderLine(order.Id)
            : new Dictionary<long, double>();
        foreach (var pair in markableLines)
        {
            var line = pair.Line;
            var item = pair.Item!;
            var requiredQty = Math.Max(0, line.QtyOrdered);
            legacyExemptByLine.TryGetValue(line.Id, out var legacyExemptQty);
            var realRequiredQty = Math.Max(0, requiredQty - Math.Min(requiredQty, Math.Max(0, legacyExemptQty)));
            var productionBaseQty = realRequiredQty;
            var stockCoveredQty = 0d;
            if (order.Type == OrderType.Customer)
            {
                shippedByLine.TryGetValue(line.Id, out var shippedQty);
                reservedByLine.TryGetValue(line.Id, out var reservedQty);
                stockCoveredQty = Math.Min(requiredQty, Math.Max(0, shippedQty) + Math.Max(0, reservedQty));
                activeProductionPalletQtyByLine.TryGetValue(line.Id, out var activePalletQty);
                productionBaseQty = activePalletQty > QtyTolerance
                    ? Math.Min(activePalletQty, Math.Max(0, realRequiredQty - stockCoveredQty))
                    : Math.Max(0, realRequiredQty - stockCoveredQty);
            }

            aggregateCoverageByLine.TryGetValue(line.Id, out var aggregateCoverage);
            aggregateCoverage ??= new MarkingLineAggregateCoverage(0, 0);
            var operationalCoveredQty = Math.Min(
                Math.Max(0, productionBaseQty),
                Math.Max(0, aggregateCoverage.OperationalQuantity));
            var coveredQty = Math.Min(realRequiredQty, Math.Max(0, aggregateCoverage.TotalQuantity));
            var exportQty = order.Type == OrderType.Internal
                ? Math.Max(0, realRequiredQty - operationalCoveredQty)
                : Math.Max(0, productionBaseQty - operationalCoveredQty);

            yield return new OrderMarkingExportLineSummary(
                line.Id,
                line.ItemId,
                item.Name,
                item.Gtin!.Trim(),
                requiredQty,
                coveredQty,
                operationalCoveredQty,
                exportQty);
        }
    }

    private string? FindMarkingConfigurationError(long orderId)
    {
        foreach (var line in _data.GetOrderLines(orderId)
                     .Where(line => line.CancelledAt == null && line.QtyOrdered > QtyTolerance))
        {
            var item = _data.FindItemById(line.ItemId);
            if (item?.ItemTypeEnableMarking == true && string.IsNullOrWhiteSpace(item.Gtin))
            {
                return $"MARKING_GTIN_MISSING: для маркируемого товара '{item.Name}' не настроен GTIN.";
            }
        }

        return null;
    }

    private Dictionary<long, double> BuildActiveProductionPalletQtyByOrderLine(long orderId)
    {
        var result = new Dictionary<long, double>();
        foreach (var pallet in EnumerateActiveProductionPallets(orderId))
        {
            if (pallet.Lines.Count > 0)
            {
                foreach (var line in pallet.Lines.Where(line => line.OrderLineId.HasValue))
                {
                    AddQty(result, line.OrderLineId!.Value, Math.Max(0, line.PlannedQty));
                }

                continue;
            }

            if (pallet.OrderLineId.HasValue)
            {
                AddQty(result, pallet.OrderLineId.Value, Math.Max(0, pallet.PlannedQty));
            }
        }

        return result;
    }

    private Dictionary<long, IReadOnlyList<string>> BuildProductionHuCodesByOrderLine(long orderId)
    {
        var result = new Dictionary<long, List<string>>();
        foreach (var pallet in EnumerateActiveProductionPallets(orderId))
        {
            if (string.IsNullOrWhiteSpace(pallet.HuCode))
            {
                continue;
            }

            var huCode = pallet.HuCode.Trim();
            if (pallet.Lines.Count > 0)
            {
                foreach (var line in pallet.Lines.Where(line => line.OrderLineId.HasValue))
                {
                    AddHuCode(result, line.OrderLineId!.Value, huCode);
                }

                continue;
            }

            if (pallet.OrderLineId.HasValue)
            {
                AddHuCode(result, pallet.OrderLineId.Value, huCode);
            }
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private IEnumerable<ProductionPallet> EnumerateActiveProductionPallets(long orderId)
    {
        foreach (var doc in _data.GetDocsByOrder(orderId).Where(doc => doc.Type == DocType.ProductionReceipt))
        {
            foreach (var pallet in _data.GetProductionPalletsByDoc(doc.Id)
                         .Where(pallet => ProductionPalletStatus.IsOperational(pallet.Status)))
            {
                yield return pallet;
            }
        }
    }

    private static void AddHuCode(IDictionary<long, List<string>> codesByLine, long orderLineId, string huCode)
    {
        if (!codesByLine.TryGetValue(orderLineId, out var codes))
        {
            codes = new List<string>();
            codesByLine[orderLineId] = codes;
        }

        codes.Add(huCode);
    }

    private static void AddQty(IDictionary<long, double> totals, long orderLineId, double qty)
    {
        if (qty <= QtyTolerance)
        {
            return;
        }

        totals[orderLineId] = totals.TryGetValue(orderLineId, out var current)
            ? current + qty
            : qty;
    }

    private static MarkingOrder CreateMarkingOrder(
        Order order,
        OrderMarkingExportLineSummary line,
        int requiredQty,
        int reserveQty,
        DateTime generatedAt,
        int sequence)
    {
        return new MarkingOrder
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            ItemId = line.ItemId,
            Gtin = line.Gtin,
            RequiredQuantity = requiredQty,
            ReserveQuantity = reserveQty,
            RequestedQuantity = checked(requiredQty + reserveQty),
            OriginalOrderId = order.Id,
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

    private sealed class OrderMarkingExportRollbackException : Exception
    {
        public OrderMarkingExportRollbackException(string message)
            : base(message)
        {
        }
    }
}

using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class ProductionPalletService
{
    private const double QtyTolerance = 0.000001d;
    private const string PlanHuCreatedBy = "PRODUCTION-PALLET-PLAN";
    private readonly IDataStore _data;
    private readonly ProductionFillCloseService? _fillClose;

    public ProductionPalletService(IDataStore data)
        : this(data, fillClose: null)
    {
    }

    public ProductionPalletService(IDataStore data, ProductionFillCloseService? fillClose)
    {
        _data = data;
        _fillClose = fillClose;
    }

    public ProductionPalletDocument Plan(long docId)
    {
        var doc = RequireProductionReceipt(docId);
        if (doc.Status == DocStatus.Closed)
        {
            throw new InvalidOperationException("Документ уже закрыт.");
        }

        _data.PlanProductionPallets(docId, DateTime.Now);
        return Get(docId);
    }

    public ProductionPalletOrderPlanResult PlanOrder(long orderId)
    {
        return PlanOrder(orderId, scopedOrderLineIds: null);
    }

    public void SyncOrderLinePlan(long orderId, long orderLineId, double orderedQty, double? oldOrderedQty = null, string source = "UpdateOrder")
    {
        var order = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        if (order.Type is not (OrderType.Internal or OrderType.Customer))
        {
            return;
        }

        if (order.Status is not (OrderStatus.InProgress or OrderStatus.Draft or OrderStatus.Accepted))
        {
            return;
        }

        _data.ExecuteInTransaction(store =>
            SyncOrderLinePlanInStore(store, orderId, orderLineId, orderedQty, oldOrderedQty, source));
    }

    internal void SyncOrderLinePlanInStore(
        IDataStore store,
        long orderId,
        long orderLineId,
        double orderedQty,
        double? oldOrderedQty,
        string source)
    {
        var order = store.GetOrder(orderId);
        if (order == null
            || order.Type is not (OrderType.Internal or OrderType.Customer)
            || order.Status is not (OrderStatus.InProgress or OrderStatus.Draft or OrderStatus.Accepted))
        {
            return;
        }

        var committedQty = GetProtectedCoverageQtyForOrderLine(store, order, orderLineId, orderedQty);
        var activePlannedBefore = GetOpenProductionPalletsForOrderLine(store, orderId, orderLineId)
            .Sum(pallet => ResolvePalletQtyForOrderLine(pallet, orderLineId));
        var missingBeforeTrim = Math.Max(0, orderedQty - committedQty - activePlannedBefore);

        var affectedOrderLineIds = TrimSurplusOpenPallets(store, order, orderId, orderLineId, orderedQty);

        var activePlannedAfterTrim = GetOpenProductionPalletsForOrderLine(store, orderId, orderLineId)
            .Sum(pallet => ResolvePalletQtyForOrderLine(pallet, orderLineId));
        var cancelledQty = Math.Max(0, activePlannedBefore - activePlannedAfterTrim);
        var missingAfterTrim = Math.Max(0, orderedQty - committedQty - activePlannedAfterTrim);
        var createdQty = 0d;
        var action = missingAfterTrim > QtyTolerance
            ? "append_planned"
            : cancelledQty > QtyTolerance
                ? "trim_open"
                : "noop";

        if (missingAfterTrim > QtyTolerance)
        {
            var openBeforeAppend = activePlannedAfterTrim;
            var preparedDoc = FindPreparedOpenProductionReceipt(store, orderId, requireRemaining: false);
            var prdDocIdForAppend = preparedDoc?.Id ?? 0;
            AppendPlannedPalletsForOrderLinesInStore(
                store,
                order,
                orderId,
                affectedOrderLineIds.Count > 0 ? affectedOrderLineIds : [orderLineId],
                allowEmptyRemaining: true,
                out _,
                existingPrdDocId: prdDocIdForAppend);
            activePlannedAfterTrim = GetOpenProductionPalletsForOrderLine(store, orderId, orderLineId)
                .Sum(pallet => ResolvePalletQtyForOrderLine(pallet, orderLineId));
            createdQty = Math.Max(0, activePlannedAfterTrim - openBeforeAppend);
        }

        ProductionPalletPlanSyncDiagnostics.Log(new ProductionPalletPlanSyncReport
        {
            Source = source,
            OrderId = orderId,
            OrderLineId = orderLineId,
            OldQty = oldOrderedQty,
            NewQty = orderedQty,
            FilledQty = committedQty,
            ActivePlannedQtyBefore = activePlannedBefore,
            MissingQty = missingBeforeTrim > missingAfterTrim ? missingBeforeTrim : missingAfterTrim,
            CreatedQty = createdQty,
            CancelledQty = cancelledQty,
            ActivePlannedQtyAfter = activePlannedAfterTrim,
            Action = action
        });
    }

    internal IReadOnlyList<long> CancelFuturePlanForOrderLineAndResolveAffectedLinesInStore(
        IDataStore store,
        long orderId,
        long orderLineId)
    {
        var pallets = GetOpenProductionPalletsForOrderLine(store, orderId, orderLineId);
        var affected = pallets.SelectMany(GetPalletOrderLineIds).Append(orderLineId).Distinct().ToArray();
        if (pallets.Count > 0)
        {
            TombstoneProductionPalletDocLines(store, pallets);
            store.CancelProductionPallets(pallets.Select(pallet => pallet.Id).ToArray());
        }

        return affected;
    }

    public ProductionPalletOrderPlanResult PlanOrder(long orderId, IReadOnlyCollection<long>? scopedOrderLineIds)
    {
        var prdDocId = 0L;
        var wasExisting = false;
        var productionRequired = true;
        _data.ExecuteInTransaction(store =>
        {
            var order = store.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
            if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
            {
                throw new InvalidOperationException(order.Status == OrderStatus.Merged
                    ? "Заказ объединён с другим заказом. Выпуск по нему не требуется."
                    : "Заказ недоступен для планирования паллет.");
            }

            if (order.Type == OrderType.Internal
                && order.Status is not OrderStatus.InProgress
                && order.Status is not OrderStatus.Draft)
            {
                throw new InvalidOperationException("Дополнение плана паллет доступно только для заказа в статусе «В работе».");
            }

            var preparedDoc = FindPreparedOpenProductionReceipt(store, orderId, requireRemaining: false);
            if (preparedDoc != null)
            {
                prdDocId = preparedDoc.Id;
                wasExisting = true;
            }

            productionRequired = AppendPlannedPalletsForOrderLinesInStore(
                store,
                order,
                orderId,
                scopedOrderLineIds,
                allowEmptyRemaining: false,
                out prdDocId,
                existingPrdDocId: prdDocId);
        });

        return productionRequired || prdDocId > 0
            ? BuildOrderPlanResult(orderId, prdDocId, wasExisting)
            : BuildNoProductionRequiredResult(orderId);
    }

    private static bool AppendPlannedPalletsForOrderLinesInStore(
        IDataStore store,
        Order order,
        long orderId,
        IReadOnlyCollection<long>? scopedOrderLineIds,
        bool allowEmptyRemaining,
        out long prdDocId,
        long existingPrdDocId = 0)
    {
        prdDocId = existingPrdDocId;
        var remainingLines = GetLinesNeedingPalletAppend(store, order);
        if (scopedOrderLineIds is { Count: > 0 })
        {
            var scoped = scopedOrderLineIds.Where(id => id > 0).ToHashSet();
            remainingLines = remainingLines
                .Where(line => scoped.Contains(line.OrderLineId))
                .ToList();
        }

        if (remainingLines.Count == 0)
        {
            if (allowEmptyRemaining || prdDocId != 0)
            {
                return false;
            }

            if (order.Type == OrderType.Customer)
            {
                return false;
            }

            throw new InvalidOperationException("Нет остатка к наполнению по заказу.");
        }

        var itemsById = store.GetItems(null).ToDictionary(item => item.Id, item => item);
        var orderLinesById = store.GetOrderLines(orderId).ToDictionary(line => line.Id, line => line);
        var manualMixedLineIds = GetManualMixedOrderLineIds(remainingLines, orderLinesById);
        foreach (var line in remainingLines)
        {
            if (!itemsById.ContainsKey(line.ItemId))
            {
                throw new InvalidOperationException("Номенклатура строки заказа не найдена.");
            }

            if (manualMixedLineIds.Contains(line.OrderLineId))
            {
                continue;
            }

            if (!itemsById.TryGetValue(line.ItemId, out var item)
                || !item.MaxQtyPerHu.HasValue
                || item.MaxQtyPerHu.Value <= QtyTolerance)
            {
                throw new InvalidOperationException("Не задано количество на паллете для номенклатуры");
            }
        }

        var targetLocation = ResolveProductionPalletPlanLocation(store);
        if (prdDocId == 0)
        {
            prdDocId = FindReusableEmptyProductionReceipt(store, orderId)?.Id ?? CreateProductionReceipt(store, order).Id;
        }

        var mixedLineIds = new HashSet<long>();
        foreach (var group in remainingLines
                     .Where(line => manualMixedLineIds.Contains(line.OrderLineId))
                     .GroupBy(line => orderLinesById[line.OrderLineId].ProductionPalletGroup!.Trim().ToUpperInvariant()))
        {
            var groupLines = group.OrderBy(line => line.OrderLineId).ToList();
            foreach (var line in groupLines)
            {
                if (!itemsById.ContainsKey(line.ItemId))
                {
                    throw new InvalidOperationException("Номенклатура строки заказа не найдена.");
                }
            }

            AddMixedPlannedPalletLines(store, prdDocId, groupLines, targetLocation.Id);
            foreach (var line in groupLines)
            {
                mixedLineIds.Add(line.OrderLineId);
            }
        }

        foreach (var line in remainingLines.Where(line => !mixedLineIds.Contains(line.OrderLineId)))
        {
            var item = itemsById[line.ItemId];
            AddPlannedPalletLines(store, prdDocId, line, item.MaxQtyPerHu!.Value, targetLocation.Id);
        }

        store.PlanProductionPallets(prdDocId, DateTime.Now);
        return true;
    }

    public ProductionPalletCancelPlanResult CancelOrderPlan(long orderId)
    {
        var options = GetCancelPlanOptions(orderId);
        var selectedPalletIds = options.Rows
            .Where(row => row.IsSelectable)
            .Select(row => row.PalletId)
            .ToArray();
        return CancelOrderPlan(orderId, selectedPalletIds);
    }

    public ProductionPalletCancelPlanResult CancelOrderPlan(long orderId, IReadOnlyCollection<long> selectedPalletIds)
    {
        var order = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
        {
            throw new InvalidOperationException(order.Status == OrderStatus.Merged
                ? "Заказ объединён с другим заказом. Выпуск по нему не требуется."
                : "Заказ недоступен для удаления плана паллет.");
        }

        var requestedIds = selectedPalletIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (requestedIds.Length == 0)
        {
            return new ProductionPalletCancelPlanResult
            {
                OrderId = order.Id,
                PrdDocId = 0,
                Message = "Нет выбранных паллет для удаления.",
                RequestedPalletIds = requestedIds,
                SkippedPalletIds = requestedIds
            };
        }

        var prdDocIds = Array.Empty<long>();
        var removedPalletIds = Array.Empty<long>();
        var skippedPalletIds = requestedIds;
        ProductionPalletPlanCleanupCounts cleanup = null!;
        _data.ExecuteInTransaction(store =>
        {
            var docsById = store.GetDocsByOrder(order.Id)
                .Where(doc => doc.Type == DocType.ProductionReceipt)
                .ToDictionary(doc => doc.Id, doc => doc);
            var selected = docsById.Values
                .SelectMany(doc => store.GetProductionPalletsByDoc(doc.Id))
                .Where(pallet => requestedIds.Contains(pallet.Id))
                .Where(pallet => pallet.OrderId == order.Id)
                .Where(pallet => docsById.TryGetValue(pallet.PrdDocId, out var doc) && doc.Status != DocStatus.Closed)
                .Where(IsPendingFillPallet)
                .ToArray();

            if (selected.Length == 0)
            {
                cleanup = new ProductionPalletPlanCleanupCounts();
                prdDocIds = Array.Empty<long>();
                return;
            }

            prdDocIds = selected.Select(pallet => pallet.PrdDocId).Distinct().ToArray();
            cleanup = store.DeleteProductionPalletPlanPallets(selected.Select(pallet => pallet.Id).ToArray());
            removedPalletIds = cleanup.RemovedPalletIds
                .Where(id => id > 0)
                .Distinct()
                .Order()
                .ToArray();
            skippedPalletIds = requestedIds
                .Except(removedPalletIds)
                .Order()
                .ToArray();
            foreach (var prdDocId in prdDocIds)
            {
                EmptyDraftProductionReceiptCleanup.TryDeleteEmptyDraftProductionReceiptIfSafe(store, order.Id, prdDocId);
            }
        });

        return new ProductionPalletCancelPlanResult
        {
            OrderId = order.Id,
            PrdDocId = prdDocIds.FirstOrDefault(),
            Message = cleanup.RemovedPalletCount > 0
                ? "Выбранные паллеты удалены из плана."
                : "Нет доступных для удаления паллет.",
            RemovedPalletCount = cleanup.RemovedPalletCount,
            RemovedLineCount = cleanup.RemovedLineCount,
            RequestedPalletIds = requestedIds,
            RemovedPalletIds = removedPalletIds,
            SkippedPalletIds = skippedPalletIds
        };
    }

    public ProductionPalletCancelPlanOptions GetCancelPlanOptions(long orderId)
    {
        var order = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        var docs = _data.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .OrderBy(doc => doc.Id)
            .ToArray();
        var docsById = docs.ToDictionary(doc => doc.Id, doc => doc);
        var markingGenerated = order.EffectiveMarkingStatus == MarkingStatus.Printed
                               || order.MarkingExcelGeneratedAt.HasValue
                               || order.MarkingPrintedAt.HasValue;
        var rows = docs
            .SelectMany(doc => _data.GetProductionPalletsByDoc(doc.Id))
            .Where(pallet => pallet.OrderId == orderId)
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .Where(HasOrderLineOwnership)
            .Select(pallet =>
            {
                docsById.TryGetValue(pallet.PrdDocId, out var doc);
                var isClosedDoc = doc?.Status == DocStatus.Closed;
                var isFilled = string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase);
                var isSelectable = !isClosedDoc && IsPendingFillPallet(pallet);
                var disabledReason = isFilled
                    ? "Нельзя удалить: паллета уже наполнена/выпущена"
                    : isClosedDoc
                        ? "Нельзя удалить: выпуск уже закрыт"
                        : isSelectable
                            ? null
                            : "Нельзя удалить: статус паллеты не позволяет удаление";
                return new ProductionPalletCancelPlanRow
                {
                    PalletId = pallet.Id,
                    PrdDocId = pallet.PrdDocId,
                    PrdDocRef = doc?.DocRef ?? string.Empty,
                    OrderLineId = pallet.OrderLineId,
                    ItemId = pallet.ItemId,
                    ItemName = pallet.ItemName,
                    HuCode = pallet.HuCode,
                    PlannedQty = pallet.PlannedQty,
                    Status = pallet.Status,
                    IsSelectable = isSelectable,
                    IsSelectedByDefault = isSelectable,
                    DisabledReason = disabledReason,
                    HasMarkingWarning = markingGenerated
                                        && string.Equals(pallet.Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase)
                };
            })
            .OrderBy(row => row.OrderLineId ?? long.MaxValue)
            .ThenBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ProductionPalletCancelPlanOptions
        {
            OrderId = order.Id,
            OrderRef = order.OrderRef,
            Rows = rows
        };
    }

    public ProductionPalletPlanAdoptionResult AdoptPlanFromInternal(long targetCustomerOrderId, long sourceInternalOrderId)
    {
        ProductionPalletPlanAdoptionResult result = null!;
        _data.ExecuteInTransaction(store =>
        {
            var sourceOrder = store.GetOrder(sourceInternalOrderId)
                              ?? throw new ProductionPalletPlanAdoptionException("SOURCE_ORDER_NOT_FOUND", "Внутренний заказ-источник не найден.");
            var targetOrder = store.GetOrder(targetCustomerOrderId)
                              ?? throw new ProductionPalletPlanAdoptionException("TARGET_ORDER_NOT_FOUND", "Клиентский заказ-получатель не найден.");

            if (sourceOrder.Type != OrderType.Internal)
            {
                throw new ProductionPalletPlanAdoptionException("SOURCE_NOT_INTERNAL", "Источник должен быть внутренним заказом.");
            }

            if (targetOrder.Type != OrderType.Customer)
            {
                throw new ProductionPalletPlanAdoptionException("TARGET_NOT_CUSTOMER", "Получатель должен быть клиентским заказом.");
            }

            if (sourceOrder.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
            {
                throw new ProductionPalletPlanAdoptionException("SOURCE_ORDER_NOT_EDITABLE", "Внутренний заказ недоступен для переноса плана паллет.");
            }

            if (targetOrder.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
            {
                throw new ProductionPalletPlanAdoptionException("TARGET_ORDER_NOT_EDITABLE", "Клиентский заказ недоступен для переноса плана паллет.");
            }

            var sourceDoc = FindProductionReceiptWithPalletPlan(store, sourceInternalOrderId)
                            ?? throw new ProductionPalletPlanAdoptionException("SOURCE_PRD_NOT_FOUND", "План паллет внутреннего заказа не найден.");
            if (sourceDoc.Status == DocStatus.Closed)
            {
                throw new ProductionPalletPlanAdoptionException("SOURCE_PRD_CLOSED", "Нельзя перенести план паллет: выпуск уже закрыт.");
            }

            if (TargetHasActiveProductionPalletPlan(store, targetCustomerOrderId))
            {
                throw new ProductionPalletPlanAdoptionException(
                    "TARGET_ALREADY_HAS_PALLET_PLAN",
                    "У клиентского заказа уже есть план паллет. Сначала удалите текущий план паллет.");
            }

            var sourcePallets = store.GetProductionPalletsByDoc(sourceDoc.Id)
                .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sourcePallets.Count == 0)
            {
                throw new ProductionPalletPlanAdoptionException("SOURCE_HAS_NO_ACTIVE_PALLETS", "У внутреннего выпуска нет активных паллет для переноса.");
            }

            if (sourcePallets.Any(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ProductionPalletPlanAdoptionException("SOURCE_HAS_FILLED_PALLETS", "Нельзя перенести план паллет: есть уже наполненные паллеты.");
            }

            if (sourcePallets.Any(pallet =>
                    !string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(pallet.Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ProductionPalletPlanAdoptionException("INVALID_OPERATION", "Перенести можно только паллеты PLANNED/PRINTED.");
            }

            if (store.CountLedgerEntriesByDocId(sourceDoc.Id) > 0)
            {
                throw new ProductionPalletPlanAdoptionException("SOURCE_HAS_LEDGER", "Нельзя перенести план паллет: по внутреннему выпуску уже есть движения склада.");
            }

            var targetLinesByItemId = store.GetOrderLines(targetCustomerOrderId)
                .GroupBy(line => line.ItemId)
                .ToDictionary(group => group.Key, group => group.OrderBy(line => line.Id).First().Id);
            var sourceItemIds = sourcePallets
                .SelectMany(pallet => GetPalletLines(pallet).Select(line => line.ItemId).DefaultIfEmpty(pallet.ItemId))
                .Distinct()
                .ToList();
            foreach (var itemId in sourceItemIds)
            {
                if (!targetLinesByItemId.ContainsKey(itemId))
                {
                    throw new ProductionPalletPlanAdoptionException("TARGET_LINE_NOT_FOUND", $"В клиентском заказе нет строки для номенклатуры id={itemId}.");
                }
            }

            var targetDoc = FindReusableEmptyProductionReceipt(store, targetCustomerOrderId)
                            ?? CreateProductionReceipt(store, targetOrder);
            var adoptResult = store.AdoptProductionPalletPlan(
                sourceDoc.Id,
                targetDoc.Id,
                sourceInternalOrderId,
                targetCustomerOrderId,
                targetLinesByItemId);
            EmptyDraftProductionReceiptCleanup.TryDeleteEmptyDraftProductionReceiptIfSafe(
                store,
                sourceInternalOrderId,
                sourceDoc.Id);
            var mergeResult = InternalOrderMergeService.TryMarkAsMerged(
                store,
                sourceInternalOrderId,
                targetCustomerOrderId,
                targetOrder.OrderRef);
            var warnings = new List<ProductionPalletPlanAdoptionWarning>();
            if (!string.IsNullOrWhiteSpace(mergeResult.WarningCode))
            {
                warnings.Add(new ProductionPalletPlanAdoptionWarning
                {
                    Code = mergeResult.WarningCode,
                    Message = mergeResult.WarningMessage ?? string.Empty
                });
            }
            else if (mergeResult.IsMerged && !string.IsNullOrWhiteSpace(mergeResult.InfoMessage))
            {
                warnings.Add(new ProductionPalletPlanAdoptionWarning
                {
                    Code = mergeResult.InfoCode ?? InternalOrderMergeService.MergedInfoCode,
                    Message = mergeResult.InfoMessage
                });
            }

            result = new ProductionPalletPlanAdoptionResult
            {
                Success = adoptResult.Success,
                Message = adoptResult.Message,
                SourceOrderId = adoptResult.SourceOrderId,
                TargetOrderId = adoptResult.TargetOrderId,
                SourcePrdDocId = adoptResult.SourcePrdDocId,
                TargetPrdDocId = adoptResult.TargetPrdDocId,
                TransferredPalletCount = adoptResult.TransferredPalletCount,
                TransferredLineCount = adoptResult.TransferredLineCount,
                TransferredHuCodes = adoptResult.TransferredHuCodes,
                Warnings = warnings,
                SourceOrderStatus = OrderStatusMapper.StatusToString(mergeResult.IsMerged ? OrderStatus.Merged : sourceOrder.Status),
                SourceOrderCommentUpdated = mergeResult.CommentUpdated
            };
        });

        return result;
    }

    public ProductionPalletDocument Get(long docId)
    {
        var doc = RequireProductionReceipt(docId);
        var pallets = _data.GetProductionPalletsByDoc(doc.Id);
        return BuildDocument(doc.Id, pallets);
    }

    public IReadOnlyList<ProductionPalletWorkItem> GetActiveWorkItems()
    {
        return _data.GetActiveProductionPalletWorkItems()
            .Where(item => item.Summary.RemainingPalletCount > 0)
            .ToList();
    }

    public IReadOnlyList<ProductionFillingOrder> GetFillingOrders()
    {
        return _data.GetActiveProductionPalletWorkItems()
            .Where(item => item.OrderId.HasValue && item.Summary.RemainingPalletCount > 0)
            .GroupBy(item => item.OrderId!.Value)
            .Select(group => BuildFillingOrder(group.Key, group.ToList()))
            .Where(row => row != null && row.OrderStatus != OrderStatusMapper.StatusToString(OrderStatus.Merged))
            .Cast<ProductionFillingOrder>()
            .OrderBy(row => row.OrderType == OrderStatusMapper.TypeToString(OrderType.Internal) ? 0 : 1)
            .ThenByDescending(row => TryParseLong(row.OrderRef, out var number) ? number : long.MinValue)
            .ThenByDescending(row => row.OrderId)
            .ToList();
    }

    public ProductionFillingContext StartFilling(long orderId)
    {
        return GetFillingContext(orderId);
    }

    public ProductionFillingContext GetFillingContext(long orderId)
    {
        var order = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
        {
            throw new InvalidOperationException(order.Status == OrderStatus.Merged
                ? "Заказ объединён с другим заказом. Выпуск по нему не требуется."
                : "Заказ недоступен для наполнения.");
        }

        var pallets = GetProductionPalletsByOrder(_data, orderId);
        var fillingPallets = BuildFillingPalletViews(_data, orderId, pallets);
        var openDoc = FindPreparedOpenProductionReceiptForFilling(_data, orderId, fillingPallets, requireRemaining: false);
        if (openDoc == null)
        {
            if (HasCompletedPalletizedProduction(fillingPallets))
            {
                throw new InvalidOperationException("Выпуск по заказу уже завершён. Нет паллет к наполнению.");
            }

            throw new InvalidOperationException("Для заказа не сформирован план паллет. Сформируйте и напечатайте паллетные этикетки перед наполненением.");
        }

        return BuildFillingContext(orderId, openDoc.Id, fillingPallets);
    }

    public IReadOnlyList<ProductionPalletPrintRow> GetPrintRows(long orderId)
    {
        var order = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        if (order.Type == OrderType.Customer)
        {
            var rows = new List<ProductionPalletPrintRow>();
            rows.AddRange(GetCustomerBoundHuPrintRows(order));
            rows.AddRange(GetProductionPalletPrintRows(order));

            return rows;
        }

        var productionRows = GetProductionPalletPrintRows(order);
        if (productionRows.Count == 0)
        {
            throw new InvalidOperationException("Сначала сформируйте план паллет");
        }

        return productionRows;
    }

    private IReadOnlyList<ProductionPalletPrintRow> GetCustomerBoundHuPrintRows(Order order)
    {
        var entries = new List<CustomerHuPrintEntry>();

        foreach (var planLine in _data.GetOrderReceiptPlanLines(order.Id)
                     .Where(line => line.QtyPlanned > QtyTolerance && !string.IsNullOrWhiteSpace(NormalizeHu(line.ToHu)))
                     .OrderBy(line => line.SortOrder)
                     .ThenBy(line => line.Id))
        {
            var huCode = NormalizeHu(planLine.ToHu)!;
            entries.Add(new CustomerHuPrintEntry(
                planLine.Id,
                planLine.ItemId,
                planLine.ItemName,
                huCode,
                planLine.QtyPlanned,
                planLine.ToLocationCode));
        }

        if (entries.Count == 0)
        {
            return Array.Empty<ProductionPalletPrintRow>();
        }

        var itemsById = entries
            .Select(entry => entry.ItemId)
            .Distinct()
            .Select(id => _data.FindItemById(id))
            .Where(item => item != null)
            .ToDictionary(item => item!.Id, item => item!);

        var rows = new List<ProductionPalletPrintRow>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            itemsById.TryGetValue(entry.ItemId, out var item);
            var itemName = !string.IsNullOrWhiteSpace(entry.ItemName)
                ? entry.ItemName
                : item?.Name ?? "Товар";
            var uom = string.IsNullOrWhiteSpace(item?.BaseUom) ? "шт" : item!.BaseUom!;
            var printLine = new ProductionPalletPrintLine
            {
                ItemName = itemName,
                Qty = entry.Qty,
                Uom = uom
            };
            rows.Add(new ProductionPalletPrintRow
            {
                SourceType = ProductionPalletPrintSourceType.ReservedHu,
                PalletId = entry.PalletId,
                OrderId = order.Id,
                OrderRef = order.OrderRef,
                ClientName = order.PartnerName ?? string.Empty,
                HuCode = entry.HuCode,
                ItemId = entry.ItemId,
                ItemName = itemName,
                Brand = item?.Brand ?? string.Empty,
                Qty = entry.Qty,
                Uom = uom,
                PalletNo = index + 1,
                PalletCount = entries.Count,
                StoragePlace = entry.LocationCode ?? string.Empty,
                Lines = new[] { printLine },
                Composition = $"{itemName} - {FormatQty(entry.Qty)} {uom}",
                Status = "BOUND"
            });
        }

        return rows;
    }

    private IReadOnlyList<ProductionPalletPrintRow> GetProductionPalletPrintRows(Order order)
    {
        var docsById = _data.GetDocsByOrder(order.Id)
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .ToDictionary(doc => doc.Id);
        var pallets = BuildOrderOwnedPalletViews(_data, order.Id, GetProductionPalletsByOrder(_data, order.Id))
            .Where(pallet => IsPrintableProductionPalletStatus(pallet.Status) && docsById.ContainsKey(pallet.PrdDocId))
            .ToList();
        if (pallets.Count == 0)
        {
            return Array.Empty<ProductionPalletPrintRow>();
        }

        var itemsById = pallets
            .SelectMany(pallet => GetPalletLines(pallet).Select(line => line.ItemId))
            .Distinct()
            .Select(id => _data.FindItemById(id))
            .Where(item => item != null)
            .ToDictionary(item => item!.Id, item => item!);
        var locationsById = _data.GetLocations().ToDictionary(location => location.Id, location => location.Code);
        var rows = new List<ProductionPalletPrintRow>(pallets.Count);
        for (var index = 0; index < pallets.Count; index++)
        {
            var pallet = pallets[index];
            var doc = docsById[pallet.PrdDocId];
            if (string.IsNullOrWhiteSpace(pallet.HuCode))
            {
                throw new InvalidOperationException("Для паллеты не задан HU.");
            }

            var componentLines = GetPalletLines(pallet)
                .OrderBy(line => line.Id)
                .ToList();
            var isMixed = componentLines.Count > 1;
            var firstLine = componentLines.FirstOrDefault();
            if (firstLine == null || !itemsById.TryGetValue(firstLine.ItemId, out var item))
            {
                throw new InvalidOperationException("Номенклатура паллеты не найдена.");
            }

            if (string.IsNullOrWhiteSpace(isMixed ? "Микс-паллета" : item.Name))
            {
                throw new InvalidOperationException("Для паллеты не задана номенклатура.");
            }

            var plannedQty = componentLines.Sum(line => line.PlannedQty);
            if (plannedQty <= QtyTolerance)
            {
                throw new InvalidOperationException("Для паллеты не задано количество.");
            }

            var printLines = componentLines.Select(line =>
            {
                var lineItem = itemsById.TryGetValue(line.ItemId, out var found) ? found : null;
                return new ProductionPalletPrintLine
                {
                    ItemName = lineItem?.Name ?? line.ItemName,
                    Qty = line.PlannedQty,
                    Uom = string.IsNullOrWhiteSpace(lineItem?.BaseUom) ? line.Uom : lineItem!.BaseUom!
                };
            }).ToList();
            var composition = string.Join("\r\n", printLines.Select((line, lineIndex) =>
                $"{lineIndex + 1}. {line.ItemName} - {FormatQty(line.Qty)} {line.Uom}"));

            rows.Add(new ProductionPalletPrintRow
            {
                SourceType = ProductionPalletPrintSourceType.ProductionPallet,
                PalletId = pallet.Id,
                OrderId = order.Id,
                OrderRef = order.OrderRef,
                ClientName = order.PartnerName ?? string.Empty,
                PrdDocId = doc.Id,
                PrdRef = doc.DocRef,
                HuCode = pallet.HuCode,
                ItemId = firstLine.ItemId,
                ItemName = isMixed ? "Микс-паллета" : item.Name,
                Brand = isMixed ? string.Empty : item.Brand ?? string.Empty,
                Qty = plannedQty,
                Uom = isMixed ? string.Empty : string.IsNullOrWhiteSpace(item.BaseUom) ? "шт" : item.BaseUom!,
                PalletNo = index + 1,
                PalletCount = pallets.Count,
                StoragePlace = pallet.ToLocationId.HasValue && locationsById.TryGetValue(pallet.ToLocationId.Value, out var locationCode)
                    ? locationCode
                    : pallet.ToLocationCode ?? string.Empty,
                ProductionDate = doc.CreatedAt.Date,
                Comment = isMixed ? composition : doc.Comment ?? string.Empty,
                IsMixedPallet = isMixed,
                Composition = composition,
                Lines = printLines,
                Status = pallet.Status
            });
        }

        return rows;
    }

    public int MarkPrinted(long orderId, DateTime printedAt)
    {
        return MarkPrinted(orderId, palletIds: null, printedAt);
    }

    public int MarkPrinted(long orderId, IReadOnlyCollection<long>? palletIds, DateTime printedAt)
    {
        var order = _data.GetOrder(orderId);
        if (order?.Type == OrderType.Customer && !HasPrintableProductionPalletPlan(_data, order))
        {
            return 0;
        }

        if (palletIds is { Count: > 0 })
        {
            var rows = GetPrintRows(orderId);
            if (rows.Count == 0)
            {
                throw new InvalidOperationException("Сначала сформируйте план паллет");
            }

            var allowedIds = rows
                .Where(row => string.Equals(row.SourceType, ProductionPalletPrintSourceType.ProductionPallet, StringComparison.OrdinalIgnoreCase))
                .Select(row => row.PalletId)
                .ToHashSet();
            if (palletIds.Any(id => !allowedIds.Contains(id)))
            {
                throw new InvalidOperationException("Выбранные паллеты не найдены в плане заказа.");
            }

            return _data.MarkProductionPalletsPrinted(orderId, palletIds, printedAt);
        }

        var allRows = GetPrintRows(orderId);
        if (allRows.Count == 0)
        {
            throw new InvalidOperationException("Сначала сформируйте план паллет");
        }

        return _data.MarkProductionPalletsPrintedByOrder(orderId, printedAt);
    }

    public ProductionPalletScanResult Scan(long? orderId, long? prdDocId, string? huCode)
    {
        var normalizedHu = NormalizeHu(huCode);
        if (string.IsNullOrWhiteSpace(normalizedHu))
        {
            return ProductionPalletScanResult.Failure("Укажите код паллеты.");
        }

        var pallet = _data.GetProductionPalletByHu(normalizedHu);
        if (pallet == null)
        {
            return ProductionPalletScanResult.Failure("Паллета не найдена в плане выпуска");
        }

        if ((prdDocId.HasValue && pallet.PrdDocId != prdDocId.Value)
            || (orderId.HasValue && pallet.OrderId != orderId.Value))
        {
            return ProductionPalletScanResult.Failure("Эта паллета относится к другому заказу");
        }

        var doc = _data.GetDoc(pallet.PrdDocId);
        if (doc == null || doc.Type != DocType.ProductionReceipt)
        {
            return ProductionPalletScanResult.Failure("Документ выпуска не найден.");
        }

        if (doc.Status == DocStatus.Closed)
        {
            return ProductionPalletScanResult.Failure("Документ выпуска уже закрыт.");
        }

        if (IsCancelledPallet(pallet))
        {
            return ProductionPalletScanResult.Failure("Паллета отменена и не может быть наполнена.");
        }

        if (!HasOnlyValidFillingPalletLines(_data, pallet))
        {
            return ProductionPalletScanResult.Failure("Строка заказа для паллеты не найдена.");
        }

        var palletLines = GetPalletLines(pallet);
        var docLinesById = _data.GetDocLines(doc.Id).ToDictionary(line => line.Id, line => line);
        foreach (var palletLine in palletLines)
        {
            if (!docLinesById.TryGetValue(palletLine.DocLineId, out var docLine)
                || docLine.ItemId != palletLine.ItemId
                || docLine.OrderLineId != palletLine.OrderLineId)
            {
                return ProductionPalletScanResult.Failure("План паллеты не совпадает со строкой выпуска.");
            }

            if (pallet.OrderId.HasValue && palletLine.OrderLineId.HasValue)
            {
                var orderLine = _data.GetOrderLines(pallet.OrderId.Value)
                    .FirstOrDefault(line => line.Id == palletLine.OrderLineId.Value);
                if (orderLine == null || orderLine.ItemId != palletLine.ItemId)
                {
                    return ProductionPalletScanResult.Failure("Строка заказа для паллеты не найдена.");
                }

                if (!string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                {
                    var alreadyFilled = GetFillGuardFilledQty(_data, pallet.OrderId.Value, orderLine.Id, pallet.Id);
                    if (alreadyFilled + palletLine.PlannedQty > orderLine.QtyOrdered + QtyTolerance)
                    {
                        return ProductionPalletScanResult.Failure("Выпуск превышает остаток по строке заказа");
                    }
                }
            }
        }

        var pallets = _data.GetProductionPalletsByDoc(doc.Id);
        var activePallets = BuildFillingPalletViews(_data, pallet.OrderId!.Value, pallets)
            .OrderBy(row => row.Id)
            .ToList();
        var index = activePallets.FindIndex(row => row.Id == pallet.Id);
        var firstLine = palletLines.First();
        var item = _data.FindItemById(firstLine.ItemId);
        var order = pallet.OrderId.HasValue ? _data.GetOrder(pallet.OrderId.Value) : null;
        var scanLines = palletLines.Select(line =>
        {
            var lineItem = _data.FindItemById(line.ItemId);
            return new ProductionPalletScanLine
            {
                ItemId = line.ItemId,
                ItemName = lineItem?.Name ?? line.ItemName,
                Brand = lineItem?.Brand ?? line.Brand,
                Qty = line.PlannedQty,
                Uom = string.IsNullOrWhiteSpace(lineItem?.BaseUom) ? line.Uom : lineItem!.BaseUom!
            };
        }).ToList();

        return new ProductionPalletScanResult
        {
            Success = true,
            AlreadyFilled = string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase),
            OrderId = pallet.OrderId,
            OrderRef = order?.OrderRef ?? doc.OrderRef,
            PrdDocId = doc.Id,
            PrdDocRef = doc.DocRef,
            PalletId = pallet.Id,
            HuCode = pallet.HuCode,
            ItemId = firstLine.ItemId,
            ItemName = palletLines.Count > 1 ? "Микс-паллета" : item?.Name ?? pallet.ItemName,
            ItemBrand = palletLines.Count > 1 ? null : item?.Brand,
            BaseUom = string.IsNullOrWhiteSpace(item?.BaseUom) ? "шт" : item!.BaseUom,
            PlannedQty = palletLines.Sum(line => line.PlannedQty),
            IsMixedPallet = palletLines.Count > 1,
            Lines = scanLines,
            PalletIndex = index >= 0 ? index + 1 : 0,
            PalletCount = activePallets.Count,
            PalletStatus = pallet.Status,
            Document = BuildFillingDocument(doc.Id, pallets, pallet.OrderId)
        };
    }

    public ProductionPalletFillResult Fill(string? huCode, string? deviceId, long? orderId = null, long? prdDocId = null)
    {
        var normalizedHu = NormalizeHu(huCode);
        if (string.IsNullOrWhiteSpace(normalizedHu))
        {
            return ProductionPalletFillResult.Failure("Укажите код паллеты.");
        }

        ProductionPalletFillResult? result = null;
        try
        {
            _data.ExecuteInTransaction(store =>
            {
                var pallet = store.GetProductionPalletByHu(normalizedHu);
                if (pallet == null)
                {
                    result = ProductionPalletFillResult.Failure("Паллета не найдена в плане выпуска.");
                    return;
                }

                if (orderId.HasValue && pallet.OrderId != orderId.Value)
                {
                    result = ProductionPalletFillResult.Failure("Эта паллета относится к другому заказу");
                    return;
                }

                if (prdDocId.HasValue && pallet.PrdDocId != prdDocId.Value)
                {
                    var isFilledForRequestedOrder = orderId.HasValue
                        && pallet.OrderId == orderId.Value
                        && string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase);
                    if (!isFilledForRequestedOrder)
                    {
                        result = ProductionPalletFillResult.Failure("Эта паллета относится к другому заказу");
                        return;
                    }
                }

                var doc = store.GetDoc(pallet.PrdDocId);
                if (doc == null || doc.Type != DocType.ProductionReceipt)
                {
                    result = ProductionPalletFillResult.Failure("Документ выпуска не найден.");
                    return;
                }

                if (doc.Status == DocStatus.Closed)
                {
                    if (string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!HasOnlyValidFillingPalletLines(store, pallet))
                        {
                            result = ProductionPalletFillResult.Failure("Строка заказа для паллеты не найдена.");
                            return;
                        }

                        result = new ProductionPalletFillResult
                        {
                            Success = true,
                            AlreadyFilled = true,
                            PrdAutoClosed = true,
                            ClosedPrdDocId = doc.Id,
                            ClosedPrdDocRef = doc.DocRef,
                            Pallet = pallet,
                            Document = BuildFillingDocument(doc.Id, store.GetProductionPalletsByDoc(doc.Id), pallet.OrderId)
                        };
                        return;
                    }

                    result = ProductionPalletFillResult.Failure("Документ выпуска уже закрыт.");
                    return;
                }

                if (IsCancelledPallet(pallet))
                {
                    result = ProductionPalletFillResult.Failure("Паллета отменена и не может быть наполнена.");
                    return;
                }

                if (!HasOnlyValidFillingPalletLines(store, pallet))
                {
                    result = ProductionPalletFillResult.Failure("Строка заказа для паллеты не найдена.");
                    return;
                }

                if (string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                {
                    result = ApplyAutoCloseAfterFillInTransaction(store, new ProductionPalletFillResult
                    {
                        Success = true,
                        AlreadyFilled = true,
                        Pallet = pallet,
                        Document = BuildFillingDocument(doc.Id, store.GetProductionPalletsByDoc(doc.Id), pallet.OrderId)
                    });
                    return;
                }

                var palletLines = GetPalletLines(pallet);
                var docLinesById = store.GetDocLines(doc.Id).ToDictionary(line => line.Id, line => line);
                foreach (var palletLine in palletLines)
                {
                    if (!docLinesById.TryGetValue(palletLine.DocLineId, out var docLine))
                    {
                        result = ProductionPalletFillResult.Failure("Строка паллеты не найдена в документе выпуска.");
                        return;
                    }

                    if (docLine.ItemId != palletLine.ItemId || docLine.OrderLineId != palletLine.OrderLineId)
                    {
                        result = ProductionPalletFillResult.Failure("План паллеты не совпадает со строкой выпуска.");
                        return;
                    }

                    if (!docLine.ToLocationId.HasValue)
                    {
                        result = ProductionPalletFillResult.Failure("Для паллеты не указано место хранения.");
                        return;
                    }

                    if (pallet.OrderId.HasValue && palletLine.OrderLineId.HasValue)
                    {
                        var orderLine = store.GetOrderLines(pallet.OrderId.Value)
                            .FirstOrDefault(line => line.Id == palletLine.OrderLineId.Value);
                        if (orderLine == null || orderLine.ItemId != palletLine.ItemId)
                        {
                            result = ProductionPalletFillResult.Failure("Строка заказа для паллеты не найдена.");
                            return;
                        }

                        var alreadyFilled = GetFillGuardFilledQty(store, pallet.OrderId.Value, orderLine.Id, pallet.Id);
                        if (alreadyFilled + palletLine.PlannedQty > orderLine.QtyOrdered + QtyTolerance)
                        {
                            result = ProductionPalletFillResult.Failure("Выпуск превышает остаток по строке заказа");
                            return;
                        }
                    }
                }

                var filledAt = DateTime.Now;
                store.MarkProductionPalletFilled(pallet.Id, filledAt, NormalizeDeviceId(deviceId));

                var filledPallet = store.GetProductionPalletByHu(normalizedHu) ?? pallet;
                result = ApplyAutoCloseAfterFillInTransaction(store, new ProductionPalletFillResult
                {
                    Success = true,
                    AlreadyFilled = false,
                    Pallet = filledPallet,
                    Document = BuildFillingDocument(doc.Id, store.GetProductionPalletsByDoc(doc.Id), filledPallet.OrderId)
                });
            });
        }
        catch (ProductionPalletFillRollbackException ex)
        {
            return ProductionPalletFillResult.Failure(ex.Message);
        }

        return result ?? ProductionPalletFillResult.Failure("Не удалось наполнить паллету.");
    }

    private static double GetFillGuardFilledQty(
        IDataStore store,
        long orderId,
        long orderLineId,
        long? excludePalletId)
    {
        var order = store.GetOrder(orderId);
        if (order?.Type != OrderType.Internal)
        {
            return store.GetFilledProductionPalletQtyByOrderLine(orderLineId, excludePalletId);
        }

        var reservedHuByItem = store.GetHuOrderContextRows()
            .Where(row => row.ReservedCustomerOrderId.HasValue && !string.IsNullOrWhiteSpace(row.HuCode))
            .GroupBy(row => row.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => NormalizeHu(row.HuCode))
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Cast<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));

        var qty = 0d;
        foreach (var doc in store.GetDocsByOrder(orderId).Where(doc => doc.Type == DocType.ProductionReceipt))
        {
            var supersededDocLineIds = store.GetDocLines(doc.Id)
                .Where(line => line.ReplacesLineId.HasValue)
                .Select(line => line.ReplacesLineId!.Value)
                .ToHashSet();
            foreach (var pallet in store.GetProductionPalletsByDoc(doc.Id))
            {
                if (excludePalletId.HasValue && pallet.Id == excludePalletId.Value)
                {
                    continue;
                }

                if (supersededDocLineIds.Contains(pallet.DocLineId))
                {
                    continue;
                }

                if (!string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
                    || !PalletAppliesToOrderLine(pallet, orderLineId))
                {
                    continue;
                }

                var normalizedHu = NormalizeHu(pallet.HuCode);
                if (!string.IsNullOrWhiteSpace(normalizedHu)
                    && reservedHuByItem.TryGetValue(pallet.ItemId, out var reservedHu)
                    && reservedHu.Contains(normalizedHu))
                {
                    continue;
                }

                qty += ResolvePalletQtyForOrderLine(pallet, orderLineId);
            }
        }

        return qty;
    }

    private ProductionPalletFillResult ApplyAutoCloseAfterFillInTransaction(
        IDataStore store,
        ProductionPalletFillResult fillResult)
    {
        if (_fillClose == null || fillResult.Pallet == null)
        {
            return fillResult;
        }

        var autoClose = _fillClose.TryAutoCloseAfterFillInTransaction(store, fillResult.Pallet);
        if (!autoClose.Attempted)
        {
            return fillResult;
        }

        if (!autoClose.Success)
        {
            throw new ProductionPalletFillRollbackException(
                autoClose.Error ?? "Не удалось провести выпуск после наполнения.");
        }

        var pallet = store.GetProductionPalletByHu(fillResult.Pallet.HuCode) ?? fillResult.Pallet;
        var prdDocId = autoClose.ClosedPrdDocId ?? pallet.PrdDocId;
        return new ProductionPalletFillResult
        {
            Success = true,
            AlreadyFilled = fillResult.AlreadyFilled || autoClose.AlreadyClosed,
            Pallet = pallet,
            Document = BuildFillingDocument(prdDocId, store.GetProductionPalletsByDoc(prdDocId), pallet.OrderId),
            PrdAutoClosed = true,
            ClosedPrdDocId = autoClose.ClosedPrdDocId,
            ClosedPrdDocRef = autoClose.ClosedPrdDocRef
        };

    }

    private sealed class ProductionPalletFillRollbackException : Exception
    {
        public ProductionPalletFillRollbackException(string message)
            : base(message)
        {
        }
    }

    private Doc RequireProductionReceipt(long docId)
    {
        var doc = _data.GetDoc(docId) ?? throw new InvalidOperationException("Документ не найден.");
        if (doc.Type != DocType.ProductionReceipt)
        {
            throw new InvalidOperationException("Документ не является выпуском продукции.");
        }

        return doc;
    }

    private ProductionFillingOrder? BuildFillingOrder(long orderId, IReadOnlyList<ProductionPalletWorkItem> workItems)
    {
        var order = _data.GetOrder(orderId);
        if (order == null || order.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            return null;
        }

        var fillingPallets = BuildFillingPalletViews(_data, orderId, GetProductionPalletsByOrder(_data, orderId));
        var activeItems = fillingPallets
            .GroupBy(pallet => pallet.PrdDocId)
            .Select(group =>
            {
                var doc = _data.GetDoc(group.Key);
                var workItem = workItems.FirstOrDefault(item => item.PrdDocId == group.Key);
                return new ProductionPalletWorkItem
                {
                    PrdDocId = group.Key,
                    PrdDocRef = doc?.DocRef ?? workItem?.PrdDocRef ?? string.Empty,
                    PrdStatus = doc == null ? workItem?.PrdStatus ?? string.Empty : DocTypeMapper.StatusToString(doc.Status),
                    OrderId = order.Id,
                    OrderRef = order.OrderRef,
                    Summary = BuildSummary(group.ToList())
                };
            })
            .Where(item => item.Summary.RemainingPalletCount > 0 || item.Summary.RemainingQty > QtyTolerance)
            .ToList();
        if (activeItems.Count == 0)
        {
            return null;
        }

        var summary = CombineSummary(activeItems.Select(item => item.Summary));
        if (summary.RemainingQty <= QtyTolerance && summary.RemainingPalletCount <= 0)
        {
            return null;
        }

        var primaryWorkItem = activeItems.First();
        return new ProductionFillingOrder
        {
            OrderId = order.Id,
            OrderRef = order.OrderRef,
            OrderType = OrderStatusMapper.TypeToString(order.Type),
            OrderTypeDisplay = OrderStatusMapper.TypeToDisplayName(order.Type),
            OrderStatus = OrderStatusMapper.StatusToString(order.Status),
            OrderStatusDisplay = OrderStatusMapper.StatusToDisplayName(order.Status, order.Type),
            PartnerName = order.PartnerDisplay,
            PrdDocId = primaryWorkItem.PrdDocId,
            PrdDocRef = primaryWorkItem.PrdDocRef,
            Summary = summary
        };
    }

    private ProductionFillingContext BuildFillingContext(
        long orderId,
        long prdDocId,
        IReadOnlyList<ProductionPallet> pallets)
    {
        var order = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        var doc = _data.GetDoc(prdDocId) ?? throw new InvalidOperationException("Документ выпуска не найден.");
        return new ProductionFillingContext
        {
            OrderId = order.Id,
            OrderRef = order.OrderRef,
            OrderType = OrderStatusMapper.TypeToString(order.Type),
            OrderTypeDisplay = OrderStatusMapper.TypeToDisplayName(order.Type),
            OrderStatus = OrderStatusMapper.StatusToString(order.Status),
            OrderStatusDisplay = OrderStatusMapper.StatusToDisplayName(order.Status, order.Type),
            PartnerName = order.PartnerDisplay,
            PrdDocId = doc.Id,
            PrdDocRef = doc.DocRef,
            Document = BuildFillingDocument(doc.Id, pallets)
        };
    }

    private ProductionPalletOrderPlanResult BuildOrderPlanResult(long orderId, long prdDocId, bool wasExisting)
    {
        var order = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        var doc = _data.GetDoc(prdDocId) ?? throw new InvalidOperationException("Документ выпуска не найден.");
        var document = Get(doc.Id);
        return new ProductionPalletOrderPlanResult
        {
            OrderId = order.Id,
            OrderRef = order.OrderRef,
            PrdDocId = doc.Id,
            PrdDocRef = doc.DocRef,
            WasExisting = wasExisting,
            ProductionRequired = true,
            Message = wasExisting ? "План паллет уже сформирован" : "План паллет сформирован",
            Summary = document.Summary,
            Document = document
        };
    }

    private ProductionPalletOrderPlanResult BuildNoProductionRequiredResult(long orderId)
    {
        var order = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        return new ProductionPalletOrderPlanResult
        {
            OrderId = order.Id,
            OrderRef = order.OrderRef,
            PrdDocId = 0,
            PrdDocRef = string.Empty,
            WasExisting = false,
            ProductionRequired = false,
            Message = "Заказ покрыт складскими остатками, производство не требуется.",
            Summary = new ProductionPalletSummary(),
            Document = new ProductionPalletDocument
            {
                Summary = new ProductionPalletSummary()
            }
        };
    }

    private static Doc? FindProductionReceiptWithPalletPlan(IDataStore store, long orderId)
    {
        Doc? closedWithPlan = null;
        foreach (var doc in store.GetDocsByOrder(orderId)
                     .Where(doc => doc.Type == DocType.ProductionReceipt)
                     .OrderByDescending(doc => doc.Id))
        {
            if (!store.HasProductionPallets(doc.Id))
            {
                continue;
            }

            if (doc.Status != DocStatus.Closed)
            {
                return doc;
            }

            closedWithPlan ??= doc;
        }

        return closedWithPlan;
    }

    private static bool HasCompletedPalletizedProduction(IReadOnlyList<ProductionPallet> pallets)
    {
        var activePallets = pallets
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return activePallets.Length > 0
               && activePallets.All(pallet =>
                   string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase));
    }

    private static Doc? FindPreparedOpenProductionReceipt(IDataStore store, long orderId, bool requireRemaining)
    {
        foreach (var doc in store.GetDocsByOrder(orderId)
                     .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status != DocStatus.Closed)
                     .OrderByDescending(doc => doc.Id))
        {
            var summary = BuildSummary(store.GetProductionPalletsByDoc(doc.Id));
            if (summary.PlannedPalletCount <= 0)
            {
                continue;
            }

            if (requireRemaining && summary.RemainingPalletCount <= 0 && summary.RemainingQty <= QtyTolerance)
            {
                continue;
            }

            return doc;
        }

        return null;
    }

    private static Doc? FindPreparedOpenProductionReceiptForFilling(
        IDataStore store,
        long orderId,
        IReadOnlyList<ProductionPallet> fillingPallets,
        bool requireRemaining)
    {
        foreach (var doc in store.GetDocsByOrder(orderId)
                     .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status != DocStatus.Closed)
                     .OrderByDescending(doc => doc.Id))
        {
            var summary = BuildSummary(fillingPallets.Where(pallet => pallet.PrdDocId == doc.Id).ToList());
            if (summary.PlannedPalletCount <= 0)
            {
                continue;
            }

            if (requireRemaining && summary.RemainingPalletCount <= 0 && summary.RemainingQty <= QtyTolerance)
            {
                continue;
            }

            return doc;
        }

        return null;
    }

    private static IReadOnlyList<ProductionPallet> GetProductionPalletsByOrder(IDataStore store, long orderId)
    {
        return store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .OrderBy(doc => doc.Id)
            .SelectMany(doc => store.GetProductionPalletsByDoc(doc.Id))
            .Where(pallet => pallet.OrderId == orderId)
            .OrderBy(pallet => pallet.Id)
            .ToList();
    }

    public static ProductionPalletSummary BuildOrderOwnedPalletSummary(IDataStore store, long orderId)
    {
        return BuildSummary(BuildOrderOwnedPalletViews(store, orderId, GetProductionPalletsByOrder(store, orderId)));
    }

    private static bool HasPrintableProductionPalletPlan(IDataStore store, Order order)
    {
        return BuildOrderOwnedPalletViews(store, order.Id, GetProductionPalletsByOrder(store, order.Id))
            .Any(pallet => IsPrintableProductionPalletStatus(pallet.Status));
    }

    private static bool IsPrintableProductionPalletStatus(string status)
    {
        return string.Equals(status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase);
    }

    private static Doc? FindReusableEmptyProductionReceipt(IDataStore store, long orderId)
    {
        return store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status != DocStatus.Closed)
            .OrderByDescending(doc => doc.Id)
            .FirstOrDefault(doc => !store.GetDocLines(doc.Id).Any() && !store.HasProductionPallets(doc.Id));
    }

    private static bool TargetHasActiveProductionPalletPlan(IDataStore store, long orderId)
    {
        return store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .Any(doc => store.HasProductionPallets(doc.Id));
    }

    private static Doc CreateProductionReceipt(IDataStore store, Order order)
    {
        var docRef = DocRefGenerator.Generate(store, DocType.ProductionReceipt, DateTime.Now);
        var docId = store.AddDoc(new Doc
        {
            DocRef = docRef,
            Type = DocType.ProductionReceipt,
            Status = DocStatus.Draft,
            CreatedAt = DateTime.Now,
            OrderId = order.Id,
            OrderRef = order.OrderRef
        });

        return store.GetDoc(docId) ?? throw new InvalidOperationException("Документ выпуска не найден.");
    }

    private static Location ResolveProductionPalletPlanLocation(IDataStore store)
    {
        var locations = store.GetLocations()
            .OrderBy(location => location.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (locations.Count == 0)
        {
            throw new InvalidOperationException("Нет доступных локаций для плана паллет.");
        }

        return locations.FirstOrDefault(location => location.AutoHuDistributionEnabled) ?? locations[0];
    }

    private static void AddPlannedPalletLines(
        IDataStore store,
        long prdDocId,
        OrderReceiptLine line,
        double palletQty,
        long toLocationId)
    {
        var remainingQty = line.QtyRemaining;
        while (remainingQty > QtyTolerance)
        {
            var chunkQty = Math.Min(palletQty, remainingQty);
            if (chunkQty <= QtyTolerance)
            {
                break;
            }

            store.AddDocLine(new DocLine
            {
                DocId = prdDocId,
                OrderLineId = line.OrderLineId,
                ProductionPurpose = line.ProductionPurpose,
                ItemId = line.ItemId,
                Qty = chunkQty,
                QtyInput = null,
                UomCode = null,
                FromLocationId = null,
                ToLocationId = toLocationId,
                FromHu = null,
                ToHu = store.CreateProductionPalletHuCode(PlanHuCreatedBy),
                PackSingleHu = true
            });

            remainingQty -= chunkQty;
        }
    }

    private static HashSet<long> GetManualMixedOrderLineIds(
        IReadOnlyList<OrderReceiptLine> remainingLines,
        IReadOnlyDictionary<long, OrderLine> orderLinesById)
    {
        var manualMixedLineIds = new HashSet<long>();
        foreach (var group in remainingLines
                     .Where(line => orderLinesById.TryGetValue(line.OrderLineId, out var orderLine)
                                    && !string.IsNullOrWhiteSpace(orderLine.ProductionPalletGroup))
                     .GroupBy(line => orderLinesById[line.OrderLineId].ProductionPalletGroup!.Trim().ToUpperInvariant())
                     .Where(group => group.Count() > 1))
        {
            foreach (var line in group)
            {
                manualMixedLineIds.Add(line.OrderLineId);
            }
        }

        return manualMixedLineIds;
    }

    internal static IReadOnlyList<OrderReceiptLine> GetLinesNeedingPalletAppend(
        IDataStore store,
        Order order)
    {
        var orderLinesById = store.GetOrderLines(order.Id)
            .Where(line => line.QtyOrdered > QtyTolerance)
            .ToDictionary(line => line.Id, line => line);
        if (orderLinesById.Count == 0)
        {
            return Array.Empty<OrderReceiptLine>();
        }

        if (order.Type == OrderType.Customer)
        {
            var receiptLinesById = OrderReceiptRemainingCalculator.GetRemaining(store, order)
                .ToDictionary(line => line.OrderLineId, line => line);
            var protectedByLine = CustomerProtectedCoverageCalculator.BuildByOrderLine(
                store,
                order.Id,
                includeUnconfirmedFilledPallets: true);
            var activePallets = GetProductionPalletsByOrder(store, order.Id)
                .Where(pallet => IsOpenProductionPalletCoverage(store, pallet))
                .ToArray();

            return orderLinesById.Values
                .Select(orderLine =>
                {
                    var protectedQty = protectedByLine.TryGetValue(orderLine.Id, out var coverage)
                        ? coverage.ResolveProtectedQty(orderLine.QtyOrdered)
                        : 0d;
                    var activePalletQty = SumPalletQtyForOrderLine(activePallets, orderLine.Id);
                    var missingQty = Math.Max(0, orderLine.QtyOrdered - protectedQty - activePalletQty);
                    receiptLinesById.TryGetValue(orderLine.Id, out var receiptLine);
                    return new OrderReceiptLine
                    {
                        OrderLineId = orderLine.Id,
                        OrderId = order.Id,
                        ItemId = orderLine.ItemId,
                        ItemName = receiptLine?.ItemName ?? string.Empty,
                        QtyOrdered = orderLine.QtyOrdered,
                        QtyReceived = Math.Max(0, orderLine.QtyOrdered - missingQty),
                        QtyRemaining = missingQty,
                        ProductionPurpose = orderLine.ProductionPurpose,
                        ToLocationId = receiptLine?.ToLocationId,
                        ToLocation = receiptLine?.ToLocation,
                        ToHu = receiptLine?.ToHu,
                        SortOrder = receiptLine?.SortOrder ?? 0
                    };
                })
                .Where(line => line.QtyRemaining > QtyTolerance)
                .OrderBy(line => line.OrderLineId)
                .ToList();
        }

        var activePalletsByOrder = GetProductionPalletsByOrder(store, order.Id)
            .Where(pallet => IsOpenProductionPalletCoverage(store, pallet))
            .ToArray();
        var receiptLinesByOrderLineId = OrderReceiptRemainingCalculator.GetRemaining(store, order)
            .ToDictionary(line => line.OrderLineId, line => line);
        var confirmedByLine = BuildInternalPlanningCoverage(store, order.Id, orderLinesById.Values.ToArray());
        return orderLinesById.Values
            .Select(orderLine =>
            {
                var confirmedQty = confirmedByLine.TryGetValue(orderLine.Id, out var confirmed) ? confirmed : 0d;
                var coveredQty = confirmedQty + SumPalletQtyForOrderLine(activePalletsByOrder, orderLine.Id);
                var missingQty = Math.Max(0, orderLine.QtyOrdered - coveredQty);
                receiptLinesByOrderLineId.TryGetValue(orderLine.Id, out var receiptLine);
                return new OrderReceiptLine
                {
                    OrderLineId = orderLine.Id,
                    OrderId = order.Id,
                    ItemId = orderLine.ItemId,
                    ItemName = receiptLine?.ItemName ?? string.Empty,
                    QtyOrdered = orderLine.QtyOrdered,
                    QtyReceived = receiptLine?.QtyReceived ?? 0,
                    QtyRemaining = missingQty,
                    ProductionPurpose = orderLine.ProductionPurpose,
                    ToLocationId = receiptLine?.ToLocationId,
                    ToLocation = receiptLine?.ToLocation,
                    ToHu = receiptLine?.ToHu,
                    SortOrder = receiptLine?.SortOrder ?? 0
                };
            })
            .Where(line => line.QtyRemaining > QtyTolerance)
            .OrderBy(line => line.OrderLineId)
            .ToList();
    }

    private static bool IsOpenProductionPalletCoverage(IDataStore store, ProductionPallet pallet)
    {
        var doc = store.GetDoc(pallet.PrdDocId);
        return doc?.Status == DocStatus.Draft
               && (string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(pallet.Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase));
    }

    private static double SumPalletQtyForOrderLine(
        IEnumerable<ProductionPallet> pallets,
        long orderLineId)
    {
        return pallets
            .Where(pallet => PalletAppliesToOrderLine(pallet, orderLineId))
            .Sum(pallet => ResolvePalletQtyForOrderLine(pallet, orderLineId));
    }

    private static IReadOnlyList<long> TrimSurplusOpenPallets(
        IDataStore store,
        Order order,
        long orderId,
        long orderLineId,
        double orderedQty)
    {
        var committedQty = GetProtectedCoverageQtyForOrderLine(store, order, orderLineId, orderedQty);
        var plannedAllowedQty = Math.Max(0, orderedQty - committedQty);
        var openPallets = GetOpenProductionPalletsForOrderLine(store, orderId, orderLineId);
        var openQty = openPallets.Sum(pallet => ResolvePalletQtyForOrderLine(pallet, orderLineId));
        if (openQty <= plannedAllowedQty + QtyTolerance)
        {
            return Array.Empty<long>();
        }

        var surplusQty = openQty - plannedAllowedQty;
        var palletIdsToCancel = new List<long>();
        foreach (var pallet in openPallets.OrderByDescending(pallet => pallet.Id))
        {
            if (surplusQty <= QtyTolerance)
            {
                break;
            }

            var palletQty = ResolvePalletQtyForOrderLine(pallet, orderLineId);
            if (palletQty <= QtyTolerance)
            {
                continue;
            }

            palletIdsToCancel.Add(pallet.Id);
            surplusQty -= palletQty;
        }

        if (palletIdsToCancel.Count == 0)
        {
            return Array.Empty<long>();
        }

        var affectedOrderLineIds = openPallets
            .Where(pallet => palletIdsToCancel.Contains(pallet.Id))
            .SelectMany(GetPalletOrderLineIds)
            .Append(orderLineId)
            .Distinct()
            .ToArray();
        TombstoneProductionPalletDocLines(store, openPallets.Where(pallet => palletIdsToCancel.Contains(pallet.Id)));
        store.CancelProductionPallets(palletIdsToCancel);
        return affectedOrderLineIds;
    }

    private static double GetProtectedCoverageQtyForOrderLine(
        IDataStore store,
        Order order,
        long orderLineId,
        double qtyOrdered)
    {
        if (order.Type == OrderType.Customer)
        {
            var coverage = CustomerProtectedCoverageCalculator.BuildByOrderLine(
                    store,
                    order.Id,
                    includeUnconfirmedFilledPallets: true)
                .GetValueOrDefault(orderLineId);
            return coverage?.ResolveProtectedQty(qtyOrdered) ?? 0d;
        }

        var confirmed = BuildInternalPlanningCoverage(store, order.Id, store.GetOrderLines(order.Id));
        return confirmed.TryGetValue(orderLineId, out var qty) ? Math.Max(0, qty) : 0d;
    }

    private static IReadOnlyDictionary<long, double> BuildInternalPlanningCoverage(
        IDataStore store,
        long orderId,
        IReadOnlyList<OrderLine> orderLines)
    {
        var confirmed = OrderReceiptRemainingCalculator.BuildConfirmedReceiptLedgerTotalsByOrderLine(store, orderId, orderLines);
        return orderLines.ToDictionary(
            line => line.Id,
            line => Math.Max(
                confirmed.TryGetValue(line.Id, out var confirmedQty) ? confirmedQty : 0d,
                Math.Max(0, store.GetFilledProductionPalletQtyByOrderLine(line.Id))));
    }

    private static IReadOnlyList<ProductionPallet> GetOpenProductionPalletsForOrderLine(
        IDataStore store,
        long orderId,
        long orderLineId)
    {
        return store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status == DocStatus.Draft)
            .SelectMany(doc => store.GetProductionPalletsByDoc(doc.Id))
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
            .Where(pallet =>
                string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pallet.Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase))
            .Where(pallet => PalletAppliesToOrderLine(pallet, orderLineId))
            .OrderBy(pallet => pallet.Id)
            .ToArray();
    }

    private static IEnumerable<long> GetPalletOrderLineIds(ProductionPallet pallet)
    {
        if (pallet.Lines.Count > 0)
        {
            return pallet.Lines.Where(line => line.OrderLineId.HasValue).Select(line => line.OrderLineId!.Value);
        }

        return pallet.OrderLineId.HasValue ? [pallet.OrderLineId.Value] : Array.Empty<long>();
    }

    private static void TombstoneProductionPalletDocLines(
        IDataStore store,
        IEnumerable<ProductionPallet> pallets)
    {
        foreach (var pallet in pallets)
        {
            var docLineIds = pallet.Lines.Count > 0
                ? pallet.Lines.Select(line => line.DocLineId)
                : pallet.DocLineId > 0 ? [pallet.DocLineId] : Array.Empty<long>();
            foreach (var docLineId in docLineIds.Distinct())
            {
                var activeLine = store.GetDocLines(pallet.PrdDocId).FirstOrDefault(line => line.Id == docLineId);
                if (activeLine == null)
                {
                    continue;
                }

                store.AddDocLine(new DocLine
                {
                    DocId = pallet.PrdDocId,
                    ReplacesLineId = activeLine.Id,
                    OrderLineId = activeLine.OrderLineId,
                    ProductionPurpose = activeLine.ProductionPurpose,
                    ItemId = activeLine.ItemId,
                    Qty = 0,
                    UomCode = activeLine.UomCode,
                    FromLocationId = activeLine.FromLocationId,
                    ToLocationId = activeLine.ToLocationId,
                    FromHu = activeLine.FromHu,
                    ToHu = activeLine.ToHu,
                    PackSingleHu = activeLine.PackSingleHu
                });
            }
        }
    }

    private static double ResolvePalletQtyForOrderLine(ProductionPallet pallet, long orderLineId)
    {
        if (pallet.Lines.Count > 0)
        {
            return pallet.Lines
                .Where(line => line.OrderLineId == orderLineId)
                .Sum(line => Math.Max(0, line.PlannedQty));
        }

        return pallet.OrderLineId == orderLineId
            ? Math.Max(0, pallet.PlannedQty)
            : 0;
    }

    private static bool PalletAppliesToOrderLine(ProductionPallet pallet, long orderLineId)
    {
        if (pallet.Lines.Count > 0)
        {
            return pallet.Lines.Any(line => line.OrderLineId == orderLineId);
        }

        return pallet.OrderLineId == orderLineId;
    }

    private static void AddMixedPlannedPalletLines(
        IDataStore store,
        long prdDocId,
        IReadOnlyList<OrderReceiptLine> lines,
        long toLocationId)
    {
        var huCode = store.CreateProductionPalletHuCode(PlanHuCreatedBy);
        foreach (var line in lines)
        {
            store.AddDocLine(new DocLine
            {
                DocId = prdDocId,
                OrderLineId = line.OrderLineId,
                ProductionPurpose = line.ProductionPurpose,
                ItemId = line.ItemId,
                Qty = line.QtyRemaining,
                QtyInput = null,
                UomCode = null,
                FromLocationId = null,
                ToLocationId = toLocationId,
                FromHu = null,
                ToHu = huCode,
                PackSingleHu = true
            });
        }
    }

    private ProductionPalletDocument BuildFillingDocument(
        long docId,
        IReadOnlyList<ProductionPallet> pallets,
        long? orderId = null)
    {
        var effectiveOrderId = orderId ?? _data.GetDoc(docId)?.OrderId;
        return effectiveOrderId.HasValue
            ? BuildDocument(docId, BuildFillingPalletViews(_data, effectiveOrderId.Value, pallets))
            : BuildDocument(docId, ExcludeCancelledPallets(pallets));
    }

    private ProductionPalletDocument BuildDocument(long docId, IReadOnlyList<ProductionPallet> pallets)
    {
        var activePallets = ExcludeCancelledPallets(pallets);
        var summary = BuildSummary(activePallets);
        var palletLineRows = activePallets
            .SelectMany(pallet => GetPalletLines(pallet).Select(line => new { Pallet = pallet, Line = line }))
            .ToList();
        var orderLineIds = palletLineRows
            .Where(row => row.Pallet.OrderId.HasValue && row.Line.OrderLineId.HasValue)
            .Select(row => (OrderId: row.Pallet.OrderId!.Value, OrderLineId: row.Line.OrderLineId!.Value))
            .Distinct()
            .ToList();
        var orderLinesById = new Dictionary<long, OrderLine>();
        foreach (var group in orderLineIds.GroupBy(row => row.OrderId))
        {
            foreach (var line in _data.GetOrderLines(group.Key))
            {
                orderLinesById[line.Id] = line;
            }
        }

        var lines = palletLineRows
            .GroupBy(row => new { row.Line.OrderLineId, row.Line.ItemId, row.Line.ItemName })
            .Select(group =>
            {
                var orderedQty = group.Key.OrderLineId.HasValue
                                  && orderLinesById.TryGetValue(group.Key.OrderLineId.Value, out var orderLine)
                    ? orderLine.QtyOrdered
                    : group.Sum(row => row.Line.PlannedQty);
                var groupRows = group.ToList();
                var plannedPalletCount = groupRows.Select(row => row.Pallet.Id).Distinct().Count();
                var filledRows = groupRows
                    .Where(row => string.Equals(row.Pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var pendingRows = groupRows
                    .Where(row => IsPendingFillPallet(row.Pallet))
                    .ToList();
                var filledPalletCount = filledRows.Select(row => row.Pallet.Id).Distinct().Count();
                var plannedQty = groupRows.Sum(row => row.Line.PlannedQty);
                var filledQty = filledRows.Sum(row => row.Line.PlannedQty);
                var pendingPalletCount = pendingRows.Select(row => row.Pallet.Id).Distinct().Count();
                return new ProductionPalletLineSummary
                {
                    OrderLineId = group.Key.OrderLineId,
                    ItemId = group.Key.ItemId,
                    ItemName = group.Key.ItemName,
                    OrderedQty = orderedQty,
                    PlannedPalletCount = plannedPalletCount,
                    PlannedQty = plannedQty,
                    FilledPalletCount = filledPalletCount,
                    FilledQty = filledQty,
                    RemainingPalletCount = pendingPalletCount,
                    RemainingQty = Math.Max(0, orderedQty - filledQty)
                };
            })
            .OrderBy(line => line.OrderLineId ?? long.MaxValue)
            .ThenBy(line => line.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProductionPalletDocument
        {
            PrdDocId = docId,
            Summary = summary,
            Lines = lines,
            Pallets = activePallets
        };
    }

    public static IReadOnlyList<ProductionPallet> BuildOrderOwnedPalletViews(
        IDataStore store,
        long orderId,
        IReadOnlyList<ProductionPallet> pallets)
    {
        var orderLinesById = store.GetOrderLines(orderId)
            .ToDictionary(line => line.Id, line => line);
        if (orderLinesById.Count == 0)
        {
            return Array.Empty<ProductionPallet>();
        }

        var result = new List<ProductionPallet>();
        foreach (var pallet in ExcludeCancelledPallets(pallets).Where(pallet => pallet.OrderId == orderId))
        {
            if (TryBuildFillingPalletView(orderId, pallet, orderLinesById, out var view))
            {
                result.Add(view);
            }
        }

        return result
            .OrderBy(pallet => pallet.Id)
            .ToList();
    }

    private static IReadOnlyList<ProductionPallet> BuildFillingPalletViews(
        IDataStore store,
        long orderId,
        IReadOnlyList<ProductionPallet> pallets)
    {
        return BuildOrderOwnedPalletViews(store, orderId, pallets);
    }

    private static bool TryBuildFillingPalletView(
        long orderId,
        ProductionPallet pallet,
        IReadOnlyDictionary<long, OrderLine> orderLinesById,
        out ProductionPallet view)
    {
        view = null!;
        if (pallet.OrderId != orderId)
        {
            return false;
        }

        var sourceLines = GetPalletLines(pallet);
        var validLines = sourceLines
            .Where(line => IsValidFillingPalletLine(line, orderLinesById))
            .ToArray();
        if (validLines.Length == 0)
        {
            return false;
        }

        var firstLine = validLines[0];
        var commonOrderLineId = validLines
            .Select(line => line.OrderLineId)
            .Distinct()
            .Count() == 1
                ? firstLine.OrderLineId
                : null;
        var plannedQty = validLines.Sum(line => line.PlannedQty);
        var exposedLines = pallet.Lines.Count > 0
            ? validLines
            : Array.Empty<ProductionPalletComponentLine>();

        view = new ProductionPallet
        {
            Id = pallet.Id,
            PrdDocId = pallet.PrdDocId,
            DocLineId = validLines.Length == 1 ? firstLine.DocLineId : pallet.DocLineId,
            OrderId = pallet.OrderId,
            OrderLineId = commonOrderLineId,
            ItemId = validLines.Length == 1 ? firstLine.ItemId : pallet.ItemId,
            ItemName = validLines.Length == 1 ? firstLine.ItemName : pallet.ItemName,
            HuCode = pallet.HuCode,
            PlannedQty = plannedQty,
            ToLocationId = pallet.ToLocationId,
            ToLocationCode = pallet.ToLocationCode,
            Status = pallet.Status,
            PalletNo = pallet.PalletNo,
            PalletCount = pallet.PalletCount,
            PrintedAt = pallet.PrintedAt,
            FilledAt = pallet.FilledAt,
            FilledByDeviceId = pallet.FilledByDeviceId,
            CancelReason = pallet.CancelReason,
            CancelledAt = pallet.CancelledAt,
            CreatedAt = pallet.CreatedAt,
            Lines = exposedLines
        };
        return true;
    }

    private static bool IsValidFillingPalletLine(
        ProductionPalletComponentLine line,
        IReadOnlyDictionary<long, OrderLine> orderLinesById)
    {
        return line.OrderLineId.HasValue
               && orderLinesById.TryGetValue(line.OrderLineId.Value, out var orderLine)
               && orderLine.ItemId == line.ItemId;
    }

    private static bool HasOnlyValidFillingPalletLines(IDataStore store, ProductionPallet pallet)
    {
        if (!pallet.OrderId.HasValue)
        {
            return false;
        }

        var orderLinesById = store.GetOrderLines(pallet.OrderId.Value)
            .ToDictionary(line => line.Id, line => line);
        if (orderLinesById.Count == 0)
        {
            return false;
        }

        var sourceLines = GetPalletLines(pallet);
        return sourceLines.Count > 0
               && sourceLines.All(line => IsValidFillingPalletLine(line, orderLinesById));
    }

    public static ProductionPalletSummary BuildSummary(IReadOnlyList<ProductionPallet> pallets)
    {
        var active = ExcludeCancelledPallets(pallets);
        var filled = active
            .Where(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var pending = active
            .Where(IsPendingFillPallet)
            .ToList();
        return new ProductionPalletSummary
        {
            PlannedPalletCount = active.Count,
            PlannedQty = active.Sum(pallet => pallet.PlannedQty),
            FilledPalletCount = filled.Count,
            FilledQty = filled.Sum(pallet => pallet.PlannedQty),
            RemainingPalletCount = pending.Count,
            RemainingQty = pending.Sum(pallet => pallet.PlannedQty)
        };
    }

    private static bool IsCancelledPallet(ProductionPallet pallet)
    {
        return string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPendingFillPallet(ProductionPallet pallet)
    {
        return string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)
               || string.Equals(pallet.Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasOrderLineOwnership(ProductionPallet pallet)
    {
        if (pallet.Lines.Count > 0)
        {
            return pallet.Lines.Any(line => line.OrderLineId.HasValue && line.OrderLineId.Value > 0);
        }

        return pallet.OrderLineId.HasValue && pallet.OrderLineId.Value > 0;
    }

    private static IReadOnlyList<ProductionPallet> ExcludeCancelledPallets(IReadOnlyList<ProductionPallet> pallets)
    {
        return pallets
            .Where(pallet => !IsCancelledPallet(pallet))
            .ToList();
    }

    private static ProductionPalletSummary CombineSummary(IEnumerable<ProductionPalletSummary> summaries)
    {
        var plannedPalletCount = 0;
        var plannedQty = 0d;
        var filledPalletCount = 0;
        var filledQty = 0d;
        var remainingPalletCount = 0;
        var remainingQty = 0d;
        foreach (var summary in summaries)
        {
            plannedPalletCount += summary.PlannedPalletCount;
            plannedQty += summary.PlannedQty;
            filledPalletCount += summary.FilledPalletCount;
            filledQty += summary.FilledQty;
            remainingPalletCount += summary.RemainingPalletCount;
            remainingQty += summary.RemainingQty;
        }

        return new ProductionPalletSummary
        {
            PlannedPalletCount = plannedPalletCount,
            PlannedQty = plannedQty,
            FilledPalletCount = filledPalletCount,
            FilledQty = filledQty,
            RemainingPalletCount = remainingPalletCount,
            RemainingQty = remainingQty
        };
    }

    private static bool TryParseLong(string? value, out long result)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out result);
    }

    private static string? NormalizeHu(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeDeviceId(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static IReadOnlyList<ProductionPalletComponentLine> GetPalletLines(ProductionPallet pallet)
    {
        if (pallet.Lines.Count > 0)
        {
            return pallet.Lines;
        }

        return new[]
        {
            new ProductionPalletComponentLine
            {
                ProductionPalletId = pallet.Id,
                DocLineId = pallet.DocLineId,
                OrderLineId = pallet.OrderLineId,
                ItemId = pallet.ItemId,
                ItemName = pallet.ItemName,
                PlannedQty = pallet.PlannedQty,
                FilledQty = string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
                    ? pallet.PlannedQty
                    : 0,
                CreatedAt = pallet.CreatedAt
            }
        };
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record CustomerHuPrintEntry(
        long PalletId,
        long ItemId,
        string ItemName,
        string HuCode,
        double Qty,
        string? LocationCode);
}

public sealed class ProductionPalletPlanAdoptionException : InvalidOperationException
{
    public ProductionPalletPlanAdoptionException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}


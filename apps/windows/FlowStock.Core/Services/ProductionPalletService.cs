using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Models.Marking;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FlowStock.Core.Services;

public sealed class ProductionPalletService
{
    private const double QtyTolerance = 0.000001d;
    private const string PlanHuCreatedBy = "PRODUCTION-PALLET-PLAN";
    private const string ReplacedByReadyHuReason = "replaced_by_ready_hu";
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
        _data.ExecuteInTransaction(store =>
        {
            var initialDoc = store.GetDoc(docId)
                             ?? throw new InvalidOperationException("Документ не найден.");
            if (initialDoc.OrderId.HasValue
                && !store.LockOrdersForUpdate(new[] { initialDoc.OrderId.Value }))
            {
                throw new InvalidOperationException("Заказ документа не найден.");
            }

            var doc = store.GetDoc(docId)
                      ?? throw new InvalidOperationException("Документ не найден.");
            if (doc.Type != DocType.ProductionReceipt)
            {
                throw new InvalidOperationException("Документ не является выпуском продукции.");
            }
            if (doc.Status == DocStatus.Closed)
            {
                throw new InvalidOperationException("Документ уже закрыт.");
            }

            store.PlanProductionPallets(docId, DateTime.Now);
        });
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
        {
            if (!store.LockOrdersForUpdate(new[] { orderId }))
            {
                throw new InvalidOperationException("Заказ не найден.");
            }
            SyncOrderLinePlanInStore(store, orderId, orderLineId, orderedQty, oldOrderedQty, source);
        });
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

        var orderLine = store.GetOrderLines(orderId).SingleOrDefault(line => line.Id == orderLineId);
        if (orderLine == null)
        {
            return;
        }

        var target = ProductionFuturePlanTargetCalculator.Calculate(store, order, orderLine, orderedQty);
        var openBefore = GetOpenProductionPalletsForOrderLine(store, orderId, orderLineId);
        var activePlannedBefore = openBefore.Sum(pallet => ResolvePalletQtyForOrderLine(pallet, orderLineId));
        var missingBefore = Math.Max(0, target.FuturePlanQty - activePlannedBefore);

        var action = ReconcileSingleItemFuturePlan(
            store,
            order,
            orderLine,
            openBefore,
            target.FuturePlanQty);

        var activePlannedAfter = GetOpenProductionPalletsForOrderLine(store, orderId, orderLineId)
            .Sum(pallet => ResolvePalletQtyForOrderLine(pallet, orderLineId));
        var cancelledQty = Math.Max(0, activePlannedBefore - activePlannedAfter);
        var createdQty = Math.Max(0, activePlannedAfter - activePlannedBefore);
        var missingAfter = Math.Max(0, target.FuturePlanQty - activePlannedAfter);

        ProductionPalletPlanSyncDiagnostics.Log(new ProductionPalletPlanSyncReport
        {
            Source = source,
            OrderId = orderId,
            OrderLineId = orderLineId,
            OldQty = oldOrderedQty,
            NewQty = orderedQty,
            FilledQty = target.ProtectedQty,
            ActivePlannedQtyBefore = activePlannedBefore,
            MissingQty = Math.Max(missingBefore, missingAfter),
            CreatedQty = createdQty,
            CancelledQty = cancelledQty,
            ActivePlannedQtyAfter = activePlannedAfter,
            Action = action
        });
    }

    private static string ReconcileSingleItemFuturePlan(
        IDataStore store,
        Order order,
        OrderLine orderLine,
        IReadOnlyList<ProductionPallet> openPallets,
        double targetFutureQty)
    {
        var currentQty = openPallets.Sum(pallet => ResolvePalletQtyForOrderLine(pallet, orderLine.Id));
        if (Math.Abs(currentQty - targetFutureQty) <= QtyTolerance)
        {
            return "noop";
        }

        if (openPallets.Count == 0)
        {
            // Обычное редактирование не создаёт первый PRD/HU: initial planning остаётся
            // явной server-командой. Reconcile продолжает только уже существующий plan.
            return targetFutureQty > QtyTolerance ? "missing_unplanned" : "noop";
        }

        if (openPallets.Any(pallet => pallet.IsMixedPallet
                                      || pallet.HasComponentProgress
                                      || (pallet.Lines.Count > 0
                                          && (pallet.Lines.Count != 1
                                              || pallet.Lines[0].OrderLineId != orderLine.Id))))
        {
            throw new InvalidOperationException(
                "Автоматическое изменение mixed HU или частично наполненная HU запрещено. Используйте явный workflow перепланирования или коррекции.");
        }

        var item = store.FindItemById(orderLine.ItemId)
                   ?? throw new InvalidOperationException("Номенклатура строки заказа не найдена.");
        if (!item.MaxQtyPerHu.HasValue || item.MaxQtyPerHu.Value <= QtyTolerance)
        {
            throw new InvalidOperationException("Не задано количество на паллете для номенклатуры");
        }

        var maxQtyPerHu = item.MaxQtyPerHu.Value;
        if (openPallets.Any(pallet => ResolvePalletQtyForOrderLine(pallet, orderLine.Id) > maxQtyPerHu + QtyTolerance))
        {
            throw new InvalidOperationException("Существующая HU превышает max_qty_per_hu; автоматический reconcile остановлен.");
        }

        if (targetFutureQty < currentQty - QtyTolerance)
        {
            var surplus = currentQty - targetFutureQty;
            var cancelled = new List<ProductionPallet>();
            foreach (var pallet in openPallets.OrderByDescending(pallet => pallet.Id))
            {
                if (surplus <= QtyTolerance)
                {
                    break;
                }

                var palletQty = ResolvePalletQtyForOrderLine(pallet, orderLine.Id);
                if (surplus + QtyTolerance >= palletQty)
                {
                    if (string.Equals(pallet.Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase)
                        || pallet.PrintedAt.HasValue)
                    {
                        throw new InvalidOperationException(
                            $"Полное удаление напечатанной HU {pallet.HuCode} требует явного подтверждения оператора в workflow удаления плана.");
                    }

                    cancelled.Add(pallet);
                    surplus -= palletQty;
                    continue;
                }

                var resizedQty = palletQty - surplus;
                if (!store.ResizeSingleItemProductionPallet(pallet.Id, resizedQty))
                {
                    throw new InvalidOperationException($"Не удалось изменить HU {pallet.HuCode}: состояние паллеты изменилось.");
                }

                surplus = 0;
            }

            if (cancelled.Count > 0)
            {
                TombstoneProductionPalletDocLines(store, cancelled);
                if (store.CancelProductionPallets(cancelled.Select(pallet => pallet.Id).ToArray()) != cancelled.Count)
                {
                    throw new InvalidOperationException("Паллетный план изменился во время reconcile.");
                }
            }

            return cancelled.Count > 0 && surplus <= QtyTolerance ? "resize_and_trim" : "resize";
        }

        var remaining = targetFutureQty - currentQty;
        var boundary = openPallets.OrderBy(pallet => pallet.Id).Last();
        var boundaryQty = ResolvePalletQtyForOrderLine(boundary, orderLine.Id);
        var freeCapacity = Math.Max(0, maxQtyPerHu - boundaryQty);
        var boundaryIncrease = Math.Min(freeCapacity, remaining);
        if (boundaryIncrease > QtyTolerance)
        {
            if (!store.ResizeSingleItemProductionPallet(boundary.Id, boundaryQty + boundaryIncrease))
            {
                throw new InvalidOperationException($"Не удалось изменить HU {boundary.HuCode}: состояние паллеты изменилось.");
            }

            remaining -= boundaryIncrease;
        }

        if (remaining <= QtyTolerance)
        {
            return "resize";
        }

        var doc = store.GetDoc(boundary.PrdDocId);
        if (doc?.Status != DocStatus.Draft || doc.Type != DocType.ProductionReceipt)
        {
            throw new InvalidOperationException("Boundary PRD недоступен для продолжения плана.");
        }

        var receiptLine = OrderReceiptRemainingCalculator.GetRemaining(store, order)
            .FirstOrDefault(line => line.OrderLineId == orderLine.Id);
        var targetLocationId = boundary.ToLocationId ?? receiptLine?.ToLocationId
            ?? ResolveProductionPalletPlanLocation(store).Id;
        AddPlannedPalletLines(
            store,
            boundary.PrdDocId,
            new OrderReceiptLine
            {
                OrderLineId = orderLine.Id,
                OrderId = order.Id,
                ItemId = orderLine.ItemId,
                ItemName = receiptLine?.ItemName ?? item.Name,
                QtyOrdered = orderLine.QtyOrdered,
                QtyReceived = 0,
                QtyRemaining = remaining,
                ProductionPurpose = orderLine.ProductionPurpose,
                ToLocationId = targetLocationId,
                ToLocation = receiptLine?.ToLocation,
                SortOrder = receiptLine?.SortOrder ?? 0
            },
            maxQtyPerHu,
            targetLocationId);
        store.PlanProductionPallets(boundary.PrdDocId, DateTime.Now);
        return boundaryIncrease > QtyTolerance ? "resize_and_append" : "append";
    }

    internal IReadOnlyList<long> CancelFuturePlanForOrderLineAndResolveAffectedLinesInStore(
        IDataStore store,
        long orderId,
        long orderLineId)
    {
        var pallets = GetOpenProductionPalletsForOrderLine(store, orderId, orderLineId);
        if (pallets.Any(pallet => pallet.HasComponentProgress))
        {
            throw new InvalidOperationException("Паллетный план находится в фактическом состоянии: есть частично наполненная микс-паллета.");
        }

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
            if (!store.LockOrdersForUpdate(new[] { orderId }))
            {
                throw new InvalidOperationException("Заказ не найден.");
            }
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

    public ProductionPalletOrderPlanResult PlanOrder(long orderId, ProductionPalletPlanMode mode)
    {
        if (mode == ProductionPalletPlanMode.Full)
        {
            return PlanOrder(orderId);
        }

        if (mode == ProductionPalletPlanMode.AdoptInternalThenPlan)
        {
            return PlanOrderAdoptInternalThenPlan(orderId);
        }

        if (mode == ProductionPalletPlanMode.ApplySelectedCoverageThenPlan)
        {
            return PlanOrderApplySelectedCoverageThenPlan(orderId, new ProductionPalletSelectedCoveragePlanRequest());
        }

        var prdDocId = 0L;
        var wasExisting = false;
        var productionRequired = true;
        IReadOnlyList<ProductionPalletPlanSkippedLine> skippedLines = Array.Empty<ProductionPalletPlanSkippedLine>();
        IReadOnlyList<long> plannedLineIds = Array.Empty<long>();
        var noSafeLines = false;
        _data.ExecuteInTransaction(store =>
        {
            if (!store.LockOrdersForUpdate(new[] { orderId }))
            {
                throw new InvalidOperationException("Заказ не найден.");
            }
            var order = store.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
            if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
            {
                throw new InvalidOperationException(order.Status == OrderStatus.Merged
                    ? "Заказ объединён с другим заказом. Выпуск по нему не требуется."
                    : "Заказ недоступен для планирования паллет.");
            }

            if (order.Type != OrderType.Customer)
            {
                throw new InvalidOperationException(
                    "Режим планирования без позиций с ожидаемым внутренним выпуском доступен только для клиентского заказа.");
            }

            var scope = BuildPrePlanSafeScope(store, order);
            skippedLines = scope.SkippedLines;
            plannedLineIds = scope.SafeLineIds;
            if (scope.SkippedLines.Count > 0 && scope.SafeLineIds.Count == 0)
            {
                noSafeLines = true;
                return;
            }

            var preparedDoc = FindPreparedOpenProductionReceipt(store, orderId, requireRemaining: false);
            if (preparedDoc != null)
            {
                prdDocId = preparedDoc.Id;
                wasExisting = true;
            }

            // Пустой skipped означает отсутствие пересечения: safe-only эквивалентен полному планированию.
            var scopedOrderLineIds = scope.SkippedLines.Count == 0 ? null : scope.SafeLineIds;
            productionRequired = AppendPlannedPalletsForOrderLinesInStore(
                store,
                order,
                orderId,
                scopedOrderLineIds,
                allowEmptyRemaining: false,
                out prdDocId,
                existingPrdDocId: prdDocId);
        });

        if (noSafeLines)
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
                Message = "Все позиции пересекаются с ожидаемым внутренним выпуском. План не создан.",
                Summary = new ProductionPalletSummary(),
                Document = new ProductionPalletDocument
                {
                    Summary = new ProductionPalletSummary()
                },
                SkippedLines = skippedLines,
                PlannedOrderLineIds = Array.Empty<long>()
            };
        }

        var baseResult = productionRequired || prdDocId > 0
            ? BuildOrderPlanResult(orderId, prdDocId, wasExisting)
            : BuildNoProductionRequiredResult(orderId);
        return new ProductionPalletOrderPlanResult
        {
            OrderId = baseResult.OrderId,
            OrderRef = baseResult.OrderRef,
            PrdDocId = baseResult.PrdDocId,
            PrdDocRef = baseResult.PrdDocRef,
            WasExisting = baseResult.WasExisting,
            ProductionRequired = baseResult.ProductionRequired,
            Message = baseResult.Message,
            Summary = baseResult.Summary,
            Document = baseResult.Document,
            SkippedLines = skippedLines,
            PlannedOrderLineIds = plannedLineIds
        };
    }

    public ProductionPalletOrderPlanResult PlanOrderApplySelectedCoverageThenPlan(
        long orderId,
        ProductionPalletSelectedCoveragePlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prdDocId = 0L;
        var wasExisting = false;
        var productionRequired = true;
        IReadOnlyList<ProductionPalletWarehouseHuCandidate> boundWarehouseHus = Array.Empty<ProductionPalletWarehouseHuCandidate>();
        IReadOnlyList<ProductionPalletProjectedAdoptionHu> adopted = Array.Empty<ProductionPalletProjectedAdoptionHu>();
        IReadOnlyList<ProductionPalletAdoptionSkippedCandidate> skippedCandidates = Array.Empty<ProductionPalletAdoptionSkippedCandidate>();
        IReadOnlyList<long> plannedLineIds = Array.Empty<long>();
        int newlyPlannedPalletCount = 0;
        double newlyPlannedQty = 0;

        _data.ExecuteInTransaction(store =>
        {
            var targetOrder = store.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
            if (targetOrder.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
            {
                throw new InvalidOperationException(targetOrder.Status == OrderStatus.Merged
                    ? "Заказ объединён с другим заказом. Выпуск по нему не требуется."
                    : "Заказ недоступен для планирования паллет.");
            }

            if (targetOrder.Type != OrderType.Customer)
            {
                throw new InvalidOperationException(
                    "Режим выбранного покрытия доступен только для клиентского заказа.");
            }

            var lockedSourceOrderIds = store.GetOrders()
                .Where(order => order.Type == OrderType.Internal)
                .Where(order => order.Status is OrderStatus.Draft or OrderStatus.InProgress)
                .Select(order => order.Id)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            var lockOrderIds = lockedSourceOrderIds
                .Append(orderId)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            if (lockOrderIds.Length > 0 && !store.LockOrdersForUpdate(lockOrderIds))
            {
                throw new InvalidOperationException("Не удалось заблокировать заказы для планирования паллет.");
            }

            var selectedWarehouseHuCodes = request.SelectedWarehouseHus
                .Select(selection => HuBindingApplyShared.NormalizeHu(selection.HuCode))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var factsStore = HuMutationEligibilityService.LockMutationScope(
                store,
                lockOrderIds,
                selectedWarehouseHuCodes);
            if (selectedWarehouseHuCodes.Length > 0)
            {
                var decisions = new HuMutationEligibilityService(factsStore).Evaluate(
                    selectedWarehouseHuCodes,
                    facts => HuMutationEligibilityService.WholeStockContext(
                        facts,
                        HuMutationOperation.ReserveOrBind,
                        orderId));
                var rejected = decisions.FirstOrDefault(pair => !pair.Value.Allowed);
                if (rejected.Value != null)
                {
                    var publicCode = rejected.Value.Reasons.Any(
                        reason => reason.Code == HuMutationEligibilityReasonCode.HuReservedByOtherOrder)
                        ? "HU_RESERVED_BY_OTHER_ORDER"
                        : "STALE_WAREHOUSE_SELECTION";
                    throw SelectedCoverageError(
                        publicCode,
                        $"HU '{rejected.Key}' изменилась или больше не подходит.",
                        rejected.Value.Reasons.Select(reason => reason.Details).ToArray());
                }
            }

            targetOrder = store.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
            EnsureSelectedCoverageTargetOrderIsOpenCustomer(targetOrder);
            boundWarehouseHus = ApplySelectedWarehouseHuCoverageInStore(store, targetOrder, request.SelectedWarehouseHus);

            var linesToPlan = GetLinesNeedingPalletAppend(store, targetOrder)
                .Where(line => line.QtyRemaining > QtyTolerance)
                .ToArray();
            plannedLineIds = linesToPlan.Select(line => line.OrderLineId).Distinct().ToArray();

            var selectedInternalIds = (request.SelectedInternalProductionPalletIds ?? Array.Empty<long>())
                .Where(id => id > 0)
                .Distinct()
                .ToHashSet();
            var projection = BuildInternalPlanAdoptionProjection(store, targetOrder, linesToPlan, lockedSourceOrderIds);
            skippedCandidates = projection.Skipped;
            adopted = selectedInternalIds.Count == 0
                ? Array.Empty<ProductionPalletProjectedAdoptionHu>()
                : projection.Adoptable
                    .Where(candidate => selectedInternalIds.Contains(candidate.ProductionPalletId))
                    .ToArray();

            var adoptedIds = adopted.Select(candidate => candidate.ProductionPalletId).ToHashSet();
            var missingSelectedInternalIds = selectedInternalIds.Where(id => !adoptedIds.Contains(id)).OrderBy(id => id).ToArray();
            if (missingSelectedInternalIds.Length > 0)
            {
                throw SelectedCoverageError(
                    "STALE_INTERNAL_SELECTION",
                    "Выбранные planned HU из INTERNAL изменились или больше не подходят. Обновите preview и повторите действие.",
                    missingSelectedInternalIds.Select(id => $"production_pallet_id={id}").ToArray());
            }

            if (adopted.Count > 0)
            {
                var existingDoc = FindPreparedOpenProductionReceipt(store, orderId, requireRemaining: false)
                                  ?? FindReusableEmptyProductionReceipt(store, orderId);
                if (existingDoc != null)
                {
                    prdDocId = existingDoc.Id;
                    wasExisting = true;
                }
                else
                {
                    prdDocId = CreateProductionReceipt(store, targetOrder).Id;
                    wasExisting = false;
                }

                ValidateSourceQuantityReductionForAdoption(store, adopted);
                int transferredCount;
                try
                {
                    transferredCount = store.AdoptSelectedProductionPallets(
                        prdDocId,
                        orderId,
                        BuildSelectedAdoptionRows(adopted));
                }
                catch (InvalidOperationException ex)
                {
                    throw SelectedCoverageError(
                        "STALE_INTERNAL_SELECTION",
                        ex.Message);
                }

                if (transferredCount != adopted.Count)
                {
                    throw SelectedCoverageError(
                        "STALE_INTERNAL_SELECTION",
                        "Нельзя перенести planned HU: часть выбранных паллет изменилась до переноса. Обновите preview и повторите действие.");
                }

                ReduceSourceInternalOrderLines(store, adopted);

                foreach (var sourceDocId in adopted.Select(candidate => candidate.SourcePrdDocId).Distinct())
                {
                    var sourceOrderId = adopted.First(candidate => candidate.SourcePrdDocId == sourceDocId).SourceOrderId;
                    EmptyDraftProductionReceiptCleanup.TryDeleteEmptyDraftProductionReceiptIfSafe(
                        store,
                        sourceOrderId,
                        sourceDocId);
                }

                CleanupDepletedSourceInternalOrderLines(store, adopted);

                foreach (var sourceOrderId in adopted.Select(candidate => candidate.SourceOrderId).Distinct())
                {
                    InternalOrderMergeService.TryMarkAsMerged(
                        store,
                        sourceOrderId,
                        orderId,
                        targetOrder.OrderRef);
                }
            }

            var palletIdsBeforeAppend = GetProductionPalletsByOrder(store, orderId)
                .Select(pallet => pallet.Id)
                .ToHashSet();
            var preparedDoc = FindPreparedOpenProductionReceipt(store, orderId, requireRemaining: false);
            if (preparedDoc != null)
            {
                prdDocId = preparedDoc.Id;
                wasExisting = wasExisting || adopted.Count == 0;
            }

            if (request.PlanRemainder)
            {
                productionRequired = AppendPlannedPalletsForOrderLinesInStore(
                    store,
                    targetOrder,
                    orderId,
                    scopedOrderLineIds: null,
                    allowEmptyRemaining: adopted.Count > 0 || boundWarehouseHus.Count > 0,
                    out prdDocId,
                    existingPrdDocId: prdDocId);
            }
            else
            {
                productionRequired = false;
            }

            var newPallets = GetProductionPalletsByOrder(store, orderId)
                .Where(pallet => !palletIdsBeforeAppend.Contains(pallet.Id))
                .ToArray();
            newlyPlannedPalletCount = newPallets.Length;
            newlyPlannedQty = newPallets.Sum(pallet => Math.Max(0, pallet.PlannedQty));
        });

        var baseResult = productionRequired || prdDocId > 0
            ? BuildOrderPlanResult(orderId, prdDocId, wasExisting)
            : BuildNoProductionRequiredResult(orderId);
        var message = boundWarehouseHus.Count > 0 || adopted.Count > 0
            ? $"Применено покрытие: складских HU {boundWarehouseHus.Count}, planned HU из INTERNAL {adopted.Count}. Сформирован остаток к производству."
            : baseResult.Message;
        return new ProductionPalletOrderPlanResult
        {
            OrderId = baseResult.OrderId,
            OrderRef = baseResult.OrderRef,
            PrdDocId = baseResult.PrdDocId,
            PrdDocRef = baseResult.PrdDocRef,
            WasExisting = baseResult.WasExisting,
            ProductionRequired = baseResult.ProductionRequired,
            Message = message,
            Summary = baseResult.Summary,
            Document = baseResult.Document,
            PlannedOrderLineIds = plannedLineIds,
            AdoptedInternalPlannedHus = adopted,
            AdoptionSkippedCandidates = skippedCandidates,
            ReprintRequiredHus = adopted.Where(candidate => candidate.WillRequireReprint).ToArray(),
            BoundWarehouseHus = boundWarehouseHus,
            AdoptedPalletCount = adopted.Count,
            AdoptedQty = adopted.Sum(candidate => candidate.PlannedQty),
            BoundWarehouseHuCount = boundWarehouseHus.Count,
            BoundWarehouseQty = boundWarehouseHus.Sum(candidate => candidate.Qty),
            NewlyPlannedPalletCount = newlyPlannedPalletCount,
            NewlyPlannedQty = newlyPlannedQty
        };
    }

    private ProductionPalletOrderPlanResult PlanOrderAdoptInternalThenPlan(long orderId)
    {
        var prdDocId = 0L;
        var wasExisting = false;
        var productionRequired = true;
        IReadOnlyList<ProductionPalletProjectedAdoptionHu> adopted = Array.Empty<ProductionPalletProjectedAdoptionHu>();
        IReadOnlyList<ProductionPalletAdoptionSkippedCandidate> skippedCandidates = Array.Empty<ProductionPalletAdoptionSkippedCandidate>();
        IReadOnlyList<long> plannedLineIds = Array.Empty<long>();
        int newlyPlannedPalletCount = 0;
        double newlyPlannedQty = 0;

        _data.ExecuteInTransaction(store =>
        {
            var targetOrder = store.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
            if (targetOrder.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
            {
                throw new InvalidOperationException(targetOrder.Status == OrderStatus.Merged
                    ? "Заказ объединён с другим заказом. Выпуск по нему не требуется."
                    : "Заказ недоступен для планирования паллет.");
            }

            if (targetOrder.Type != OrderType.Customer)
            {
                throw new InvalidOperationException(
                    "Режим переноса planned HU из внутреннего заказа доступен только для клиентского заказа.");
            }

            var lockedSourceOrderIds = store.GetOrders()
                .Where(order => order.Type == OrderType.Internal)
                .Where(order => order.Status is OrderStatus.Draft or OrderStatus.InProgress)
                .Select(order => order.Id)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            var lockOrderIds = lockedSourceOrderIds
                .Append(orderId)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            if (lockOrderIds.Length > 0)
            {
                if (!store.LockOrdersForUpdate(lockOrderIds))
                {
                    throw new InvalidOperationException("Не удалось заблокировать заказы для планирования паллет.");
                }
            }

            targetOrder = store.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
            var linesToPlan = GetLinesNeedingPalletAppend(store, targetOrder)
                .Where(line => line.QtyRemaining > QtyTolerance)
                .ToArray();
            plannedLineIds = linesToPlan.Select(line => line.OrderLineId).Distinct().ToArray();

            var projection = BuildInternalPlanAdoptionProjection(store, targetOrder, linesToPlan, lockedSourceOrderIds);
            adopted = projection.Adoptable;
            skippedCandidates = projection.Skipped;

            if (adopted.Count > 0)
            {
                var existingDoc = FindPreparedOpenProductionReceipt(store, orderId, requireRemaining: false)
                                  ?? FindReusableEmptyProductionReceipt(store, orderId);
                if (existingDoc != null)
                {
                    prdDocId = existingDoc.Id;
                    wasExisting = true;
                }
                else
                {
                    prdDocId = CreateProductionReceipt(store, targetOrder).Id;
                    wasExisting = false;
                }

                ValidateSourceQuantityReductionForAdoption(store, adopted);
                var transferredCount = store.AdoptSelectedProductionPallets(
                    prdDocId,
                    orderId,
                    BuildSelectedAdoptionRows(adopted));
                if (transferredCount != adopted.Count)
                {
                    throw new InvalidOperationException(
                        "Нельзя перенести planned HU: часть выбранных паллет изменилась до переноса. Обновите заказ и повторите планирование.");
                }

                ReduceSourceInternalOrderLines(store, adopted);

                foreach (var sourceDocId in adopted.Select(candidate => candidate.SourcePrdDocId).Distinct())
                {
                    var sourceOrderId = adopted.First(candidate => candidate.SourcePrdDocId == sourceDocId).SourceOrderId;
                    EmptyDraftProductionReceiptCleanup.TryDeleteEmptyDraftProductionReceiptIfSafe(
                        store,
                        sourceOrderId,
                        sourceDocId);
                }

                CleanupDepletedSourceInternalOrderLines(store, adopted);

                foreach (var sourceOrderId in adopted.Select(candidate => candidate.SourceOrderId).Distinct())
                {
                    InternalOrderMergeService.TryMarkAsMerged(
                        store,
                        sourceOrderId,
                        orderId,
                        targetOrder.OrderRef);
                }
            }

            var palletIdsBeforeAppend = GetProductionPalletsByOrder(store, orderId)
                .Select(pallet => pallet.Id)
                .ToHashSet();
            var preparedDoc = FindPreparedOpenProductionReceipt(store, orderId, requireRemaining: false);
            if (preparedDoc != null)
            {
                prdDocId = preparedDoc.Id;
                wasExisting = wasExisting || adopted.Count == 0;
            }

            productionRequired = AppendPlannedPalletsForOrderLinesInStore(
                store,
                targetOrder,
                orderId,
                scopedOrderLineIds: null,
                allowEmptyRemaining: adopted.Count > 0,
                out prdDocId,
                existingPrdDocId: prdDocId);

            var newPallets = GetProductionPalletsByOrder(store, orderId)
                .Where(pallet => !palletIdsBeforeAppend.Contains(pallet.Id))
                .ToArray();
            newlyPlannedPalletCount = newPallets.Length;
            newlyPlannedQty = newPallets.Sum(pallet => Math.Max(0, pallet.PlannedQty));
        });

        var baseResult = productionRequired || prdDocId > 0
            ? BuildOrderPlanResult(orderId, prdDocId, wasExisting)
            : BuildNoProductionRequiredResult(orderId);
        var adoptedQty = adopted.Sum(candidate => candidate.PlannedQty);
        var message = adopted.Count > 0
            ? $"Перенесено planned HU из внутреннего заказа: {adopted.Count}. Сформирован остаток к производству."
            : baseResult.Message;
        return new ProductionPalletOrderPlanResult
        {
            OrderId = baseResult.OrderId,
            OrderRef = baseResult.OrderRef,
            PrdDocId = baseResult.PrdDocId,
            PrdDocRef = baseResult.PrdDocRef,
            WasExisting = baseResult.WasExisting,
            ProductionRequired = baseResult.ProductionRequired,
            Message = message,
            Summary = baseResult.Summary,
            Document = baseResult.Document,
            PlannedOrderLineIds = plannedLineIds,
            AdoptedInternalPlannedHus = adopted,
            AdoptionSkippedCandidates = skippedCandidates,
            ReprintRequiredHus = adopted.Where(candidate => candidate.WillRequireReprint).ToArray(),
            AdoptedPalletCount = adopted.Count,
            AdoptedQty = adoptedQty,
            NewlyPlannedPalletCount = newlyPlannedPalletCount,
            NewlyPlannedQty = newlyPlannedQty
        };
    }

    private static IReadOnlyList<ProductionPalletSelectedAdoption> BuildSelectedAdoptionRows(
        IReadOnlyList<ProductionPalletProjectedAdoptionHu> adopted)
    {
        return adopted
            .Select(candidate => new ProductionPalletSelectedAdoption
            {
                ProductionPalletId = candidate.ProductionPalletId,
                SourceOrderId = candidate.SourceOrderId,
                SourcePrdDocId = candidate.SourcePrdDocId,
                ExpectedStatus = candidate.Status,
                HuCode = candidate.HuCode,
                TargetOrderLineId = candidate.TargetOrderLineId,
                Lines = candidate.Lines
                    .Select(line => new ProductionPalletSelectedAdoptionLine
                    {
                        DocLineId = line.DocLineId,
                        SourceOrderLineId = line.SourceOrderLineId,
                        TargetOrderLineId = line.TargetOrderLineId,
                        ItemId = line.ItemId,
                        PlannedQty = line.PlannedQty
                    })
                    .ToArray()
            })
            .ToArray();
    }

    private static IReadOnlyList<ProductionPalletWarehouseHuCandidate> ApplySelectedWarehouseHuCoverageInStore(
        IDataStore store,
        Order targetOrder,
        IReadOnlyList<ProductionPalletSelectedWarehouseHu>? selectedWarehouseHus)
    {
        selectedWarehouseHus ??= Array.Empty<ProductionPalletSelectedWarehouseHu>();
        if (selectedWarehouseHus.Count == 0)
        {
            return Array.Empty<ProductionPalletWarehouseHuCandidate>();
        }

        if (store is not IOptimizedHuReservationCandidatesStore)
        {
            throw SelectedCoverageError(
                "WAREHOUSE_CANDIDATES_UNSUPPORTED",
                "Хранилище не поддерживает проверку складских HU.");
        }

        var normalizedSelections = selectedWarehouseHus
            .Select(selection => new
            {
                HuCode = HuBindingApplyShared.NormalizeHu(selection.HuCode),
                selection.ItemId,
                selection.TargetOrderLineId
            })
            .ToArray();
        if (normalizedSelections.Any(selection => string.IsNullOrWhiteSpace(selection.HuCode)
                                                  || selection.ItemId <= 0
                                                  || selection.TargetOrderLineId <= 0))
        {
            throw SelectedCoverageError(
                "INVALID_WAREHOUSE_SELECTION",
                "Некорректный выбор складских HU. Обновите preview и повторите действие.");
        }

        var duplicateHu = normalizedSelections
            .GroupBy(selection => selection.HuCode!, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateHu != null)
        {
            throw SelectedCoverageError(
                "DUPLICATE_WAREHOUSE_HU_SELECTION",
                $"HU '{duplicateHu.Key}' выбран более одного раза.");
        }

        var orderLines = store.GetOrderLines(targetOrder.Id)
            .Where(line => line.Id > 0)
            .ToDictionary(line => line.Id);
        var itemNamesById = store.GetItems(null).ToDictionary(item => item.Id, item => item.Name);
        var existingPlanLines = store.GetOrderReceiptPlanLines(targetOrder.Id)
            .Where(line => line.QtyPlanned > QtyTolerance)
            .ToArray();
        var shipmentRemainingByLine = store.GetOrderShipmentRemaining(targetOrder.Id)
            .ToDictionary(line => line.OrderLineId);
        var reservedByOtherActiveCustomerOrders = store.GetReservedOrderReceiptHuCodes(targetOrder.Id)
            .Select(HuBindingApplyShared.NormalizeHu)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reservedOwnerByHu = (store.GetHuOrderContextRows() ?? Array.Empty<HuOrderContextRow>())
            .Where(row => row.ReservedCustomerOrderId.HasValue && row.ReservedCustomerOrderId.Value != targetOrder.Id)
            .Where(row => !string.IsNullOrWhiteSpace(row.HuCode))
            .GroupBy(row => HuBindingApplyShared.NormalizeHu(row.HuCode)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var candidatesService = new HuReservationCandidatesService(store);
        var affectedLineIds = normalizedSelections.Select(selection => selection.TargetOrderLineId).Distinct().ToHashSet();
        var duplicateHuGuard = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var replacementLines = new List<OrderReceiptPlanLine>();
        var applied = new List<ProductionPalletWarehouseHuCandidate>();
        var palletsToCancel = new List<long>();

        foreach (var group in normalizedSelections.GroupBy(selection => selection.TargetOrderLineId))
        {
            if (!orderLines.TryGetValue(group.Key, out var orderLine))
            {
                throw SelectedCoverageError(
                    "WAREHOUSE_TARGET_LINE_NOT_FOUND",
                    $"Строка заказа {group.Key} не найдена.");
            }

            var selectedForLine = group.ToArray();
            if (selectedForLine.Any(selection => selection.ItemId != orderLine.ItemId))
            {
                throw SelectedCoverageError(
                    "WAREHOUSE_ITEM_MISMATCH",
                    $"Выбранная складская HU не соответствует товару строки {orderLine.Id}.");
            }

            var currentPlanForLine = existingPlanLines
                .Where(line => line.OrderLineId == orderLine.Id)
                .OrderBy(line => line.SortOrder)
                .ThenBy(line => line.Id)
                .ToArray();
            var previousHuCodes = currentPlanForLine
                .Select(line => HuBindingApplyShared.NormalizeHu(line.ToHu))
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Cast<string>()
                .ToArray();
            var previousHuSet = previousHuCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var finalHuCodes = previousHuCodes
                .Concat(selectedForLine.Select(selection => selection.HuCode!))
                .ToArray();
            try
            {
                HuBindingApplyShared.ValidateDuplicateHuInFinalSelection(finalHuCodes, orderLine.Id, duplicateHuGuard);
                HuBindingApplyShared.ValidateHuNotReservedOnOtherUnaffectedLine(
                    targetOrder.Id,
                    orderLine.Id,
                    finalHuCodes,
                    affectedLineIds,
                    existingPlanLines);
            }
            catch (OrderHuBindingApplyFinalException ex)
            {
                throw SelectedCoverageError(ex.ErrorCode, ex.Message, ex.Problems);
            }

            var candidatesByHu = HuBindingApplyShared.BuildCandidatesByHu(candidatesService, targetOrder.Id, orderLine);
            var selectedCandidates = new List<HuReservationCandidateResult>();
            foreach (var selection in selectedForLine)
            {
                var huCode = selection.HuCode!;
                if (previousHuSet.Contains(huCode))
                {
                    throw SelectedCoverageError(
                        "WAREHOUSE_HU_ALREADY_BOUND",
                        $"HU '{huCode}' уже привязана к заказу. Обновите preview и повторите действие.");
                }

                if (reservedByOtherActiveCustomerOrders.Contains(huCode))
                {
                    if (reservedOwnerByHu.TryGetValue(huCode, out var owner))
                    {
                        throw SelectedCoverageError(
                            "HU_RESERVED_BY_OTHER_ORDER",
                            $"HU '{huCode}' уже закреплён за другим активным клиентским заказом.",
                            [$"HU '{huCode}' принадлежит заказу {owner.ReservedCustomerOrderRef ?? owner.ReservedCustomerOrderId!.Value.ToString(CultureInfo.InvariantCulture)}."]);
                    }

                    throw SelectedCoverageError(
                        "HU_RESERVED_BY_OTHER_ORDER",
                        $"HU '{huCode}' уже зарезервирован другим активным клиентским заказом.",
                        [$"HU '{huCode}' не может быть выбран для заказа {targetOrder.Id}."]);
                }

                if (!candidatesByHu.TryGetValue(huCode, out var candidate)
                    || !string.Equals(candidate.Source, OrderHuReservationApplyService.SourceLedgerStock, StringComparison.OrdinalIgnoreCase)
                    || candidate.Qty <= QtyTolerance)
                {
                    throw SelectedCoverageError(
                        "STALE_WAREHOUSE_SELECTION",
                        $"HU '{huCode}' больше не доступна как свободная складская HU. Обновите preview и повторите действие.");
                }

                if (candidate.ReservedByOrderId.HasValue && candidate.ReservedByOrderId.Value != targetOrder.Id)
                {
                    throw SelectedCoverageError(
                        "WAREHOUSE_HU_RESERVED_BY_OTHER_ORDER",
                        $"HU '{huCode}' уже зарезервирована другим клиентским заказом.");
                }

                selectedCandidates.Add(candidate);
            }

            var previousQty = currentPlanForLine.Sum(line => Math.Max(0, line.QtyPlanned));
            var selectedQty = selectedCandidates.Sum(candidate => Math.Max(0, candidate.Qty));
            var finalBoundQty = previousQty + selectedQty;
            var remainingQty = HuBindingApplyShared.ResolveShipmentRemaining(orderLine, shipmentRemainingByLine);
            if (finalBoundQty > remainingQty + QtyTolerance)
            {
                throw SelectedCoverageError(
                    "WAREHOUSE_QTY_EXCEEDS_REMAINING",
                    "Выбранные складские HU превышают остаток строки заказа.",
                    [$"order_line_id={orderLine.Id}", $"final_bound_qty={finalBoundQty:0.###}", $"remaining_qty={remainingQty:0.###}"]);
            }

            var finalBoundQtyByHu = currentPlanForLine
                .Where(line => !string.IsNullOrWhiteSpace(line.ToHu))
                .GroupBy(line => HuBindingApplyShared.NormalizeHu(line.ToHu)!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(line => Math.Max(0, line.QtyPlanned)), StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in selectedCandidates)
            {
                finalBoundQtyByHu[candidate.HuCode] = finalBoundQtyByHu.TryGetValue(candidate.HuCode, out var current)
                    ? current + Math.Max(0, candidate.Qty)
                    : Math.Max(0, candidate.Qty);
            }

            var surplus = HuBindingApplyShared.ComputeCancellableFuturePlanSurplus(
                store,
                targetOrder.Id,
                orderLine,
                finalBoundQtyByHu);
            try
            {
                palletsToCancel.AddRange(HuBindingApplyShared.SelectFuturePlanPalletsToCancel(
                    store,
                    targetOrder.Id,
                    orderLine,
                    surplus));
            }
            catch (OrderHuBindingApplyFinalException ex)
            {
                throw SelectedCoverageError(
                    ex.ErrorCode,
                    ex.Message,
                    ex.Problems);
            }

            var sortOrder = 0;
            foreach (var current in currentPlanForLine)
            {
                replacementLines.Add(new OrderReceiptPlanLine
                {
                    OrderId = targetOrder.Id,
                    OrderLineId = orderLine.Id,
                    ItemId = orderLine.ItemId,
                    ItemName = current.ItemName,
                    QtyPlanned = current.QtyPlanned,
                    ToLocationId = current.ToLocationId,
                    ToLocationCode = current.ToLocationCode,
                    ToHu = current.ToHu,
                    SortOrder = sortOrder++
                });
            }

            foreach (var candidate in selectedCandidates)
            {
                replacementLines.Add(new OrderReceiptPlanLine
                {
                    OrderId = targetOrder.Id,
                    OrderLineId = orderLine.Id,
                    ItemId = orderLine.ItemId,
                    ItemName = itemNamesById.GetValueOrDefault(orderLine.ItemId, string.Empty),
                    QtyPlanned = candidate.Qty,
                    ToHu = candidate.HuCode,
                    SortOrder = sortOrder++
                });
                applied.Add(new ProductionPalletWarehouseHuCandidate
                {
                    HuCode = candidate.HuCode,
                    ItemId = orderLine.ItemId,
                    ItemName = itemNamesById.GetValueOrDefault(orderLine.ItemId, string.Empty),
                    TargetOrderLineId = orderLine.Id,
                    Qty = candidate.Qty,
                    Status = "LEDGER_STOCK",
                    SourceRef = candidate.Note,
                    Recommended = true,
                    SelectedByDefault = true
                });
            }
        }

        store.ReplaceOrderReceiptPlanLinesForOrderLines(targetOrder.Id, affectedLineIds, replacementLines);
        if (applied.Count > 0 && !targetOrder.UseReservedStock)
        {
            store.UpdateOrder(HuBindingApplyShared.CopyOrderWithReservedStock(targetOrder));
        }

        if (palletsToCancel.Count > 0)
        {
            var distinctPalletIds = palletsToCancel.Distinct().ToArray();
            var cancelled = store.CancelProductionPalletsForReadyHuBinding(
                distinctPalletIds,
                ReplacedByReadyHuReason,
                DateTime.UtcNow);
            if (cancelled != distinctPalletIds.Length)
            {
                throw SelectedCoverageError(
                    "HU_BINDING_PLAN_CONFLICT",
                    "Плановые паллеты изменились и не могут быть безопасно отменены.");
            }

            store.RemoveDocLinesForProductionPallets(distinctPalletIds);
        }

        new OrderService(store).RefreshPersistedStatus(targetOrder.Id);
        return applied;
    }

    private static ProductionPalletSelectedCoverageException SelectedCoverageError(
        string code,
        string message,
        IReadOnlyList<string>? problems = null) =>
        new(code, message, problems);

    private static void EnsureSelectedCoverageTargetOrderIsOpenCustomer(Order targetOrder)
    {
        if (targetOrder.Type != OrderType.Customer)
        {
            throw new InvalidOperationException(
                "Режим выбранного покрытия доступен только для клиентского заказа.");
        }

        if (targetOrder.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
        {
            throw new InvalidOperationException(targetOrder.Status == OrderStatus.Merged
                ? "Заказ объединён с другим заказом. Выпуск по нему не требуется."
                : "Заказ недоступен для планирования паллет.");
        }
    }

    private static void ValidateSourceQuantityReductionForAdoption(
        IDataStore store,
        IReadOnlyList<ProductionPalletProjectedAdoptionHu> adopted)
    {
        var selectedPalletIds = adopted.Select(candidate => candidate.ProductionPalletId).ToHashSet();
        foreach (var sourceGroup in adopted.GroupBy(candidate => candidate.SourceOrderId))
        {
            var sourceOrder = store.GetOrder(sourceGroup.Key)
                              ?? throw new InvalidOperationException("Внутренний заказ-источник не найден.");
            var sourceLines = store.GetOrderLines(sourceOrder.Id)
                .ToDictionary(line => line.Id, line => line);
            var confirmedByLine = BuildInternalPlanningCoverage(store, sourceOrder.Id, sourceLines.Values.ToArray());
            var reductionByLine = sourceGroup
                .SelectMany(candidate => candidate.Lines)
                .GroupBy(line => line.SourceOrderLineId)
                .ToDictionary(group => group.Key, group => group.Sum(line => line.PlannedQty));

            foreach (var (sourceLineId, reductionQty) in reductionByLine)
            {
                if (!sourceLines.TryGetValue(sourceLineId, out var sourceLine))
                {
                    throw new InvalidOperationException("Строка внутреннего заказа для переноса не найдена.");
                }

                var newQtyOrdered = Math.Max(0, sourceLine.QtyOrdered - reductionQty);
                var confirmedQty = confirmedByLine.TryGetValue(sourceLineId, out var confirmed) ? confirmed : 0d;
                var activeNonTransferredQty = GetOpenProductionPalletsForOrderLine(store, sourceOrder.Id, sourceLineId)
                    .Where(pallet => !selectedPalletIds.Contains(pallet.Id))
                    .Sum(pallet => ResolvePalletQtyForOrderLine(pallet, sourceLineId));
                var minimumAllowed = confirmedQty + activeNonTransferredQty;
                if (newQtyOrdered + QtyTolerance < minimumAllowed)
                {
                    throw new InvalidOperationException(
                        "Нельзя перенести planned HU: уменьшение внутреннего заказа опустит ожидаемый выпуск ниже уже произведённого или активного непереносимого покрытия.");
                }
            }
        }
    }

    private static void ReduceSourceInternalOrderLines(
        IDataStore store,
        IReadOnlyList<ProductionPalletProjectedAdoptionHu> adopted)
    {
        foreach (var sourceGroup in adopted.GroupBy(candidate => candidate.SourceOrderId))
        {
            var sourceLines = store.GetOrderLines(sourceGroup.Key)
                .ToDictionary(line => line.Id, line => line);
            var reductionByLine = sourceGroup
                .SelectMany(candidate => candidate.Lines)
                .GroupBy(line => line.SourceOrderLineId)
                .ToDictionary(group => group.Key, group => group.Sum(line => line.PlannedQty));

            foreach (var (sourceLineId, reductionQty) in reductionByLine)
            {
                if (!sourceLines.TryGetValue(sourceLineId, out var sourceLine))
                {
                    throw new InvalidOperationException("Строка внутреннего заказа для переноса не найдена.");
                }

                var newQtyOrdered = Math.Max(0, sourceLine.QtyOrdered - reductionQty);
                store.UpdateOrderLineQty(sourceLineId, newQtyOrdered);
            }
        }
    }

    private static void CleanupDepletedSourceInternalOrderLines(
        IDataStore store,
        IReadOnlyList<ProductionPalletProjectedAdoptionHu> adopted)
    {
        foreach (var sourceGroup in adopted.GroupBy(candidate => candidate.SourceOrderId))
        {
            var sourceOrder = store.GetOrder(sourceGroup.Key);
            if (sourceOrder?.Type != OrderType.Internal)
            {
                continue;
            }

            var candidateLineIds = sourceGroup
                .SelectMany(candidate => candidate.Lines)
                .Select(line => line.SourceOrderLineId)
                .Distinct()
                .ToHashSet();
            if (candidateLineIds.Count == 0)
            {
                continue;
            }

            var sourceLines = store.GetOrderLines(sourceOrder.Id);
            var producedByLine = BuildInternalPlanningCoverage(store, sourceOrder.Id, sourceLines);
            foreach (var sourceLine in sourceLines.Where(line => candidateLineIds.Contains(line.Id)).ToArray())
            {
                if (sourceLine.QtyOrdered > QtyTolerance)
                {
                    continue;
                }

                var producedQty = producedByLine.TryGetValue(sourceLine.Id, out var produced) ? produced : 0d;
                if (producedQty > QtyTolerance)
                {
                    continue;
                }

                var blocker = GetSourceInternalOrderLineCleanupBlocker(store, sourceOrder.Id, sourceLine.Id);
                if (blocker != SourceInternalOrderLineCleanupBlocker.None)
                {
                    continue;
                }

                ClearStaleInternalReceiptPlanRowsForDepletedSourceLine(store, sourceOrder.Id, sourceLine.Id);
                if (store.GetOrderReceiptPlanLines(sourceOrder.Id).Any(line => line.OrderLineId == sourceLine.Id))
                {
                    continue;
                }

                store.DeleteOrderLine(sourceLine.Id);
            }
        }
    }

    private enum SourceInternalOrderLineCleanupBlocker
    {
        None,
        RemainingDocLine,
        ActiveProductionPallet
    }

    private static SourceInternalOrderLineCleanupBlocker GetSourceInternalOrderLineCleanupBlocker(
        IDataStore store,
        long sourceOrderId,
        long sourceOrderLineId)
    {
        foreach (var doc in store.GetDocsByOrder(sourceOrderId))
        {
            if (store.GetDocLines(doc.Id).Any(line => line.OrderLineId == sourceOrderLineId))
            {
                return SourceInternalOrderLineCleanupBlocker.RemainingDocLine;
            }

            if (doc.Type != DocType.ProductionReceipt)
            {
                continue;
            }

            var hasActivePalletReference = store.GetProductionPalletsByDoc(doc.Id)
                .Where(pallet => ProductionPalletStatus.IsOperational(pallet.Status))
                .Any(pallet => PalletAppliesToOrderLine(pallet, sourceOrderLineId));
            if (hasActivePalletReference)
            {
                return SourceInternalOrderLineCleanupBlocker.ActiveProductionPallet;
            }
        }

        return SourceInternalOrderLineCleanupBlocker.None;
    }

    private static void ClearStaleInternalReceiptPlanRowsForDepletedSourceLine(
        IDataStore store,
        long sourceOrderId,
        long sourceOrderLineId)
    {
        if (store.GetOrderReceiptPlanLines(sourceOrderId).Any(line => line.OrderLineId == sourceOrderLineId))
        {
            store.ReplaceOrderReceiptPlanLinesForOrderLines(
                sourceOrderId,
                [sourceOrderLineId],
                Array.Empty<OrderReceiptPlanLine>());
        }
    }

    public ProductionPalletPrePlanCoveragePreview GetCustomerPrePlanCoveragePreview(long orderId)
    {
        var order = _data.GetOrder(orderId) ?? throw new InvalidOperationException("Заказ не найден.");
        return BuildPrePlanCoveragePreviewInStore(_data, order);
    }

    internal static ProductionPalletPrePlanCoveragePreview BuildPrePlanCoveragePreviewInStore(IDataStore store, Order order)
    {
        var empty = new ProductionPalletPrePlanCoveragePreview
        {
            OrderId = order.Id,
            OrderRef = order.OrderRef
        };
        if (order.Type != OrderType.Customer
            || order.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
        {
            return empty;
        }

        var scope = BuildPrePlanSafeScope(store, order);
        if (scope.WouldPlanLines.Count == 0)
        {
            return empty;
        }

        var warehouseCandidates = BuildWarehouseHuCandidates(store, order.Id, scope.WouldPlanLines);
        var freeHuLines = BuildFreeWarehouseHuLines(scope.WouldPlanLines, warehouseCandidates);
        var linesAfterDefaultWarehouseCoverage = SubtractSelectedWarehouseCandidates(scope.WouldPlanLines, warehouseCandidates);
        var adoptionProjection = BuildInternalPlanAdoptionProjection(store, order, linesAfterDefaultWarehouseCoverage);
        var internalCandidates = adoptionProjection.Adoptable
            .Select(candidate => new ProductionPalletInternalPlannedHuCandidate
            {
                ProductionPalletId = candidate.ProductionPalletId,
                HuCode = candidate.HuCode,
                SourceOrderId = candidate.SourceOrderId,
                SourceOrderRef = candidate.SourceOrderRef,
                SourcePrdDocId = candidate.SourcePrdDocId,
                SourcePrdDocRef = candidate.SourcePrdDocRef,
                SourceStatus = candidate.SourceStatus,
                TargetOrderLineId = candidate.TargetOrderLineId,
                ItemId = candidate.ItemId,
                ItemName = candidate.ItemName,
                PlannedQty = candidate.PlannedQty,
                ProductionPalletGroup = candidate.ProductionPalletGroup,
                IsMixed = candidate.IsMixed,
                Status = candidate.Status,
                Recommended = true,
                SelectedByDefault = true
            })
            .ToArray();
        return new ProductionPalletPrePlanCoveragePreview
        {
            OrderId = order.Id,
            OrderRef = order.OrderRef,
            HasWarning = scope.WarningLines.Count > 0,
            Message = BuildPrePlanMessage(scope.WarningLines, freeHuLines),
            Lines = scope.WarningLines,
            WouldPlanLineCount = scope.WouldPlanLines.Count,
            SafeLineCount = scope.SafeLineIds.Count,
            WarningLineCount = scope.SkippedLines.Count,
            HasFreeWarehouseHu = warehouseCandidates.Count > 0,
            FreeWarehouseHuLines = freeHuLines,
            WarehouseHuCandidates = warehouseCandidates,
            InternalPlannedHuCandidates = internalCandidates,
            AdoptableInternalPlannedHus = adoptionProjection.Adoptable,
            AdoptionSkippedCandidates = adoptionProjection.Skipped,
            ProjectedAdoptedPalletCount = adoptionProjection.Adoptable.Count,
            ProjectedAdoptedQty = adoptionProjection.Adoptable.Sum(candidate => candidate.PlannedQty),
            ProjectedRemainingQtyAfterAdoption = adoptionProjection.RemainingQtyAfterAdoption
        };
    }

    private sealed record InternalPlanAdoptionProjection(
        IReadOnlyList<ProductionPalletProjectedAdoptionHu> Adoptable,
        IReadOnlyList<ProductionPalletAdoptionSkippedCandidate> Skipped,
        double RemainingQtyAfterAdoption);

    private sealed record PrePlanSafeScope(
        IReadOnlyList<OrderReceiptLine> WouldPlanLines,
        IReadOnlyList<ProductionPalletInternalSupplyWarningLine> WarningLines,
        IReadOnlyList<long> SafeLineIds,
        IReadOnlyList<ProductionPalletPlanSkippedLine> SkippedLines);

    private static PrePlanSafeScope BuildPrePlanSafeScope(IDataStore store, Order order)
    {
        IReadOnlyList<OrderReceiptLine> linesToPlan;
        try
        {
            linesToPlan = GetLinesNeedingPalletAppend(store, order);
        }
        catch (InvalidOperationException)
        {
            // Preview — только советник: ошибки планирования показывает сам POST /plan.
            linesToPlan = Array.Empty<OrderReceiptLine>();
        }

        linesToPlan = linesToPlan
            .Where(line => line.QtyRemaining > QtyTolerance)
            .OrderBy(line => line.OrderLineId)
            .ToArray();
        if (linesToPlan.Count == 0)
        {
            return new PrePlanSafeScope(
                linesToPlan,
                Array.Empty<ProductionPalletInternalSupplyWarningLine>(),
                Array.Empty<long>(),
                Array.Empty<ProductionPalletPlanSkippedLine>());
        }

        var neededItemIds = linesToPlan.Select(line => line.ItemId).ToHashSet();
        var expectedByInternalOrder = new List<(Order InternalOrder, Dictionary<long, double> ExpectedByItem)>();
        foreach (var internalOrder in store.GetOrders()
                     .Where(candidate => candidate.Type == OrderType.Internal
                                         && candidate.Status is not OrderStatus.Shipped
                                             and not OrderStatus.Cancelled
                                             and not OrderStatus.Merged)
                     .OrderBy(candidate => candidate.Id))
        {
            var expectedByItem = new Dictionary<long, double>();
            foreach (var line in OrderReceiptRemainingCalculator.GetRemaining(store, internalOrder)
                         .Where(line => neededItemIds.Contains(line.ItemId) && line.QtyRemaining > QtyTolerance))
            {
                expectedByItem[line.ItemId] = expectedByItem.TryGetValue(line.ItemId, out var current)
                    ? current + line.QtyRemaining
                    : line.QtyRemaining;
            }

            if (expectedByItem.Count > 0)
            {
                expectedByInternalOrder.Add((internalOrder, expectedByItem));
            }
        }

        var itemNamesById = store.GetItems(null).ToDictionary(item => item.Id, item => item.Name);
        var warningLines = new List<ProductionPalletInternalSupplyWarningLine>();
        foreach (var line in linesToPlan)
        {
            foreach (var (internalOrder, expectedByItem) in expectedByInternalOrder)
            {
                if (!expectedByItem.TryGetValue(line.ItemId, out var expectedQty))
                {
                    continue;
                }

                warningLines.Add(new ProductionPalletInternalSupplyWarningLine
                {
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    ItemName = ResolveItemName(line, itemNamesById),
                    WouldPlanQty = line.QtyRemaining,
                    InternalOrderId = internalOrder.Id,
                    InternalOrderRef = internalOrder.OrderRef,
                    InternalOrderStatus = OrderStatusMapper.StatusToString(internalOrder.Status),
                    ExpectedQty = expectedQty
                });
            }
        }

        var directAffectedLineIds = warningLines.Select(line => line.OrderLineId).ToHashSet();
        var orderLinesById = store.GetOrderLines(order.Id).ToDictionary(line => line.Id, line => line);
        var manualMixedLineIds = GetManualMixedOrderLineIds(linesToPlan, orderLinesById);
        var affectedGroups = directAffectedLineIds
            .Where(manualMixedLineIds.Contains)
            .Select(lineId => NormalizePalletGroup(orderLinesById, lineId))
            .Where(group => !string.IsNullOrEmpty(group))
            .ToHashSet(StringComparer.Ordinal);

        var skippedLines = new List<ProductionPalletPlanSkippedLine>();
        var affectedLineIds = new HashSet<long>(directAffectedLineIds);
        foreach (var line in linesToPlan)
        {
            var group = orderLinesById.TryGetValue(line.OrderLineId, out var orderLine)
                ? orderLine.ProductionPalletGroup?.Trim()
                : null;
            if (directAffectedLineIds.Contains(line.OrderLineId))
            {
                skippedLines.Add(new ProductionPalletPlanSkippedLine
                {
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    ItemName = ResolveItemName(line, itemNamesById),
                    ProductionPalletGroup = group,
                    SkippedReason = ProductionPalletPlanSkippedReason.ExpectedInternalSupply,
                    InternalRefs = warningLines.Where(warning => warning.OrderLineId == line.OrderLineId).ToArray()
                });
                continue;
            }

            var normalizedGroup = NormalizePalletGroup(orderLinesById, line.OrderLineId);
            if (manualMixedLineIds.Contains(line.OrderLineId)
                && !string.IsNullOrEmpty(normalizedGroup)
                && affectedGroups.Contains(normalizedGroup))
            {
                affectedLineIds.Add(line.OrderLineId);
                var triggeredBy = linesToPlan
                    .Select(candidate => candidate.OrderLineId)
                    .Where(directAffectedLineIds.Contains)
                    .Where(candidateId => NormalizePalletGroup(orderLinesById, candidateId) == normalizedGroup)
                    .Cast<long?>()
                    .FirstOrDefault();
                skippedLines.Add(new ProductionPalletPlanSkippedLine
                {
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    ItemName = ResolveItemName(line, itemNamesById),
                    ProductionPalletGroup = group,
                    SkippedReason = ProductionPalletPlanSkippedReason.MixedGroupContainsExpectedInternalSupply,
                    TriggeredByOrderLineId = triggeredBy
                });
            }
        }

        var safeLineIds = linesToPlan
            .Select(line => line.OrderLineId)
            .Where(lineId => !affectedLineIds.Contains(lineId))
            .ToArray();
        return new PrePlanSafeScope(linesToPlan, warningLines, safeLineIds, skippedLines);
    }

    private static InternalPlanAdoptionProjection BuildInternalPlanAdoptionProjection(
        IDataStore store,
        Order targetOrder,
        IReadOnlyList<OrderReceiptLine> linesToPlan,
        IReadOnlyCollection<long>? allowedSourceOrderIds = null)
    {
        if (targetOrder.Type != OrderType.Customer || linesToPlan.Count == 0)
        {
            return new InternalPlanAdoptionProjection(
                Array.Empty<ProductionPalletProjectedAdoptionHu>(),
                Array.Empty<ProductionPalletAdoptionSkippedCandidate>(),
                0);
        }

        var remainingByTargetLine = linesToPlan
            .Where(line => line.QtyRemaining > QtyTolerance)
            .ToDictionary(line => line.OrderLineId, line => Math.Max(0, line.QtyRemaining));
        if (remainingByTargetLine.Count == 0)
        {
            return new InternalPlanAdoptionProjection(
                Array.Empty<ProductionPalletProjectedAdoptionHu>(),
                Array.Empty<ProductionPalletAdoptionSkippedCandidate>(),
                0);
        }

        var targetOrderLinesById = store.GetOrderLines(targetOrder.Id)
            .ToDictionary(line => line.Id, line => line);
        var itemNamesById = store.GetItems(null).ToDictionary(item => item.Id, item => item.Name);
        var neededItems = linesToPlan.Select(line => line.ItemId).ToHashSet();
        var allowedSourceOrderIdSet = allowedSourceOrderIds?.ToHashSet();
        var adoptable = new List<ProductionPalletProjectedAdoptionHu>();
        var skipped = new List<ProductionPalletAdoptionSkippedCandidate>();

        foreach (var sourceOrder in store.GetOrders()
                     .Where(order => order.Type == OrderType.Internal)
                     .Where(order => order.Status is OrderStatus.Draft or OrderStatus.InProgress)
                     .Where(order => allowedSourceOrderIdSet == null || allowedSourceOrderIdSet.Contains(order.Id))
                     .OrderBy(order => order.Id))
        {
            foreach (var sourceDoc in store.GetDocsByOrder(sourceOrder.Id)
                         .Where(doc => doc.Type == DocType.ProductionReceipt)
                         .OrderBy(doc => doc.Id))
            {
                var docHasLedger = store.CountLedgerEntriesByDocId(sourceDoc.Id) > 0;
                foreach (var pallet in store.GetProductionPalletsByDoc(sourceDoc.Id)
                             .Where(pallet => GetPalletLines(pallet).Any(line => neededItems.Contains(line.ItemId)))
                             .OrderBy(pallet => pallet.Id))
                {
                    if (sourceDoc.Status == DocStatus.Closed)
                    {
                        skipped.Add(BuildSkippedAdoptionCandidate(sourceOrder, sourceDoc, pallet, null, null, ProductionPalletPlanSkippedReason.SourcePrdClosed));
                        continue;
                    }

                    if (docHasLedger)
                    {
                        skipped.Add(BuildSkippedAdoptionCandidate(sourceOrder, sourceDoc, pallet, null, null, ProductionPalletPlanSkippedReason.SourcePrdHasLedger));
                        continue;
                    }

                    if (!IsEmptyAdoptablePalletStatus(pallet.Status)
                        || string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped.Add(BuildSkippedAdoptionCandidate(sourceOrder, sourceDoc, pallet, null, null, ProductionPalletPlanSkippedReason.StatusNotEligible));
                        continue;
                    }

                    if (pallet.FilledAt.HasValue || pallet.HasComponentProgress)
                    {
                        skipped.Add(BuildSkippedAdoptionCandidate(sourceOrder, sourceDoc, pallet, null, null, ProductionPalletPlanSkippedReason.PartialProgress));
                        continue;
                    }

                    var lines = GetPalletLines(pallet).ToArray();
                    if (lines.Any(line => !line.OrderLineId.HasValue))
                    {
                        skipped.Add(BuildSkippedAdoptionCandidate(sourceOrder, sourceDoc, pallet, null, null, ProductionPalletPlanSkippedReason.MissingSourceOrderLine));
                        continue;
                    }

                    if (!TryBuildProjectedAdoption(
                            sourceOrder,
                            sourceDoc,
                            pallet,
                            lines,
                            targetOrderLinesById,
                            remainingByTargetLine,
                            itemNamesById,
                            out var projected,
                            out var skipReason))
                    {
                        skipped.Add(BuildSkippedAdoptionCandidate(sourceOrder, sourceDoc, pallet, null, null, skipReason));
                        continue;
                    }

                    foreach (var line in projected.Lines)
                    {
                        remainingByTargetLine[line.TargetOrderLineId] -= line.PlannedQty;
                    }

                    adoptable.Add(projected);
                }
            }
        }

        return new InternalPlanAdoptionProjection(
            adoptable,
            skipped,
            remainingByTargetLine.Values.Sum(qty => Math.Max(0, qty)));
    }

    private static bool TryBuildProjectedAdoption(
        Order sourceOrder,
        Doc sourceDoc,
        ProductionPallet pallet,
        IReadOnlyList<ProductionPalletComponentLine> sourceLines,
        IReadOnlyDictionary<long, OrderLine> targetOrderLinesById,
        IReadOnlyDictionary<long, double> remainingByTargetLine,
        IReadOnlyDictionary<long, string> itemNamesById,
        out ProductionPalletProjectedAdoptionHu projected,
        out string skipReason)
    {
        projected = new ProductionPalletProjectedAdoptionHu();
        skipReason = string.Empty;
        var mappedLines = new List<ProductionPalletProjectedAdoptionLine>();
        var targetLineIds = new List<long>();

        if (sourceLines.Count > 1)
        {
            var candidates = sourceLines
                .Select(sourceLine => FindTargetLineForItem(sourceLine.ItemId, targetOrderLinesById, remainingByTargetLine, requireMixedGroup: true))
                .ToArray();
            if (candidates.Any(candidate => candidate == null))
            {
                skipReason = ProductionPalletPlanSkippedReason.MixedGroupMismatch;
                return false;
            }

            var group = NormalizePalletGroup(targetOrderLinesById, candidates[0]!.Id);
            if (string.IsNullOrWhiteSpace(group)
                || candidates.Any(candidate => NormalizePalletGroup(targetOrderLinesById, candidate!.Id) != group))
            {
                skipReason = ProductionPalletPlanSkippedReason.MixedGroupMismatch;
                return false;
            }

            foreach (var sourceLine in sourceLines)
            {
                var targetLine = candidates.First(candidate => candidate!.ItemId == sourceLine.ItemId)!;
                if (!HasEnoughRemaining(remainingByTargetLine, targetLine.Id, sourceLine.PlannedQty))
                {
                    skipReason = ProductionPalletPlanSkippedReason.QtyExceedsShortage;
                    return false;
                }

                mappedLines.Add(BuildProjectedLine(sourceLine, targetLine, itemNamesById));
                targetLineIds.Add(targetLine.Id);
            }
        }
        else
        {
            var sourceLine = sourceLines[0];
            var targetLine = FindTargetLineForItem(sourceLine.ItemId, targetOrderLinesById, remainingByTargetLine, requireMixedGroup: false);
            if (targetLine == null)
            {
                skipReason = ProductionPalletPlanSkippedReason.MixedGroupMismatch;
                return false;
            }

            if (!HasEnoughRemaining(remainingByTargetLine, targetLine.Id, sourceLine.PlannedQty))
            {
                skipReason = ProductionPalletPlanSkippedReason.QtyExceedsShortage;
                return false;
            }

            mappedLines.Add(BuildProjectedLine(sourceLine, targetLine, itemNamesById));
            targetLineIds.Add(targetLine.Id);
        }

        var targetGroup = targetLineIds.Count > 0 ? NormalizePalletGroup(targetOrderLinesById, targetLineIds[0]) : string.Empty;
        projected = new ProductionPalletProjectedAdoptionHu
        {
            ProductionPalletId = pallet.Id,
            HuCode = pallet.HuCode,
            SourceOrderId = sourceOrder.Id,
            SourceOrderRef = sourceOrder.OrderRef,
            SourcePrdDocId = sourceDoc.Id,
            SourcePrdDocRef = sourceDoc.DocRef,
            SourceStatus = OrderStatusMapper.StatusToString(sourceOrder.Status),
            TargetOrderLineId = targetLineIds.Count == 1 ? targetLineIds[0] : null,
            ItemId = pallet.ItemId,
            ItemName = string.IsNullOrWhiteSpace(pallet.ItemName)
                ? itemNamesById.GetValueOrDefault(pallet.ItemId, string.Empty)
                : pallet.ItemName,
            PlannedQty = mappedLines.Sum(line => line.PlannedQty),
            ProductionPalletGroup = string.IsNullOrWhiteSpace(targetGroup) ? null : targetGroup,
            IsMixed = sourceLines.Count > 1,
            Status = pallet.Status,
            WillRequireReprint = string.Equals(
                                     pallet.Status,
                                     ProductionPalletStatus.Printed,
                                     StringComparison.OrdinalIgnoreCase)
                                 || pallet.PrintedAt.HasValue,
            Lines = mappedLines
        };
        return true;
    }

    private static OrderLine? FindTargetLineForItem(
        long itemId,
        IReadOnlyDictionary<long, OrderLine> targetOrderLinesById,
        IReadOnlyDictionary<long, double> remainingByTargetLine,
        bool requireMixedGroup)
    {
        return targetOrderLinesById.Values
            .Where(line => line.ItemId == itemId)
            .Where(line => remainingByTargetLine.TryGetValue(line.Id, out var remaining) && remaining > QtyTolerance)
            .Where(line => requireMixedGroup == !string.IsNullOrWhiteSpace(line.ProductionPalletGroup))
            .OrderBy(line => line.Id)
            .FirstOrDefault();
    }

    private static bool HasEnoughRemaining(
        IReadOnlyDictionary<long, double> remainingByTargetLine,
        long targetOrderLineId,
        double qty)
    {
        return remainingByTargetLine.TryGetValue(targetOrderLineId, out var remaining)
               && qty > QtyTolerance
               && qty <= remaining + QtyTolerance;
    }

    private static ProductionPalletProjectedAdoptionLine BuildProjectedLine(
        ProductionPalletComponentLine sourceLine,
        OrderLine targetLine,
        IReadOnlyDictionary<long, string> itemNamesById)
    {
        return new ProductionPalletProjectedAdoptionLine
        {
            SourceOrderLineId = sourceLine.OrderLineId!.Value,
            TargetOrderLineId = targetLine.Id,
            DocLineId = sourceLine.DocLineId,
            ItemId = sourceLine.ItemId,
            ItemName = string.IsNullOrWhiteSpace(sourceLine.ItemName)
                ? itemNamesById.GetValueOrDefault(sourceLine.ItemId, string.Empty)
                : sourceLine.ItemName,
            PlannedQty = Math.Max(0, sourceLine.PlannedQty)
        };
    }

    private static ProductionPalletAdoptionSkippedCandidate BuildSkippedAdoptionCandidate(
        Order sourceOrder,
        Doc sourceDoc,
        ProductionPallet pallet,
        long? targetOrderLineId,
        string? productionPalletGroup,
        string skipReason)
    {
        return new ProductionPalletAdoptionSkippedCandidate
        {
            ProductionPalletId = pallet.Id,
            HuCode = pallet.HuCode,
            SourceOrderId = sourceOrder.Id,
            SourceOrderRef = sourceOrder.OrderRef,
            SourcePrdDocId = sourceDoc.Id,
            SourcePrdDocRef = sourceDoc.DocRef,
            SourceStatus = OrderStatusMapper.StatusToString(sourceOrder.Status),
            TargetOrderLineId = targetOrderLineId,
            ItemId = pallet.ItemId,
            ItemName = pallet.ItemName,
            PlannedQty = pallet.PlannedQty,
            ProductionPalletGroup = productionPalletGroup,
            IsMixed = pallet.IsMixedPallet,
            Status = pallet.Status,
            SkipReason = skipReason
        };
    }

    private static bool IsEmptyAdoptablePalletStatus(string status)
    {
        return string.Equals(status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePalletGroup(IReadOnlyDictionary<long, OrderLine> orderLinesById, long orderLineId)
    {
        return orderLinesById.TryGetValue(orderLineId, out var orderLine)
               && !string.IsNullOrWhiteSpace(orderLine.ProductionPalletGroup)
            ? orderLine.ProductionPalletGroup!.Trim().ToUpperInvariant()
            : string.Empty;
    }

    private static string ResolveItemName(OrderReceiptLine line, IReadOnlyDictionary<long, string> itemNamesById)
    {
        return !string.IsNullOrWhiteSpace(line.ItemName)
            ? line.ItemName
            : itemNamesById.GetValueOrDefault(line.ItemId, string.Empty);
    }

    private static IReadOnlyList<ProductionPalletWarehouseHuCandidate> BuildWarehouseHuCandidates(
        IDataStore store,
        long orderId,
        IReadOnlyList<OrderReceiptLine> wouldPlanLines)
    {
        if (store is not IOptimizedHuReservationCandidatesStore || wouldPlanLines.Count == 0)
        {
            return Array.Empty<ProductionPalletWarehouseHuCandidate>();
        }

        var reservedHu = store.GetOrderReceiptPlanLines(orderId)
            .Select(planLine => planLine.ToHu?.Trim())
            .Where(hu => !string.IsNullOrWhiteSpace(hu))
            .Select(hu => hu!)
            .ToArray();
        var result = new HuReservationCandidatesService(store).Build(new HuReservationCandidatesQuery
        {
            OrderId = orderId,
            Lines = wouldPlanLines
                .Select(line => new HuReservationCandidatesLineQuery
                {
                    ClientLineKey = line.OrderLineId.ToString(CultureInfo.InvariantCulture),
                    OrderLineId = line.OrderLineId,
                    ItemId = line.ItemId,
                    QtyOrdered = line.QtyRemaining
                })
                .ToArray(),
            ExcludeHuCodes = reservedHu
        });

        return result.Lines
            .SelectMany(line => line.Candidates.Select(candidate => (Line: line, Candidate: candidate)))
            .Where(entry => string.Equals(entry.Candidate.Source, OrderHuReservationApplyService.SourceLedgerStock, StringComparison.OrdinalIgnoreCase))
            .Where(entry => entry.Candidate.Qty > QtyTolerance)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Candidate.HuCode))
            .GroupBy(entry => (entry.Line.OrderLineId, HuCode: entry.Candidate.HuCode.Trim().ToUpperInvariant()))
            .Select(group => group.First())
            .Select(entry =>
            {
                var sourceRef = entry.Candidate.ReservedByOrderRef
                                ?? entry.Candidate.SourceOrderRef
                                ?? entry.Candidate.SourcePrdRef
                                ?? entry.Candidate.Note
                                ?? string.Empty;
                return new ProductionPalletWarehouseHuCandidate
                {
                    HuCode = entry.Candidate.HuCode,
                    ItemId = entry.Line.ItemId,
                    ItemName = wouldPlanLines.FirstOrDefault(line => line.OrderLineId == entry.Line.OrderLineId)?.ItemName ?? string.Empty,
                    TargetOrderLineId = entry.Line.OrderLineId ?? 0,
                    Qty = entry.Candidate.Qty,
                    Status = "LEDGER_STOCK",
                    SourceRef = sourceRef,
                    Recommended = entry.Candidate.AutoSelected,
                    SelectedByDefault = entry.Candidate.AutoSelected
                };
            })
            .Where(candidate => candidate.TargetOrderLineId > 0)
            .OrderBy(candidate => candidate.TargetOrderLineId)
            .ThenBy(candidate => candidate.HuCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<ProductionPalletPrePlanFreeHuLine> BuildFreeWarehouseHuLines(
        IReadOnlyList<OrderReceiptLine> wouldPlanLines,
        IReadOnlyList<ProductionPalletWarehouseHuCandidate> warehouseCandidates)
    {
        var freeByLine = warehouseCandidates
            .GroupBy(candidate => candidate.TargetOrderLineId)
            .ToDictionary(
                group => group.Key,
                group => (Count: group.Count(), Qty: group.Sum(candidate => Math.Max(0, candidate.Qty))));
        return wouldPlanLines
            .Where(line => freeByLine.ContainsKey(line.OrderLineId))
            .Select(line => new ProductionPalletPrePlanFreeHuLine
            {
                OrderLineId = line.OrderLineId,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                WouldPlanQty = line.QtyRemaining,
                FreeHuCount = freeByLine[line.OrderLineId].Count,
                FreeHuQty = freeByLine[line.OrderLineId].Qty
            })
            .ToArray();
    }

    private static IReadOnlyList<OrderReceiptLine> SubtractSelectedWarehouseCandidates(
        IReadOnlyList<OrderReceiptLine> wouldPlanLines,
        IReadOnlyList<ProductionPalletWarehouseHuCandidate> warehouseCandidates)
    {
        var selectedQtyByLine = warehouseCandidates
            .Where(candidate => candidate.SelectedByDefault && string.IsNullOrWhiteSpace(candidate.DisabledReason))
            .GroupBy(candidate => candidate.TargetOrderLineId)
            .ToDictionary(group => group.Key, group => group.Sum(candidate => Math.Max(0, candidate.Qty)));
        if (selectedQtyByLine.Count == 0)
        {
            return wouldPlanLines;
        }

        return wouldPlanLines
            .Select(line =>
            {
                var selectedQty = selectedQtyByLine.TryGetValue(line.OrderLineId, out var qty) ? qty : 0d;
                var remaining = Math.Max(0, line.QtyRemaining - selectedQty);
                return new OrderReceiptLine
                {
                    OrderLineId = line.OrderLineId,
                    OrderId = line.OrderId,
                    ItemId = line.ItemId,
                    ItemName = line.ItemName,
                    QtyOrdered = line.QtyOrdered,
                    QtyReceived = line.QtyReceived,
                    QtyRemaining = remaining,
                    ProductionPurpose = line.ProductionPurpose,
                    ToLocationId = line.ToLocationId,
                    ToLocation = line.ToLocation,
                    ToHu = line.ToHu,
                    SortOrder = line.SortOrder
                };
            })
            .Where(line => line.QtyRemaining > QtyTolerance)
            .ToArray();
    }

    private static string BuildPrePlanMessage(
        IReadOnlyList<ProductionPalletInternalSupplyWarningLine> warningLines,
        IReadOnlyList<ProductionPalletPrePlanFreeHuLine> freeHuLines)
    {
        var message = new StringBuilder();
        if (warningLines.Count > 0)
        {
            message.Append("По этим позициям уже ожидается выпуск во внутреннем заказе:");
            foreach (var warningLine in warningLines)
            {
                var status = OrderStatusMapper.StatusFromString(warningLine.InternalOrderStatus);
                var statusDisplay = status.HasValue
                    ? OrderStatusMapper.StatusToDisplayName(status.Value, OrderType.Internal)
                    : warningLine.InternalOrderStatus;
                message.Append(Environment.NewLine)
                    .Append($"{warningLine.ItemName} — к планированию {FormatWarningQty(warningLine.WouldPlanQty)}, " +
                            $"ожидается {FormatWarningQty(warningLine.ExpectedQty)} (заказ {warningLine.InternalOrderRef}, {statusDisplay})");
            }
        }

        if (freeHuLines.Count > 0)
        {
            if (message.Length > 0)
            {
                message.Append(Environment.NewLine).Append(Environment.NewLine);
            }

            message.Append("По части позиций есть свободные складские HU, их можно привязать вместо производства:");
            foreach (var freeHuLine in freeHuLines)
            {
                message.Append(Environment.NewLine)
                    .Append($"{freeHuLine.ItemName} — свободно {FormatWarningQty(freeHuLine.FreeHuQty)} в {freeHuLine.FreeHuCount} HU");
            }
        }

        return message.ToString();
    }

    private static string FormatWarningQty(double value)
    {
        return value.ToString("0.###", CultureInfo.CurrentCulture);
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
        var survivingReprintRequiredHuCodes = Array.Empty<string>();
        ProductionPalletPlanCleanupCounts cleanup = null!;
        _data.ExecuteInTransaction(store =>
        {
            if (!store.LockOrdersForUpdate(new[] { order.Id }))
            {
                throw new InvalidOperationException("Заказ не найден.");
            }
            var currentOrder = store.GetOrder(order.Id)
                               ?? throw new InvalidOperationException("Заказ не найден.");
            if (currentOrder.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
            {
                throw new InvalidOperationException("Заказ недоступен для удаления плана паллет.");
            }

            var docsById = store.GetDocsByOrder(order.Id)
                .Where(doc => doc.Type == DocType.ProductionReceipt)
                .ToDictionary(doc => doc.Id, doc => doc);
            var selected = docsById.Values
                .SelectMany(doc => store.GetProductionPalletsByDoc(doc.Id))
                .Where(pallet => requestedIds.Contains(pallet.Id))
                .Where(pallet => pallet.OrderId == order.Id)
                .Where(pallet => docsById.TryGetValue(pallet.PrdDocId, out var doc) && doc.Status != DocStatus.Closed)
                .Where(IsRemovableFuturePlanPallet)
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

            if (cleanup.RemovedPalletCount > 0)
            {
                new OrderService(store).RefreshPersistedStatus(order.Id);
                var hasSurvivingProductionPallets = store.GetDocsByOrder(order.Id)
                    .Where(doc => doc.Type == DocType.ProductionReceipt)
                    .SelectMany(doc => store.GetProductionPalletsByDoc(doc.Id))
                    .Any(pallet => pallet.OrderId == order.Id && IsPrintableProductionPalletStatus(pallet.Status));
                if (hasSurvivingProductionPallets)
                {
                    survivingReprintRequiredHuCodes = new ProductionPalletService(store).GetPrintRows(order.Id)
                        .Where(row => string.Equals(
                                          row.SourceType,
                                          ProductionPalletPrintSourceType.ProductionPallet,
                                          StringComparison.OrdinalIgnoreCase)
                                      && row.ReprintRequired)
                        .Select(row => row.HuCode)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
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
            SkippedPalletIds = skippedPalletIds,
            SurvivingReprintRequiredHuCodes = survivingReprintRequiredHuCodes
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
        var markingGenerated = order.MarkingExcelGeneratedAt.HasValue
                               || order.MarkingPrintedAt.HasValue;
        var rows = docs
            .SelectMany(doc => _data.GetProductionPalletsByDoc(doc.Id))
            .Where(pallet => pallet.OrderId == orderId)
            .Where(pallet => ProductionPalletStatus.IsOperational(pallet.Status))
            .Where(HasOrderLineOwnership)
            .Select(pallet =>
            {
                docsById.TryGetValue(pallet.PrdDocId, out var doc);
                var isClosedDoc = doc?.Status == DocStatus.Closed;
                var isFilled = string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase);
                var isSelectable = !isClosedDoc && IsRemovableFuturePlanPallet(pallet);
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
            var orderIds = new[] { sourceInternalOrderId, targetCustomerOrderId }
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            if (!store.LockOrdersForUpdate(orderIds))
            {
                throw new ProductionPalletPlanAdoptionException(
                    "ORDER_NOT_FOUND",
                    "Один из заказов переноса плана не найден.");
            }

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
                .Where(pallet => ProductionPalletStatus.IsOperational(pallet.Status))
                .ToList();
            if (sourcePallets.Count == 0)
            {
                throw new ProductionPalletPlanAdoptionException("SOURCE_HAS_NO_ACTIVE_PALLETS", "У внутреннего выпуска нет активных паллет для переноса.");
            }

            if (sourcePallets.Any(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ProductionPalletPlanAdoptionException("SOURCE_HAS_FILLED_PALLETS", "Нельзя перенести план паллет: есть уже наполненные паллеты.");
            }

            if (sourcePallets.Any(pallet => pallet.HasComponentProgress))
            {
                throw new ProductionPalletPlanAdoptionException("SOURCE_HAS_PARTIAL_PALLETS", "Нельзя перенести план паллет: есть частично наполненные микс-паллеты.");
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
        var workItems = _data.GetActiveProductionPalletWorkItems();
        var workOrderIds = workItems
            .Where(item => item.OrderId.HasValue && item.Summary.RemainingPalletCount > 0)
            .Select(item => item.OrderId!.Value)
            .ToHashSet();

        var candidateOrderIds = workOrderIds.ToArray();
        if (candidateOrderIds.Length == 0)
        {
            return Array.Empty<ProductionFillingOrder>();
        }

        var ordersById = LoadOrdersById(candidateOrderIds);
        var palletsByOrderId = LoadPalletsByOrderId(candidateOrderIds);
        var orderLinesByOrderId = LoadOrderLinesByOrderId(candidateOrderIds);
        var docsByOrderId = LoadDocsByOrderId(candidateOrderIds);
        var productionDocIds = docsByOrderId.Values
            .SelectMany(docs => docs)
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .Select(doc => doc.Id)
            .Distinct()
            .ToArray();
        var docLinesByDocId = LoadDocLinesByDocId(productionDocIds);
        var huOrderContextRows = ordersById.Values.Any(order => order.Type == OrderType.Internal)
            ? LoadHuOrderContextRows()
            : Array.Empty<HuOrderContextRow>();
        var shipmentProgressByOrderId = LoadShipmentProgressByOrderId(candidateOrderIds, orderLinesByOrderId);
        var completions = LoadFillingCompletions(candidateOrderIds);

        var rows = new Dictionary<long, ProductionFillingOrder>();
        foreach (var group in workItems
                     .Where(item => item.OrderId.HasValue && item.Summary.RemainingPalletCount > 0)
                     .GroupBy(item => item.OrderId!.Value))
        {
            if (!ordersById.TryGetValue(group.Key, out var order) || IsTerminalFillingOrder(order))
            {
                continue;
            }

            var row = BuildFillingOrderFromPreloaded(
                order,
                group.ToList(),
                palletsByOrderId,
                orderLinesByOrderId,
                docsByOrderId,
                docLinesByDocId,
                huOrderContextRows,
                completions,
                shipmentProgressByOrderId.GetValueOrDefault(order.Id));
            if (row != null && !ShouldExcludeFromFillingList(row))
            {
                rows[row.OrderId] = row;
            }
        }

        return rows.Values
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
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Merged)
        {
            throw new InvalidOperationException(order.Status == OrderStatus.Merged
                ? "Заказ объединён с другим заказом. Выпуск по нему не требуется."
                : "Заказ недоступен для наполнения.");
        }

        var orderIds = new[] { orderId };
        var pallets = LoadPalletsByOrderId(orderIds).GetValueOrDefault(orderId)
                      ?? Array.Empty<ProductionPallet>();
        var orderLines = LoadOrderLinesByOrderId(orderIds).GetValueOrDefault(orderId)
                         ?? Array.Empty<OrderLine>();
        var docs = LoadDocsByOrderId(orderIds).GetValueOrDefault(orderId)
                   ?? Array.Empty<Doc>();
        var productionDocIds = docs
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .Select(doc => doc.Id)
            .ToArray();
        var docLinesByDocId = LoadDocLinesByDocId(productionDocIds);
        var huOrderContextRows = order.Type == OrderType.Internal
            ? LoadHuOrderContextRows()
            : Array.Empty<HuOrderContextRow>();
        var eligibilitySnapshot = BuildFillingEligibilitySnapshot(
            order,
            orderLines,
            docs,
            docLinesByDocId,
            pallets,
            huOrderContextRows);
        var fillingPallets = BuildOrderOwnedPalletViews(
            orderId,
            pallets,
            orderLines.ToDictionary(line => line.Id));
        var openDoc = FindPreparedOpenProductionReceiptForFilling(
            docs,
            fillingPallets,
            requireRemaining: false);
        if (openDoc == null)
        {
            var progress = BuildOperationProgress(_data, orderId, fillingPallets);
            if (progress.CanClose && fillingPallets.Count > 0)
            {
                var completedDoc = docs.First(doc => doc.Id == fillingPallets[0].PrdDocId);
                return BuildFillingContext(
                    order,
                    completedDoc,
                    fillingPallets,
                    pallets,
                    eligibilitySnapshot);
            }

            if (HasCompletedPalletizedProduction(fillingPallets))
            {
                throw new InvalidOperationException("Выпуск по заказу уже завершён. Нет паллет к наполнению.");
            }

            throw new InvalidOperationException("Для заказа не сформирован план паллет. Сформируйте и напечатайте паллетные этикетки перед наполненением.");
        }

        return BuildFillingContext(order, openDoc, fillingPallets, pallets, eligibilitySnapshot);
    }

    public ProductionFillingCompleteResult CompleteFilling(long orderId, string? deviceId)
    {
        ProductionFillingCompleteResult? result = null;
        _data.ExecuteInTransaction(store =>
        {
            var pallets = BuildFillingPalletViews(store, orderId, GetProductionPalletsByOrder(store, orderId));
            var progress = BuildOperationProgress(store, orderId, pallets);
            if (!progress.CanClose)
            {
                result = new ProductionFillingCompleteResult
                {
                    Success = false,
                    Error = "FILLING_INCOMPLETE",
                    Message = "Не все обязательные паллеты наполнены."
                };
                return;
            }

            var existing = store.GetProductionFillingCompletion(orderId, progress.OperationFingerprint);
            var completedAt = existing?.CompletedAt ?? DateTime.Now;
            if (existing == null)
            {
                store.AddProductionFillingCompletion(new ProductionFillingCompletion
                {
                    OrderId = orderId,
                    OperationFingerprint = progress.OperationFingerprint,
                    CompletedAt = completedAt,
                    CompletedByDeviceId = deviceId
                });
            }

            result = new ProductionFillingCompleteResult
            {
                Success = true,
                Message = existing == null ? "Операция наполнения завершена." : "Операция наполнения уже завершена.",
                ClosedAt = completedAt
            };
        });

        if (result?.Success == true)
        {
            result = new ProductionFillingCompleteResult
            {
                Success = true,
                Message = result.Message,
                ClosedAt = result.ClosedAt,
                Context = GetFillingContext(orderId)
            };
        }
        return result ?? new ProductionFillingCompleteResult { Success = false, Error = "FILLING_COMPLETE_FAILED", Message = "Не удалось завершить наполнение." };
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
                planLine.QtyPlanned));
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
        var locationsById = _data.GetLocations().ToDictionary(location => location.Id, location => location.Code);
        var locationResolver = HuCurrentLocationResolver.Create(_data.GetHuStockRows(), locationsById);

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
                StorageConditions = item?.StorageConditions ?? string.Empty,
                Qty = entry.Qty,
                Uom = uom,
                PalletNo = index + 1,
                PalletCount = entries.Count,
                StoragePlace = locationResolver.Resolve(entry.HuCode, entry.ItemId) ?? string.Empty,
                Lines = new[] { printLine },
                Composition = $"{itemName} - {FormatQty(entry.Qty)} {uom}",
                Status = "BOUND"
            });
        }

        return rows;
    }

    private static ProductionPalletPrintRow ApplyLabelFingerprint(
        ProductionPalletPrintRow row,
        ProductionPallet pallet)
    {
        var fingerprint = ProductionPalletLabelFingerprint.Compute(row);
        var claimsPrintedLifecycle = pallet.PrintedAt.HasValue
                                     || string.Equals(
                                         pallet.Status,
                                         ProductionPalletStatus.Printed,
                                         StringComparison.OrdinalIgnoreCase);
        var hasVerifiedFingerprint = pallet.PrintedAt.HasValue
                                     && pallet.PrintedLabelFingerprintVersion == ProductionPalletLabelContract.FingerprintVersion
                                     && !string.IsNullOrWhiteSpace(pallet.PrintedLabelFingerprint);
        var verifiedFingerprintMismatch = hasVerifiedFingerprint
                                          && !string.Equals(
                                              fingerprint,
                                              pallet.PrintedLabelFingerprint,
                                              StringComparison.OrdinalIgnoreCase);
        var reprintRequired = claimsPrintedLifecycle && (!hasVerifiedFingerprint || verifiedFingerprintMismatch);
        var labelState = !claimsPrintedLifecycle
            ? ProductionPalletLabelState.Unprinted
            : !hasVerifiedFingerprint
                ? ProductionPalletLabelState.LegacyUnverified
                : verifiedFingerprintMismatch
                    ? ProductionPalletLabelState.ReprintRequired
                    : ProductionPalletLabelState.Current;

        return new ProductionPalletPrintRow
        {
            SourceType = row.SourceType,
            PalletId = row.PalletId,
            OrderId = row.OrderId,
            OrderRef = row.OrderRef,
            ClientName = row.ClientName,
            PrdDocId = row.PrdDocId,
            PrdRef = row.PrdRef,
            HuCode = row.HuCode,
            ItemId = row.ItemId,
            ItemName = row.ItemName,
            Brand = row.Brand,
            StorageConditions = row.StorageConditions,
            Qty = row.Qty,
            Uom = row.Uom,
            PalletNo = row.PalletNo,
            PalletCount = row.PalletCount,
            StoragePlace = row.StoragePlace,
            ProductionDate = row.ProductionDate,
            Comment = row.Comment,
            IsMixedPallet = row.IsMixedPallet,
            Composition = row.Composition,
            Lines = row.Lines,
            Status = row.Status,
            LabelContract = ProductionPalletLabelContract.FingerprintV1,
            LabelFingerprint = fingerprint,
            ReprintRequired = reprintRequired,
            LabelState = labelState
        };
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
                StorageConditions = isMixed ? string.Empty : item.StorageConditions ?? string.Empty,
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

        var palletsById = pallets.ToDictionary(pallet => pallet.Id);
        return rows
            .Select(row => ApplyLabelFingerprint(row, palletsById[row.PalletId]))
            .ToArray();
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

    public int AcknowledgePrintedLabels(
        long orderId,
        IReadOnlyCollection<ProductionPalletLabelAcknowledgement> acknowledgements,
        DateTime printedAt)
    {
        var supplied = acknowledgements
            .Where(row => row.PalletId > 0)
            .Select(row => new ProductionPalletLabelAcknowledgement(
                row.PalletId,
                (row.ExpectedLabelFingerprint ?? string.Empty).Trim().ToLowerInvariant()))
            .ToArray();
        if (supplied.GroupBy(row => row.PalletId).Any(group => group.Count() != 1))
        {
            throw new ProductionPalletLabelContractException(
                ProductionPalletLabelContract.UpgradeRequired,
                "Каждая production-pallet HU должна подтверждаться ровно одним fingerprint.");
        }

        var requested = supplied
            .OrderBy(row => row.PalletId)
            .ToArray();
        if (requested.Length == 0
            || requested.Any(row => string.IsNullOrWhiteSpace(row.ExpectedLabelFingerprint)))
        {
            throw new ProductionPalletLabelContractException(
                ProductionPalletLabelContract.UpgradeRequired,
                "Для подтверждения production-pallet печати требуется fingerprint каждой этикетки.");
        }

        var updated = 0;
        _data.ExecuteInTransaction(store =>
        {
            if (!store.LockOrdersForUpdate(new[] { orderId }))
            {
                throw new InvalidOperationException("Заказ не найден.");
            }

            var rows = new ProductionPalletService(store).GetPrintRows(orderId)
                .Where(row => string.Equals(
                    row.SourceType,
                    ProductionPalletPrintSourceType.ProductionPallet,
                    StringComparison.OrdinalIgnoreCase))
                .ToDictionary(row => row.PalletId);
            foreach (var acknowledgement in requested)
            {
                if (!rows.TryGetValue(acknowledgement.PalletId, out var current))
                {
                    throw new InvalidOperationException("Выбранные паллеты не найдены в плане заказа.");
                }

                if (!string.Equals(
                        current.LabelFingerprint,
                        acknowledgement.ExpectedLabelFingerprint.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ProductionPalletLabelContractException(
                        ProductionPalletLabelContract.Stale,
                        $"Payload этикетки HU '{current.HuCode}' изменился. Обновите данные и перепечатайте этикетку.");
                }
            }

            updated = store.AcknowledgeProductionPalletLabels(orderId, requested, printedAt);
            if (updated != requested.Length)
            {
                throw new ProductionPalletLabelContractException(
                    ProductionPalletLabelContract.Stale,
                    "Состояние выбранных паллет изменилось до подтверждения печати.");
            }
        });
        return updated;
    }

    // Unified classification used identically by Scan and Fill so both report the same
    // outcome for the same pallet. Priority: actual order + status drive the result.
    // A prdDocId mismatch within the SAME order is never treated as "another order".
    // Returns a (code, message) failure, or null when the pallet is fillable or already
    // filled for the requested order (then alreadyFilledForRequestedOrder is set).
    private (string Code, string Message)? ClassifyPalletForFilling(
        IDataStore store,
        long? requestedOrderId,
        ProductionPallet pallet,
        out bool alreadyFilledForRequestedOrder)
    {
        alreadyFilledForRequestedOrder = false;
        var isFilled = string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase);
        var belongsToRequested = !requestedOrderId.HasValue || pallet.OrderId == requestedOrderId.Value;

        if (isFilled && belongsToRequested)
        {
            alreadyFilledForRequestedOrder = true;
            return null;
        }

        if (isFilled)
        {
            return (ProductionFillingErrorCodes.PalletAlreadyFilledInOtherOrder,
                $"Паллета уже наполнена по другому заказу №{ResolveOrderRef(store, pallet.OrderId)}.");
        }

        if (!belongsToRequested)
        {
            return (ProductionFillingErrorCodes.PalletBelongsToAnotherOrder,
                $"Эта паллета относится к другому заказу №{ResolveOrderRef(store, pallet.OrderId)}.");
        }

        if (IsCancelledPallet(pallet))
        {
            return (ProductionFillingErrorCodes.PalletCancelled, "Паллета отменена и не может быть наполнена.");
        }

        if (string.Equals(pallet.Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase)
            && pallet.OrderId.HasValue)
        {
            var labelRow = new ProductionPalletService(store).GetPrintRows(pallet.OrderId.Value)
                .FirstOrDefault(row => string.Equals(
                                           row.SourceType,
                                           ProductionPalletPrintSourceType.ProductionPallet,
                                           StringComparison.OrdinalIgnoreCase)
                                       && row.PalletId == pallet.Id);
            if (labelRow == null
                || string.Equals(
                    labelRow.LabelState,
                    ProductionPalletLabelState.LegacyUnverified,
                    StringComparison.OrdinalIgnoreCase))
            {
                return (ProductionFillingErrorCodes.LabelStateUnverified,
                    $"Для HU {pallet.HuCode} нет проверяемого fingerprint физической этикетки. Перепечатайте этикетку в обновлённом WPF.");
            }

            if (labelRow.ReprintRequired)
            {
                return (ProductionFillingErrorCodes.LabelReprintRequired,
                    $"Этикетка HU {pallet.HuCode} устарела после изменения заказа/плана. Перепечатайте её перед наполнением.");
            }
        }

        return null;
    }

    private static string ResolveOrderRef(IDataStore store, long? orderId)
    {
        if (!orderId.HasValue)
        {
            return string.Empty;
        }

        try
        {
            return store.GetOrder(orderId.Value)?.OrderRef ?? string.Empty;
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return string.Empty;
        }
    }

    public ProductionPalletScanResult Scan(long? orderId, long? prdDocId, string? huCode)
    {
        var normalizedHu = NormalizeHu(huCode);
        if (string.IsNullOrWhiteSpace(normalizedHu))
        {
            return ProductionPalletScanResult.Failure(
                ProductionFillingErrorCodes.HuRequired, "Укажите код паллеты.");
        }

        var pallet = _data.GetProductionPalletByHu(normalizedHu);
        if (pallet == null)
        {
            return ProductionPalletScanResult.Failure(
                ProductionFillingErrorCodes.PalletNotFound, "Паллета не найдена в плане выпуска.");
        }

        var classification = ClassifyPalletForFilling(_data, orderId, pallet, out var alreadyFilledForRequestedOrder);
        if (classification.HasValue)
        {
            return ProductionPalletScanResult.Failure(classification.Value.Code, classification.Value.Message);
        }

        var doc = _data.GetDoc(pallet.PrdDocId);
        if (doc == null || doc.Type != DocType.ProductionReceipt)
        {
            return ProductionPalletScanResult.Failure(
                ProductionFillingErrorCodes.PalletPlanInvalid, "Документ выпуска не найден.");
        }

        if (alreadyFilledForRequestedOrder)
        {
            // Already filled for THIS order: report "already filled" regardless of a stale
            // prdDocId from the order context or the PRD having been auto-closed / moved to
            // a dedicated document. A PRD mismatch must not become an "another order" error.
            var filledOrder = pallet.OrderId.HasValue ? _data.GetOrder(pallet.OrderId.Value) : null;
            return new ProductionPalletScanResult
            {
                Success = true,
                Error = ProductionFillingErrorCodes.PalletAlreadyFilled,
                ErrorMessage = "Паллета уже наполнена.",
                AlreadyFilled = true,
                OrderId = pallet.OrderId,
                OrderRef = filledOrder?.OrderRef ?? doc.OrderRef,
                PrdDocId = doc.Id,
                PrdDocRef = doc.DocRef,
                PalletId = pallet.Id,
                HuCode = pallet.HuCode,
                PalletStatus = pallet.Status,
                EffectiveStatus = pallet.EffectiveStatus,
                CanFill = pallet.CanFill,
                Document = BuildFillingDocument(doc.Id, _data.GetProductionPalletsByDoc(doc.Id), pallet.OrderId)
            };
        }

        if (doc.Status == DocStatus.Closed)
        {
            return ProductionPalletScanResult.Failure(
                ProductionFillingErrorCodes.PrdAlreadyClosed, "Документ выпуска уже закрыт.");
        }

        var lineGuardFailure = ValidateFillingPalletLineGuards(
            _data,
            pallet,
            doc,
            requireDestinationLocation: false);
        if (lineGuardFailure.HasValue)
        {
            return ProductionPalletScanResult.Failure(
                lineGuardFailure.Value.Code,
                lineGuardFailure.Value.Message);
        }

        var palletLines = GetPalletLines(pallet);

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
                ComponentLineId = line.Id,
                ItemId = line.ItemId,
                ItemName = lineItem?.Name ?? line.ItemName,
                Brand = lineItem?.Brand ?? line.Brand,
                Qty = line.PlannedQty,
                PlannedQty = line.PlannedQty,
                FilledQty = line.FilledQty,
                FilledAt = line.FilledAt,
                IsCompleted = line.IsCompleted,
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
            EffectiveStatus = pallet.EffectiveStatus,
            CanFill = pallet.CanFill,
            Document = BuildFillingDocument(doc.Id, pallets, pallet.OrderId)
        };
    }

    public ProductionPalletFillResult Fill(
        string? huCode,
        string? deviceId,
        long? orderId = null,
        long? prdDocId = null) =>
        FillWholePallet(huCode, deviceId, orderId, prdDocId, null, requireMixedCompatibility: false);

    private ProductionPalletFillResult FillWholePallet(
        string? huCode,
        string? deviceId,
        long? orderId,
        long? prdDocId,
        IReadOnlySet<long>? requestedComponentLineIds,
        bool requireMixedCompatibility)
    {
        var normalizedHu = NormalizeHu(huCode);
        if (string.IsNullOrWhiteSpace(normalizedHu))
        {
            return ProductionPalletFillResult.Failure(
                ProductionFillingErrorCodes.HuRequired, "Укажите код паллеты.");
        }

        ProductionPalletFillResult? result = null;
        try
        {
            _data.ExecuteInTransaction(store =>
            {
                var initialPallet = store.GetProductionPalletByHu(normalizedHu);
                if (store is IHuTransactionLockStore lockStore && initialPallet?.OrderId is long lockOrderId)
                {
                    if (!store.LockOrdersForUpdate(new[] { lockOrderId }))
                    {
                        result = ProductionPalletFillResult.Failure(
                            ProductionFillingErrorCodes.PalletNotFound, "Заказ паллеты не найден.");
                        return;
                    }
                    lockStore.LockNormalizedHus(new[] { normalizedHu });
                }
                var pallet = store.GetProductionPalletByHuForUpdate(normalizedHu);
                if (pallet == null)
                {
                    result = ProductionPalletFillResult.Failure(
                        ProductionFillingErrorCodes.PalletNotFound, "Паллета не найдена в плане выпуска.");
                    return;
                }

                if (store is IMarkingCutoverRuntimeGuard cutoverGuard)
                {
                    try
                    {
                        cutoverGuard.RequireEnforcedMarkingWorkflowForPallet(
                            pallet.Id,
                            "production_pallet_fill");
                    }
                    catch (InvalidOperationException ex)
                    {
                        var errorCode = ex.Message.StartsWith(
                            MarkingCutoverRuntimeErrors.MaintenanceRequired,
                            StringComparison.Ordinal)
                            ? MarkingCutoverRuntimeErrors.MaintenanceRequired
                            : ex.Message;
                        result = ProductionPalletFillResult.Failure(
                            errorCode,
                            ex.Message);
                        return;
                    }
                }

                // Same unified classification as Scan. A stale prdDocId within the same
                // order is intentionally ignored here (the actual pallet.PrdDocId is used),
                // so a PRD mismatch never becomes an "another order" error.
                var classification = ClassifyPalletForFilling(store, orderId, pallet, out _);
                if (classification.HasValue)
                {
                    result = ProductionPalletFillResult.Failure(classification.Value.Code, classification.Value.Message);
                    return;
                }

                var doc = store.GetDoc(pallet.PrdDocId);
                if (doc == null || doc.Type != DocType.ProductionReceipt)
                {
                    result = ProductionPalletFillResult.Failure(
                        ProductionFillingErrorCodes.PalletPlanInvalid, "Документ выпуска не найден.");
                    return;
                }

                if (doc.Status == DocStatus.Closed)
                {
                    if (string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!HasOnlyValidFillingPalletLines(store, pallet))
                        {
                            result = ProductionPalletFillResult.Failure(
                                ProductionFillingErrorCodes.PalletPlanInvalid, "Строка заказа для паллеты не найдена.");
                            return;
                        }

                        result = new ProductionPalletFillResult
                        {
                            Success = true,
                            Error = ProductionFillingErrorCodes.PalletAlreadyFilled,
                            ErrorMessage = "Паллета уже наполнена.",
                            AlreadyFilled = true,
                            PrdAutoClosed = true,
                            ClosedPrdDocId = doc.Id,
                            ClosedPrdDocRef = doc.DocRef,
                            Pallet = pallet,
                            Document = BuildFillingDocument(doc.Id, store.GetProductionPalletsByDoc(doc.Id), pallet.OrderId)
                        };
                        return;
                    }

                    result = ProductionPalletFillResult.Failure(
                        ProductionFillingErrorCodes.PrdAlreadyClosed, "Документ выпуска уже закрыт.");
                    return;
                }

                if (IsCancelledPallet(pallet))
                {
                    result = ProductionPalletFillResult.Failure(
                        ProductionFillingErrorCodes.PalletCancelled, "Паллета отменена и не может быть наполнена.");
                    return;
                }

                var palletLines = GetPalletLines(pallet);
                var lineGuardFailure = ValidateFillingPalletLineGuards(
                    store,
                    pallet,
                    doc,
                    requireDestinationLocation: true);
                if (lineGuardFailure.HasValue)
                {
                    result = ProductionPalletFillResult.Failure(
                        lineGuardFailure.Value.Code,
                        lineGuardFailure.Value.Message);
                    return;
                }

                if (requireMixedCompatibility)
                {
                    if (!pallet.IsMixedPallet)
                    {
                        result = ProductionPalletFillResult.Failure(
                            "PALLET_NOT_MIXED", "Эта паллета не является микс-паллетой.");
                        return;
                    }

                    var allComponentIds = palletLines.Select(line => line.Id).Where(id => id > 0).ToHashSet();
                    if (requestedComponentLineIds == null
                        || !allComponentIds.SetEquals(requestedComponentLineIds))
                    {
                        result = ProductionPalletFillResult.Failure(
                            ProductionFillingErrorCodes.PartialComponentFillNotAllowed,
                            "Микс-паллета должна быть подтверждена целиком; частичное наполнение запрещено.");
                        return;
                    }
                }

                if (string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                {
                    result = ProductionPalletFillResult.Failure(
                        ProductionFillingErrorCodes.PalletPlanInvalid,
                        "Паллета отмечена FILLED, но выпуск не проведён. Используйте контролируемый recovery flow.");
                    return;
                }

                if (_fillClose == null || !_fillClose.AutoCloseEnabled)
                {
                    result = ProductionPalletFillResult.Failure(
                        ProductionFillingErrorCodes.ProductionAutoCloseRequired,
                        "Наполнение production pallet требует включённого атомарного проведения выпуска.");
                    return;
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

    public ProductionPalletFillResult FillMixedComponents(
        string? huCode,
        IReadOnlyCollection<long>? componentLineIds,
        string? deviceId,
        long? orderId = null,
        long? prdDocId = null)
    {
        var requestedIds = (componentLineIds ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .ToHashSet();
        if (requestedIds.Count == 0)
        {
            return ProductionPalletFillResult.Failure(
                "COMPONENT_LINE_IDS_REQUIRED",
                "Передайте полный состав микс-паллеты для compatibility-подтверждения.");
        }

        return FillWholePallet(
            huCode,
            deviceId,
            orderId,
            prdDocId,
            requestedIds,
            requireMixedCompatibility: true);
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
            Error = fillResult.Error,
            ErrorMessage = fillResult.ErrorMessage,
            AlreadyFilled = fillResult.AlreadyFilled || autoClose.AlreadyClosed,
            Pallet = pallet,
            Document = BuildFillingDocument(prdDocId, store.GetProductionPalletsByDoc(prdDocId), pallet.OrderId),
            PrdAutoClosed = true,
            ClosedPrdDocId = autoClose.ClosedPrdDocId,
            ClosedPrdDocRef = autoClose.ClosedPrdDocRef,
            EffectiveStatus = pallet.EffectiveStatus,
            FilledComponentCount = pallet.FilledComponentCount,
            TotalComponentCount = pallet.TotalComponentCount,
            LedgerWritten = true,
            Message = fillResult.Message
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
        if (order == null)
        {
            return null;
        }

        var palletsByOrderId = LoadPalletsByOrderId(new[] { orderId });
        var orderLinesByOrderId = LoadOrderLinesByOrderId(new[] { orderId });
        var docsByOrderId = LoadDocsByOrderId(new[] { orderId });
        var productionDocIds = docsByOrderId.Values
            .SelectMany(docs => docs)
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .Select(doc => doc.Id)
            .ToArray();
        var docLinesByDocId = LoadDocLinesByDocId(productionDocIds);
        var huOrderContextRows = order.Type == OrderType.Internal
            ? LoadHuOrderContextRows()
            : Array.Empty<HuOrderContextRow>();
        var completions = LoadFillingCompletions(new[] { orderId });
        return BuildFillingOrderFromPreloaded(
            order,
            workItems,
            palletsByOrderId,
            orderLinesByOrderId,
            docsByOrderId,
            docLinesByDocId,
            huOrderContextRows,
            completions);
    }

    private ProductionFillingOrder? BuildFillingOrderFromPreloaded(
        Order order,
        IReadOnlyList<ProductionPalletWorkItem> workItems,
        IReadOnlyDictionary<long, IReadOnlyList<ProductionPallet>> palletsByOrderId,
        IReadOnlyDictionary<long, IReadOnlyList<OrderLine>> orderLinesByOrderId,
        IReadOnlyDictionary<long, IReadOnlyList<Doc>> docsByOrderId,
        IReadOnlyDictionary<long, IReadOnlyList<DocLine>> docLinesByDocId,
        IReadOnlyList<HuOrderContextRow> huOrderContextRows,
        IReadOnlyList<ProductionFillingCompletion> completions,
        OrderShipmentProgress? shipmentProgress = null)
    {
        if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            return null;
        }

        palletsByOrderId.TryGetValue(order.Id, out var rawPallets);
        rawPallets ??= Array.Empty<ProductionPallet>();
        orderLinesByOrderId.TryGetValue(order.Id, out var orderLines);
        orderLines ??= Array.Empty<OrderLine>();
        docsByOrderId.TryGetValue(order.Id, out var docs);
        docs ??= Array.Empty<Doc>();
        var orderLinesById = orderLines.ToDictionary(line => line.Id);

        var fillingPallets = BuildOrderOwnedPalletViews(order.Id, rawPallets, orderLinesById);
        var eligibilitySnapshot = BuildFillingEligibilitySnapshot(
            order,
            orderLines,
            docs,
            docLinesByDocId,
            rawPallets,
            huOrderContextRows);
        if (BuildFillingEligibleHuCodes(
                order.Id,
                fillingPallets,
                rawPallets,
                eligibilitySnapshot).Count == 0)
        {
            return null;
        }

        var activeItems = fillingPallets
            .GroupBy(pallet => pallet.PrdDocId)
            .Select(group =>
            {
                var workItem = workItems.FirstOrDefault(item => item.PrdDocId == group.Key);
                return new ProductionPalletWorkItem
                {
                    PrdDocId = group.Key,
                    PrdDocRef = workItem?.PrdDocRef ?? string.Empty,
                    PrdStatus = workItem?.PrdStatus ?? string.Empty,
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
        return MapProductionFillingOrder(
            order,
            primaryWorkItem.PrdDocId,
            primaryWorkItem.PrdDocRef,
            summary,
            BuildOperationProgress(order.Id, fillingPallets, completions),
            shipmentProgress);
    }

    private ProductionFillingOrder? BuildReadyFillingOrderFromPreloaded(
        Order order,
        IReadOnlyDictionary<long, IReadOnlyList<ProductionPallet>> palletsByOrderId,
        IReadOnlyDictionary<long, IReadOnlyList<OrderLine>> orderLinesByOrderId,
        IReadOnlyList<ProductionFillingCompletion> completions,
        OrderShipmentProgress? shipmentProgress = null)
    {
        palletsByOrderId.TryGetValue(order.Id, out var rawPallets);
        rawPallets ??= Array.Empty<ProductionPallet>();
        orderLinesByOrderId.TryGetValue(order.Id, out var orderLines);
        orderLines ??= Array.Empty<OrderLine>();
        var orderLinesById = orderLines.ToDictionary(line => line.Id);

        var pallets = BuildOrderOwnedPalletViews(order.Id, rawPallets, orderLinesById);
        var progress = BuildOperationProgress(order.Id, pallets, completions);
        if (!progress.CanClose || progress.IsClosed || pallets.Count == 0)
        {
            return null;
        }

        var docId = pallets[0].PrdDocId;
        var docRef = pallets[0].PrdDocId > 0
            ? TryGetDocRef(docId)
            : null;
        return MapProductionFillingOrder(order, docId, docRef, BuildSummary(pallets), progress, shipmentProgress);
    }

    private string? TryGetDocRef(long docId)
    {
        try
        {
            return _data.GetDoc(docId)?.DocRef;
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return null;
        }
    }

    private static ProductionFillingOrder MapProductionFillingOrder(
        Order order,
        long? prdDocId,
        string? prdDocRef,
        ProductionPalletSummary summary,
        ProductionOperationProgress progress,
        OrderShipmentProgress? shipmentProgress = null)
    {
        return new ProductionFillingOrder
        {
            OrderId = order.Id,
            OrderRef = order.OrderRef,
            OrderType = OrderStatusMapper.TypeToString(order.Type),
            OrderTypeDisplay = OrderStatusMapper.TypeToDisplayName(order.Type),
            OrderStatus = OrderStatusMapper.StatusToString(order.Status),
            OrderStatusDisplay = OrderStatusMapper.StatusToDisplayName(order.Status, order.Type),
            OrderStatusPresentation = OrderOperatorStatusResolver.Resolve(order.Status, order.Type, shipmentProgress),
            PartnerName = order.PartnerDisplay,
            PrdDocId = prdDocId,
            PrdDocRef = prdDocRef,
            Summary = summary,
            Progress = progress
        };
    }

    private static bool ShouldExcludeFromFillingList(ProductionFillingOrder row)
    {
        return row.OrderType == OrderStatusMapper.TypeToString(OrderType.Internal)
               && row.OrderStatus == OrderStatusMapper.StatusToString(OrderStatus.Shipped)
               && row.Summary.RemainingQty <= QtyTolerance
               && row.Summary.RemainingPalletCount <= 0;
    }

    private static bool IsTerminalFillingOrder(Order order)
    {
        return order.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged;
    }

    private Dictionary<long, Order> LoadOrdersById(IReadOnlyCollection<long> orderIds)
    {
        try
        {
            return _data.GetOrdersByIds(orderIds).ToDictionary(order => order.Id);
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            var result = new Dictionary<long, Order>();
            foreach (var orderId in orderIds)
            {
                var order = _data.GetOrder(orderId);
                if (order != null)
                {
                    result[order.Id] = order;
                }
            }

            return result;
        }
    }

    private IReadOnlyDictionary<long, IReadOnlyList<ProductionPallet>> LoadPalletsByOrderId(IReadOnlyCollection<long> orderIds)
    {
        try
        {
            return _data.GetProductionPalletsByOrderIds(orderIds);
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return orderIds.ToDictionary(
                orderId => orderId,
                orderId => (IReadOnlyList<ProductionPallet>)GetProductionPalletsByOrder(_data, orderId));
        }
    }

    private IReadOnlyDictionary<long, IReadOnlyList<OrderLine>> LoadOrderLinesByOrderId(IReadOnlyCollection<long> orderIds)
    {
        try
        {
            return _data.GetOrderLinesByOrderIds(orderIds);
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return orderIds.ToDictionary(
                orderId => orderId,
                orderId => (IReadOnlyList<OrderLine>)_data.GetOrderLines(orderId));
        }
    }

    private IReadOnlyDictionary<long, IReadOnlyList<Doc>> LoadDocsByOrderId(IReadOnlyCollection<long> orderIds)
    {
        try
        {
            return _data.GetDocsByOrderIds(orderIds);
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return orderIds.ToDictionary(
                orderId => orderId,
                orderId => (IReadOnlyList<Doc>)_data.GetDocsByOrder(orderId));
        }
    }

    private IReadOnlyDictionary<long, IReadOnlyList<DocLine>> LoadDocLinesByDocId(
        IReadOnlyCollection<long> docIds)
    {
        if (docIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<DocLine>>();
        }

        try
        {
            return _data.GetDocLinesByDocIds(docIds);
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return docIds.ToDictionary(
                docId => docId,
                docId => (IReadOnlyList<DocLine>)_data.GetDocLines(docId));
        }
    }

    private IReadOnlyList<HuOrderContextRow> LoadHuOrderContextRows()
    {
        try
        {
            return _data.GetHuOrderContextRows();
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return Array.Empty<HuOrderContextRow>();
        }
    }

    private IReadOnlyDictionary<long, OrderShipmentProgress> LoadShipmentProgressByOrderId(
        IReadOnlyCollection<long> orderIds,
        IReadOnlyDictionary<long, IReadOnlyList<OrderLine>> orderLinesByOrderId)
    {
        IReadOnlyDictionary<long, IReadOnlyDictionary<long, double>> shippedByOrderId;
        try
        {
            shippedByOrderId = _data.GetShippedTotalsByOrderIds(orderIds);
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            shippedByOrderId = new Dictionary<long, IReadOnlyDictionary<long, double>>();
        }

        return orderIds.ToDictionary(
            orderId => orderId,
            orderId =>
            {
                var lines = orderLinesByOrderId.GetValueOrDefault(orderId) ?? Array.Empty<OrderLine>();
                var shipped = shippedByOrderId.GetValueOrDefault(orderId)
                              ?? new Dictionary<long, double>();
                return new OrderShipmentProgress
                {
                    OrderedQty = lines.Sum(line => Math.Max(0, line.QtyOrdered)),
                    ShippedQty = lines.Sum(line => Math.Max(0, shipped.GetValueOrDefault(line.Id))),
                    RemainingQty = lines.Sum(line =>
                        Math.Max(0, line.QtyOrdered - Math.Max(0, shipped.GetValueOrDefault(line.Id))))
                };
            });
    }

    private IReadOnlyList<ProductionFillingCompletion> LoadFillingCompletions(IReadOnlyCollection<long> orderIds)
    {
        try
        {
            return _data.GetProductionFillingCompletionsByOrderIds(orderIds);
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            return Array.Empty<ProductionFillingCompletion>();
        }
    }

    private ProductionFillingContext BuildFillingContext(
        Order order,
        Doc doc,
        IReadOnlyList<ProductionPallet> pallets,
        IReadOnlyList<ProductionPallet> rawPallets,
        FillingEligibilitySnapshot eligibilitySnapshot)
    {
        var markingEligibility = BuildMarkingFillingEligibility(rawPallets);
        return new ProductionFillingContext
        {
            OrderId = order.Id,
            OrderRef = order.OrderRef,
            OrderType = OrderStatusMapper.TypeToString(order.Type),
            OrderTypeDisplay = OrderStatusMapper.TypeToDisplayName(order.Type),
            OrderStatus = OrderStatusMapper.StatusToString(order.Status),
            OrderStatusDisplay = OrderStatusMapper.StatusToDisplayName(order.Status, order.Type),
            OrderStatusPresentation = OrderOperatorStatusResolver.Resolve(
                order.Status,
                order.Type,
                OrderShipmentProgressService.Get(_data, order.Id)),
            PartnerName = order.PartnerDisplay,
            PrdDocId = doc.Id,
            PrdDocRef = doc.DocRef,
            Document = BuildFillingDocument(doc.Id, pallets),
            Progress = BuildOperationProgress(_data, order.Id, pallets),
            FillingEligibleHuCodes = BuildFillingEligibleHuCodes(
                order.Id,
                pallets,
                rawPallets,
                eligibilitySnapshot,
                markingEligibility),
            FillingMarkingEligibilityByHuCode = rawPallets
                .Where(pallet => !string.IsNullOrWhiteSpace(pallet.HuCode)
                                 && markingEligibility.ContainsKey(pallet.Id))
                .ToDictionary(
                    pallet => pallet.HuCode.Trim(),
                    pallet => markingEligibility[pallet.Id],
                    StringComparer.OrdinalIgnoreCase)
        };
    }

    private IReadOnlySet<string> BuildFillingEligibleHuCodes(
        long orderId,
        IReadOnlyList<ProductionPallet> fillingPallets,
        IReadOnlyList<ProductionPallet> rawPallets,
        FillingEligibilitySnapshot snapshot,
        IReadOnlyDictionary<long, MarkingPalletEligibility>? markingEligibility = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_fillClose?.AutoCloseEnabled != true || fillingPallets.Count == 0)
        {
            return result;
        }

        var rawById = rawPallets.ToDictionary(pallet => pallet.Id);
        markingEligibility ??= BuildMarkingFillingEligibility(rawPallets);

        foreach (var view in fillingPallets)
        {
            if (!rawById.TryGetValue(view.Id, out var pallet))
            {
                continue;
            }

            if (pallet.OrderId != orderId
                || !pallet.CanFill
                || markingEligibility.TryGetValue(pallet.Id, out var markingDecision)
                   && !markingDecision.IsEligible
                || !snapshot.ProductionDocsById.TryGetValue(pallet.PrdDocId, out var doc)
                || doc.Status == DocStatus.Closed
                || !snapshot.DocLinesByDocId.TryGetValue(pallet.PrdDocId, out var docLinesById)
                || ValidateFillingPalletLineGuards(
                    pallet,
                    snapshot.OrderLinesById,
                    docLinesById,
                    snapshot.FilledQtyByOrderLineId,
                    requireDestinationLocation: true).HasValue)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(pallet.HuCode))
            {
                result.Add(pallet.HuCode.Trim());
            }
        }

        return result;
    }

    private IReadOnlyDictionary<long, MarkingPalletEligibility> BuildMarkingFillingEligibility(
        IReadOnlyCollection<ProductionPallet> pallets)
    {
        if (_data is not IMarkingCutoverRuntimeGuard guard)
        {
            return new Dictionary<long, MarkingPalletEligibility>();
        }

        return guard.GetMarkingPalletEligibility(
            pallets.Select(pallet => pallet.Id).Where(id => id > 0).Distinct().ToArray(),
            "production_pallet_filling_read_model");
    }

    private static FillingEligibilitySnapshot BuildFillingEligibilitySnapshot(
        Order order,
        IReadOnlyList<OrderLine> orderLines,
        IReadOnlyList<Doc> docs,
        IReadOnlyDictionary<long, IReadOnlyList<DocLine>> docLinesByDocId,
        IReadOnlyList<ProductionPallet> rawPallets,
        IReadOnlyList<HuOrderContextRow> huOrderContextRows)
    {
        var productionDocsById = docs
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .ToDictionary(doc => doc.Id);
        var activeDocLinesByDocId = productionDocsById.Keys.ToDictionary(
            docId => docId,
            docId => (IReadOnlyDictionary<long, DocLine>)(docLinesByDocId.TryGetValue(docId, out var lines)
                ? lines.ToDictionary(line => line.Id)
                : new Dictionary<long, DocLine>()));
        var orderLinesById = orderLines.ToDictionary(line => line.Id);
        var filledQtyByOrderLineId = BuildFillGuardFilledQtyByOrderLine(
            order,
            orderLinesById.Keys,
            productionDocsById,
            activeDocLinesByDocId,
            rawPallets,
            huOrderContextRows);
        return new FillingEligibilitySnapshot(
            orderLinesById,
            productionDocsById,
            activeDocLinesByDocId,
            filledQtyByOrderLineId);
    }

    private static IReadOnlyDictionary<long, double> BuildFillGuardFilledQtyByOrderLine(
        Order order,
        IEnumerable<long> orderLineIds,
        IReadOnlyDictionary<long, Doc> productionDocsById,
        IReadOnlyDictionary<long, IReadOnlyDictionary<long, DocLine>> docLinesByDocId,
        IReadOnlyList<ProductionPallet> rawPallets,
        IReadOnlyList<HuOrderContextRow> huOrderContextRows)
    {
        var result = orderLineIds.Distinct().ToDictionary(orderLineId => orderLineId, _ => 0d);
        var reservedHuByItem = order.Type == OrderType.Internal
            ? huOrderContextRows
                .Where(row => row.ReservedCustomerOrderId.HasValue && !string.IsNullOrWhiteSpace(row.HuCode))
                .GroupBy(row => row.ItemId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(row => NormalizeHu(row.HuCode))
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Cast<string>()
                        .ToHashSet(StringComparer.OrdinalIgnoreCase))
            : new Dictionary<long, HashSet<string>>();

        foreach (var pallet in rawPallets)
        {
            if (!string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (order.Type == OrderType.Internal)
            {
                if (!productionDocsById.ContainsKey(pallet.PrdDocId))
                {
                    continue;
                }

                var supersededDocLineIds = docLinesByDocId.TryGetValue(pallet.PrdDocId, out var docLines)
                    ? docLines.Values
                        .Where(line => line.ReplacesLineId.HasValue)
                        .Select(line => line.ReplacesLineId!.Value)
                        .ToHashSet()
                    : new HashSet<long>();
                if (supersededDocLineIds.Contains(pallet.DocLineId))
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
            }

            foreach (var orderLineId in GetPalletLines(pallet)
                         .Where(line => line.OrderLineId.HasValue)
                         .Select(line => line.OrderLineId!.Value)
                         .Distinct())
            {
                if (result.ContainsKey(orderLineId))
                {
                    result[orderLineId] += ResolvePalletQtyForOrderLine(pallet, orderLineId);
                }
            }
        }

        return result;
    }

    private sealed record FillingEligibilitySnapshot(
        IReadOnlyDictionary<long, OrderLine> OrderLinesById,
        IReadOnlyDictionary<long, Doc> ProductionDocsById,
        IReadOnlyDictionary<long, IReadOnlyDictionary<long, DocLine>> DocLinesByDocId,
        IReadOnlyDictionary<long, double> FilledQtyByOrderLineId);

    private static ProductionOperationProgress BuildOperationProgress(IDataStore store, long orderId, IReadOnlyList<ProductionPallet> pallets)
    {
        return BuildOperationProgress(orderId, pallets, Array.Empty<ProductionFillingCompletion>(), store);
    }

    private static ProductionOperationProgress BuildOperationProgress(
        long orderId,
        IReadOnlyList<ProductionPallet> pallets,
        IReadOnlyList<ProductionFillingCompletion> completions,
        IDataStore? store = null)
    {
        // Closure is derived purely from pallet state: once every active (non-cancelled)
        // pallet is FILLED the filling operation is considered closed. The
        // production_filling_completions marker is kept only for audit/compat and must
        // NOT be required for IsClosed — otherwise a "RequiredPallets == ScannedPallets,
        // CanClose == true, IsClosed == false" limbo could persist for new or legacy data.
        _ = orderId;
        _ = completions;
        _ = store;
        var required = pallets.Where(pallet => ProductionPalletStatus.IsOperational(pallet.Status)).ToList();
        var scanned = required.Count(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase));
        var fingerprint = BuildOperationFingerprint(required);
        var canClose = required.Count > 0 && scanned == required.Count;

        return new ProductionOperationProgress
        {
            RequiredPallets = required.Count,
            ScannedPallets = scanned,
            RemainingPallets = Math.Max(0, required.Count - scanned),
            CanClose = canClose,
            IsClosed = canClose,
            OperationFingerprint = fingerprint
        };
    }

    private static string BuildOperationFingerprint(IReadOnlyList<ProductionPallet> pallets)
    {
        var text = string.Join("|", pallets.OrderBy(pallet => pallet.Id).Select(pallet =>
            string.Join(":", pallet.Id, pallet.HuCode, pallet.PlannedQty.ToString("R", CultureInfo.InvariantCulture),
                string.Join(",", pallet.Lines.OrderBy(line => line.Id).Select(line =>
                    $"{line.Id}/{line.DocLineId}/{line.OrderLineId}/{line.ItemId}/{line.PlannedQty.ToString("R", CultureInfo.InvariantCulture)}")))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static bool IsMockStoreException(Exception ex)
    {
        var fullName = ex.GetType().FullName ?? string.Empty;
        return fullName.Contains("Moq", StringComparison.OrdinalIgnoreCase)
               || fullName.Contains("Castle.Proxies", StringComparison.OrdinalIgnoreCase);
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
            .Where(pallet => ProductionPalletStatus.IsOperational(pallet.Status))
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
        IReadOnlyList<Doc> docs,
        IReadOnlyList<ProductionPallet> fillingPallets,
        bool requireRemaining)
    {
        foreach (var doc in docs
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

    private static IReadOnlyCollection<long> ExpandManualMixedGroupScope(
        IDataStore store,
        long orderId,
        IEnumerable<long> scopedOrderLineIds)
    {
        var orderLines = store.GetOrderLines(orderId);
        var scopedIds = scopedOrderLineIds.Where(id => id > 0).ToHashSet();
        var scopedGroups = orderLines
            .Where(line => scopedIds.Contains(line.Id) && !string.IsNullOrWhiteSpace(line.ProductionPalletGroup))
            .Select(line => line.ProductionPalletGroup!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (scopedGroups.Count == 0)
        {
            return scopedIds;
        }

        foreach (var line in orderLines.Where(line =>
                     !string.IsNullOrWhiteSpace(line.ProductionPalletGroup)
                     && scopedGroups.Contains(line.ProductionPalletGroup!.Trim())))
        {
            scopedIds.Add(line.Id);
        }

        return scopedIds;
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

        var activePalletsByOrder = GetProductionPalletsByOrder(store, order.Id)
            .Where(pallet => IsOpenProductionPalletCoverage(store, pallet))
            .ToArray();
        var receiptLinesByOrderLineId = OrderReceiptRemainingCalculator.GetRemaining(store, order)
            .ToDictionary(line => line.OrderLineId, line => line);
        return orderLinesById.Values
            .Select(orderLine =>
            {
                var target = ProductionFuturePlanTargetCalculator.Calculate(store, order, orderLine);
                var activePalletQty = SumPalletQtyForOrderLine(activePalletsByOrder, orderLine.Id);
                var missingQty = Math.Max(0, target.FuturePlanQty - activePalletQty);
                receiptLinesByOrderLineId.TryGetValue(orderLine.Id, out var receiptLine);
                return new OrderReceiptLine
                {
                    OrderLineId = orderLine.Id,
                    OrderId = order.Id,
                    ItemId = orderLine.ItemId,
                    ItemName = receiptLine?.ItemName ?? string.Empty,
                    QtyOrdered = orderLine.QtyOrdered,
                    QtyReceived = order.Type == OrderType.Customer
                        ? Math.Max(0, orderLine.QtyOrdered - missingQty)
                        : receiptLine?.QtyReceived ?? 0,
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

    internal static IReadOnlyDictionary<long, double> BuildInternalPlanningCoverage(
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
            .Where(pallet => ProductionPalletStatus.IsOperational(pallet.Status))
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
        return BuildOrderOwnedPalletViews(orderId, pallets, orderLinesById);
    }

    public static IReadOnlyList<ProductionPallet> BuildOrderOwnedPalletViews(
        long orderId,
        IReadOnlyList<ProductionPallet> pallets,
        IReadOnlyDictionary<long, OrderLine> orderLinesById)
    {
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
            PrintedLabelFingerprint = pallet.PrintedLabelFingerprint,
            PrintedLabelFingerprintVersion = pallet.PrintedLabelFingerprintVersion,
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

    private static (string Code, string Message)? ValidateFillingPalletLineGuards(
        IDataStore store,
        ProductionPallet pallet,
        Doc doc,
        bool requireDestinationLocation)
    {
        if (!pallet.OrderId.HasValue)
        {
            return (ProductionFillingErrorCodes.PalletPlanInvalid, "Строка заказа для паллеты не найдена.");
        }

        var docLinesById = store.GetDocLines(doc.Id).ToDictionary(line => line.Id, line => line);
        var orderLinesById = store.GetOrderLines(pallet.OrderId.Value).ToDictionary(line => line.Id);
        var filledQtyByOrderLineId = GetPalletLines(pallet)
            .Where(line => line.OrderLineId.HasValue)
            .Select(line => line.OrderLineId!.Value)
            .Distinct()
            .ToDictionary(
                orderLineId => orderLineId,
                orderLineId => GetFillGuardFilledQty(
                    store,
                    pallet.OrderId.Value,
                    orderLineId,
                    pallet.Id));

        return ValidateFillingPalletLineGuards(
            pallet,
            orderLinesById,
            docLinesById,
            filledQtyByOrderLineId,
            requireDestinationLocation);
    }

    private static (string Code, string Message)? ValidateFillingPalletLineGuards(
        ProductionPallet pallet,
        IReadOnlyDictionary<long, OrderLine> orderLinesById,
        IReadOnlyDictionary<long, DocLine> docLinesById,
        IReadOnlyDictionary<long, double> filledQtyByOrderLineId,
        bool requireDestinationLocation)
    {
        if (!pallet.OrderId.HasValue || orderLinesById.Count == 0)
        {
            return (ProductionFillingErrorCodes.PalletPlanInvalid, "Строка заказа для паллеты не найдена.");
        }

        var palletLines = GetPalletLines(pallet);
        if (palletLines.Count == 0
            || palletLines.Any(line => !IsValidFillingPalletLine(line, orderLinesById)))
        {
            return (ProductionFillingErrorCodes.PalletPlanInvalid, "Строка заказа для паллеты не найдена.");
        }

        if (HasInconsistentComponentProgress(palletLines, pallet.Status))
        {
            return (
                ProductionFillingErrorCodes.PalletPartialFillInconsistent,
                "По паллете найден исторический частичный component progress. Требуется контролируемая корректировка.");
        }

        foreach (var palletLine in palletLines)
        {
            if (!docLinesById.TryGetValue(palletLine.DocLineId, out var docLine))
            {
                return (
                    ProductionFillingErrorCodes.PalletPlanInvalid,
                    "Строка паллеты не найдена в документе выпуска.");
            }

            if (docLine.ItemId != palletLine.ItemId || docLine.OrderLineId != palletLine.OrderLineId)
            {
                return (
                    ProductionFillingErrorCodes.PalletPlanInvalid,
                    "План паллеты не совпадает со строкой выпуска.");
            }

            if (requireDestinationLocation && !docLine.ToLocationId.HasValue)
            {
                return (
                    ProductionFillingErrorCodes.PalletPlanInvalid,
                    "Для паллеты не указано место хранения.");
            }

            if (pallet.OrderId.HasValue && palletLine.OrderLineId.HasValue)
            {
                if (!orderLinesById.TryGetValue(palletLine.OrderLineId.Value, out var orderLine)
                    || orderLine.ItemId != palletLine.ItemId)
                {
                    return (
                        ProductionFillingErrorCodes.PalletPlanInvalid,
                        "Строка заказа для паллеты не найдена.");
                }

                var alreadyFilled = filledQtyByOrderLineId.GetValueOrDefault(orderLine.Id);
                if (alreadyFilled + palletLine.PlannedQty > orderLine.QtyOrdered + QtyTolerance)
                {
                    return (
                        ProductionFillingErrorCodes.FillExceedsRemaining,
                        "Выпуск превышает остаток по строке заказа");
                }
            }
        }

        return null;
    }

    private static bool HasInconsistentComponentProgress(
        IReadOnlyCollection<ProductionPalletComponentLine> palletLines,
        string? palletStatus)
    {
        if (!palletLines.Any(line => line.FilledQty > QtyTolerance))
        {
            return false;
        }

        var hasCompleteFilledComposition = palletLines.Count > 0
                                           && palletLines.All(line =>
                                               line.PlannedQty > QtyTolerance
                                               && Math.Abs(line.FilledQty - line.PlannedQty) <= QtyTolerance);
        return !string.Equals(palletStatus, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
               || !hasCompleteFilledComposition;
    }

    private static bool IsPendingFillPallet(ProductionPallet pallet)
    {
        return pallet.CanFill;
    }

    private static bool IsRemovableFuturePlanPallet(ProductionPallet pallet)
    {
        return pallet.CanFill && !pallet.HasComponentProgress;
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
                FilledAt = pallet.FilledAt,
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
        double Qty);
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

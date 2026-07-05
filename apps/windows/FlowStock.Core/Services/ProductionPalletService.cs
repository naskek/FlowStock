using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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

        TrimSurplusOpenPallets(store, order, orderId, orderLineId, orderedQty);

        var activePlannedAfterTrim = GetOpenProductionPalletsForOrderLine(store, orderId, orderLineId)
            .Sum(pallet => ResolvePalletQtyForOrderLine(pallet, orderLineId));
        var cancelledQty = Math.Max(0, activePlannedBefore - activePlannedAfterTrim);
        var missingAfterTrim = Math.Max(0, orderedQty - committedQty - activePlannedAfterTrim);
        var createdQty = 0d;
        var action = cancelledQty > QtyTolerance
                ? "trim_open"
                : missingAfterTrim > QtyTolerance
                    ? "missing_unplanned"
                    : "noop";

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

    /// <summary>
    /// Server-authoritative preview for the pallet constructor. Returns the current per-line
    /// shortfall, a homogeneous auto-split proposal (<c>suggested_pallets</c>, delta only),
    /// the existing read-only open plan and FILLED history, and a fingerprint for stale-checks.
    /// Read-only: does not write anything.
    /// </summary>
    public ProductionPalletPlanPreview BuildPlanPreview(long orderId)
    {
        var order = _data.GetOrder(orderId)
            ?? throw new ProductionPalletPlanException(
                ProductionPalletPlanErrorCodes.OrderNotPlannable, "Заказ не найден.");
        return BuildPlanPreviewInStore(_data, order);
    }

    internal static ProductionPalletPlanPreview BuildPlanPreviewInStore(IDataStore store, Order order)
    {
        var itemsById = store.GetItems(null).ToDictionary(item => item.Id, item => item);
        var orderLines = store.GetOrderLines(order.Id)
            .Where(line => line.CancelledAt == null && line.QtyOrdered > QtyTolerance)
            .OrderBy(line => line.Id)
            .ToArray();

        var plannable = order.Status is not (OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged);
        var shortfallByLine = plannable
            ? GetLinesNeedingPalletAppend(store, order).ToDictionary(line => line.OrderLineId, line => line)
            : new Dictionary<long, OrderReceiptLine>();

        var previewLines = orderLines
            .Select(line =>
            {
                itemsById.TryGetValue(line.ItemId, out var item);
                var shortfall = shortfallByLine.TryGetValue(line.Id, out var receipt) ? receipt.QtyRemaining : 0d;
                return new ProductionPalletPlanPreviewLine(
                    line.Id,
                    line.ItemId,
                    item?.Name ?? string.Empty,
                    item?.MaxQtyPerHu,
                    shortfall);
            })
            .ToArray();

        var suggested = BuildSuggestedPallets(shortfallByLine.Values, itemsById);

        var pallets = GetProductionPalletsByOrder(store, order.Id)
            .Where(pallet => !IsCancelledPallet(pallet))
            .ToArray();
        var docsById = store.GetDocsByOrder(order.Id).ToDictionary(doc => doc.Id, doc => doc);
        var openPlan = new List<SavedPalletDto>();
        var historical = new List<SavedPalletDto>();
        foreach (var pallet in pallets)
        {
            docsById.TryGetValue(pallet.PrdDocId, out var doc);
            var isFilled = string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase);
            var kind = isFilled ? SavedPalletDto.HistoricalKind : SavedPalletDto.OpenKind;
            var dto = BuildSavedPalletDto(pallet, doc, itemsById, kind);
            if (isFilled)
            {
                historical.Add(dto);
            }
            else
            {
                openPlan.Add(dto);
            }
        }

        var productionRequired = previewLines.Any(line => line.ShortfallQty > QtyTolerance);
        var fingerprint = ComputePreviewFingerprint(store, order, orderLines, previewLines, pallets);

        return new ProductionPalletPlanPreview
        {
            OrderId = order.Id,
            OrderRef = order.OrderRef,
            OrderType = order.Type == OrderType.Customer ? "CUSTOMER" : "INTERNAL",
            OrderStatus = order.Status.ToString().ToUpperInvariant(),
            ProductionRequired = productionRequired,
            PreviewFingerprint = fingerprint,
            Lines = previewLines,
            SuggestedPallets = suggested,
            OpenPlanPallets = openPlan,
            HistoricalPallets = historical
        };
    }

    private static IReadOnlyList<SuggestedPalletDto> BuildSuggestedPallets(
        IEnumerable<OrderReceiptLine> shortfallLines,
        IReadOnlyDictionary<long, Item> itemsById)
    {
        var result = new List<SuggestedPalletDto>();
        var tempNo = 1;
        foreach (var line in shortfallLines.OrderBy(line => line.OrderLineId))
        {
            if (line.QtyRemaining <= QtyTolerance)
            {
                continue;
            }

            itemsById.TryGetValue(line.ItemId, out var item);
            var cap = item?.MaxQtyPerHu;
            var itemName = string.IsNullOrWhiteSpace(item?.Name) ? line.ItemName : item!.Name;
            var chunkCap = cap.HasValue && cap.Value > QtyTolerance ? cap.Value : line.QtyRemaining;
            var remaining = line.QtyRemaining;
            while (remaining > QtyTolerance)
            {
                var chunk = Math.Min(chunkCap, remaining);
                if (chunk <= QtyTolerance)
                {
                    break;
                }

                result.Add(new SuggestedPalletDto(
                    tempNo++,
                    cap,
                    chunk,
                    IsMixed: false,
                    new[] { new SuggestedPalletComponentDto(line.OrderLineId, line.ItemId, itemName, chunk) }));
                remaining -= chunk;
            }
        }

        return result;
    }

    private static SavedPalletDto BuildSavedPalletDto(
        ProductionPallet pallet,
        Doc? doc,
        IReadOnlyDictionary<long, Item> itemsById,
        string kind)
    {
        var components = pallet.Lines
            .Select(line =>
            {
                itemsById.TryGetValue(line.ItemId, out var item);
                var name = string.IsNullOrWhiteSpace(line.ItemName) ? item?.Name ?? string.Empty : line.ItemName;
                return new SavedPalletComponentDto(
                    line.Id,
                    line.OrderLineId,
                    line.ItemId,
                    name,
                    line.PlannedQty,
                    line.FilledQty,
                    line.IsCompleted);
            })
            .ToArray();

        var caps = pallet.Lines
            .Select(line => itemsById.TryGetValue(line.ItemId, out var item) ? item.MaxQtyPerHu : null)
            .ToArray();
        double? capacity = caps.Length > 0
            && caps.All(cap => cap.HasValue && cap.Value > QtyTolerance)
            && caps.Select(cap => cap!.Value).Distinct().Count() == 1
                ? caps[0]
                : null;

        var isClosedDoc = doc?.Status == DocStatus.Closed;
        var isFilled = string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase);
        var isPartial = pallet.IsMixedPallet && pallet.HasComponentProgress && !pallet.AreAllComponentsFilled;
        var canDelete = !isClosedDoc && IsRemovableFuturePlanPallet(pallet);
        var disabledReason = isFilled
            ? "Паллета наполнена/выпущена"
            : isClosedDoc
                ? "Выпуск закрыт"
                : isPartial
                    ? "Паллета частично наполнена"
                    : canDelete
                        ? null
                        : "Статус паллеты не позволяет удаление";

        var totalQty = pallet.Lines.Count > 0
            ? pallet.Lines.Sum(line => line.PlannedQty)
            : pallet.PlannedQty;

        return new SavedPalletDto(
            kind,
            pallet.Id,
            pallet.HuCode,
            pallet.PrdDocId,
            doc?.DocRef ?? string.Empty,
            pallet.Status,
            pallet.EffectiveStatus,
            capacity,
            totalQty,
            pallet.IsMixedPallet,
            pallet.HasComponentProgress,
            canDelete,
            disabledReason,
            components);
    }

    private static string ComputePreviewFingerprint(
        IDataStore store,
        Order order,
        IReadOnlyList<OrderLine> orderLines,
        IReadOnlyList<ProductionPalletPlanPreviewLine> previewLines,
        IReadOnlyList<ProductionPallet> pallets)
    {
        var previewByLine = previewLines.ToDictionary(line => line.OrderLineId, line => line);
        var sb = new StringBuilder();
        sb.Append("order:").Append(order.Id).Append('|')
            .Append(order.Type).Append('|').Append(order.Status).Append(';');
        foreach (var line in orderLines.OrderBy(line => line.Id))
        {
            previewByLine.TryGetValue(line.Id, out var preview);
            sb.Append("line:").Append(line.Id).Append('|')
                .Append(line.Revision).Append('|')
                .Append(line.CancelledAt.HasValue ? 1 : 0).Append('|')
                .Append(line.ItemId).Append('|')
                .Append(FingerprintQty(line.QtyOrdered)).Append('|')
                .Append(FingerprintQty(preview?.ShortfallQty ?? 0)).Append('|')
                .Append(preview?.MaxQtyPerHu is { } cap ? FingerprintQty(cap) : "null").Append(';');
        }

        foreach (var pallet in pallets.OrderBy(pallet => pallet.Id))
        {
            sb.Append("pallet:").Append(pallet.Id).Append('|')
                .Append(pallet.HuCode).Append('|').Append(pallet.Status).Append(';');
        }

        foreach (var reserve in store.GetOrderReceiptPlanLines(order.Id)
                     .Where(reserve => reserve.QtyPlanned > 0)
                     .OrderBy(reserve => reserve.OrderLineId)
                     .ThenBy(reserve => reserve.ToHu, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("rpl:").Append(reserve.OrderLineId).Append('|')
                .Append(reserve.ToHu).Append('|').Append(FingerprintQty(reserve.QtyPlanned)).Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    private static string FingerprintQty(double value)
    {
        return Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Confirms an explicit pallet plan (append-delta). Under an <c>orders</c> row lock, re-reads
    /// state, verifies the preview fingerprint and equal-cap/allocation rules, then creates ONLY
    /// the new pallets on the current shortfall. Existing PLANNED/PRINTED/PARTIALLY_FILLED/FILLED
    /// pallets, their doc_lines, PRD, progress and ledger are never modified. Atomic.
    /// </summary>
    public ProductionPalletOrderPlanResult ConfirmExplicitPlan(long orderId, ProductionPalletExplicitPlanRequest request)
    {
        if (request == null)
        {
            throw new ProductionPalletPlanException(
                ProductionPalletPlanErrorCodes.PlanPreviewStale, "Пустой запрос плана паллет.");
        }

        var prdDocId = 0L;
        var wasExisting = false;
        var newFingerprint = string.Empty;
        _data.ExecuteInTransaction(store =>
        {
            // 1. Serialize per order via the shared orders row lock (id ASC, like close/outbound/control).
            if (!store.LockOrdersForUpdate([orderId]))
            {
                throw new ProductionPalletPlanException(
                    ProductionPalletPlanErrorCodes.OrderNotPlannable, "Заказ недоступен для планирования паллет.");
            }

            // 2. Re-read and re-validate order status under the lock.
            var order = store.GetOrder(orderId)
                ?? throw new ProductionPalletPlanException(
                    ProductionPalletPlanErrorCodes.OrderNotPlannable, "Заказ не найден.");
            if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged
                || (order.Type == OrderType.Internal && order.Status is not (OrderStatus.InProgress or OrderStatus.Draft)))
            {
                throw new ProductionPalletPlanException(
                    ProductionPalletPlanErrorCodes.OrderNotPlannable, "Заказ недоступен для планирования паллет.");
            }

            // 3. Recompute preview + fingerprint; reject stale.
            var preview = BuildPlanPreviewInStore(store, order);
            if (!string.Equals(preview.PreviewFingerprint, request.PreviewFingerprint, StringComparison.Ordinal))
            {
                throw new ProductionPalletPlanException(
                    ProductionPalletPlanErrorCodes.PlanPreviewStale,
                    "Данные заказа изменились с момента предпросмотра. Обновите план паллет.",
                    currentPreviewFingerprint: preview.PreviewFingerprint);
            }

            // 4. Zero shortfall is the only NO_PRODUCTION_REQUIRED case.
            var requiredByLine = preview.Lines
                .Where(line => line.ShortfallQty > QtyTolerance)
                .ToDictionary(line => line.OrderLineId, line => line.ShortfallQty);
            if (requiredByLine.Count == 0)
            {
                throw new ProductionPalletPlanException(
                    ProductionPalletPlanErrorCodes.NoProductionRequired, "Нет производственной нехватки по заказу.");
            }

            var orderLinesById = store.GetOrderLines(orderId).ToDictionary(line => line.Id, line => line);
            var itemsById = store.GetItems(null).ToDictionary(item => item.Id, item => item);

            // 5-8. Validate every pallet/component and accumulate per-line allocation.
            // The input plan is never silently rewritten: an empty pallet or a non-finite /
            // non-positive component qty is a structured INVALID_PALLET_PLAN error, not a skip.
            var canonicalPallets = new List<IReadOnlyList<(long OrderLineId, double Qty)>>();
            var allocatedByLine = new Dictionary<long, double>();
            var palletNo = 0;
            foreach (var pallet in request.Pallets ?? Array.Empty<ProductionPalletExplicitPlanPallet?>())
            {
                palletNo++;
                // Malformed JSON (pallets: [null]) yields a null element; reject before any access.
                if (pallet == null)
                {
                    throw new ProductionPalletPlanException(
                        ProductionPalletPlanErrorCodes.InvalidPalletPlan,
                        $"Паллета {palletNo} не задана (null в списке паллет).");
                }

                if (pallet.Components == null || pallet.Components.Count == 0)
                {
                    throw new ProductionPalletPlanException(
                        ProductionPalletPlanErrorCodes.InvalidPalletPlan,
                        $"Паллета {palletNo} пуста: каждая паллета плана должна содержать хотя бы один компонент.");
                }

                var byLine = new Dictionary<long, double>();
                foreach (var component in pallet.Components)
                {
                    // Malformed JSON (components: [null]) yields a null element; reject before any access.
                    if (component == null)
                    {
                        throw new ProductionPalletPlanException(
                            ProductionPalletPlanErrorCodes.InvalidPalletPlan,
                            $"Паллета {palletNo}: компонент не задан (null в списке компонентов).");
                    }

                    if (!orderLinesById.TryGetValue(component.OrderLineId, out var orderLine) || orderLine.OrderId != orderId)
                    {
                        throw new ProductionPalletPlanException(
                            ProductionPalletPlanErrorCodes.OrderLineNotFound,
                            $"Строка заказа {component.OrderLineId} не найдена в этом заказе.");
                    }

                    if (orderLine.CancelledAt != null)
                    {
                        throw new ProductionPalletPlanException(
                            ProductionPalletPlanErrorCodes.OrderLineCancelled,
                            $"Строка заказа {component.OrderLineId} отменена.");
                    }

                    if (double.IsNaN(component.Qty) || double.IsInfinity(component.Qty) || component.Qty <= QtyTolerance)
                    {
                        throw new ProductionPalletPlanException(
                            ProductionPalletPlanErrorCodes.InvalidPalletPlan,
                            $"Паллета {palletNo}: недопустимое количество компонента строки {component.OrderLineId}. " +
                            "Количество должно быть конечным и строго больше нуля.");
                    }

                    // Canonicalize duplicate components of the same line within a pallet by summing.
                    byLine[component.OrderLineId] = byLine.GetValueOrDefault(component.OrderLineId) + component.Qty;
                }

                // Equal-cap: all components share the same positive max_qty_per_hu; sum <= capacity.
                var caps = byLine.Keys
                    .Select(lineId => itemsById.TryGetValue(orderLinesById[lineId].ItemId, out var item) ? item.MaxQtyPerHu : null)
                    .ToArray();
                if (caps.Any(cap => !cap.HasValue || cap.Value <= QtyTolerance))
                {
                    throw new ProductionPalletPlanException(
                        ProductionPalletPlanErrorCodes.PalletCapacityMismatch,
                        "Не задана вместимость (max_qty_per_hu) для товара на паллете.");
                }

                if (caps.Select(cap => cap!.Value).Distinct().Count() > 1)
                {
                    throw new ProductionPalletPlanException(
                        ProductionPalletPlanErrorCodes.PalletCapacityMismatch,
                        "Разная вместимость (max_qty_per_hu) у товаров mixed-паллеты.");
                }

                var capacity = caps[0]!.Value;
                var total = byLine.Values.Sum();
                if (total > capacity + QtyTolerance)
                {
                    throw new ProductionPalletPlanException(
                        ProductionPalletPlanErrorCodes.PalletOverCapacity,
                        $"Сумма на паллете ({FingerprintQty(total)}) превышает вместимость {FingerprintQty(capacity)}.");
                }

                canonicalPallets.Add(byLine.Select(pair => (pair.Key, pair.Value)).ToArray());
                foreach (var pair in byLine)
                {
                    allocatedByLine[pair.Key] = allocatedByLine.GetValueOrDefault(pair.Key) + pair.Value;
                }
            }

            // 9. Exact coverage: allocated per line must equal the current shortfall.
            var mismatches = new List<LineAllocationMismatchDetail>();
            foreach (var lineId in requiredByLine.Keys.Union(allocatedByLine.Keys))
            {
                var required = requiredByLine.GetValueOrDefault(lineId);
                var allocated = allocatedByLine.GetValueOrDefault(lineId);
                if (Math.Abs(required - allocated) > QtyTolerance)
                {
                    mismatches.Add(new LineAllocationMismatchDetail(
                        lineId,
                        Math.Round(required, 6),
                        Math.Round(allocated, 6),
                        Math.Round(allocated - required, 6)));
                }
            }

            if (mismatches.Count > 0)
            {
                // Details stay a typed list; the endpoint owns the snake_case wire mapping.
                throw new ProductionPalletPlanException(
                    ProductionPalletPlanErrorCodes.LineAllocationMismatch,
                    "Распределение по строкам не совпадает с производственной нехваткой.",
                    details: (IReadOnlyList<LineAllocationMismatchDetail>)mismatches);
            }

            // 10. Append only new doc_lines/pallets; existing plan is untouched.
            var targetLocation = ResolveProductionPalletPlanLocation(store);
            var existingOpen = FindPreparedOpenProductionReceipt(store, orderId, requireRemaining: false);
            prdDocId = existingOpen?.Id
                ?? FindReusableEmptyProductionReceipt(store, orderId)?.Id
                ?? CreateProductionReceipt(store, order).Id;
            wasExisting = existingOpen != null;

            foreach (var pallet in canonicalPallets)
            {
                var huCode = store.CreateProductionPalletHuCode(PlanHuCreatedBy);
                foreach (var (componentLineId, qty) in pallet)
                {
                    var orderLine = orderLinesById[componentLineId];
                    store.AddDocLine(new DocLine
                    {
                        DocId = prdDocId,
                        OrderLineId = componentLineId,
                        ProductionPurpose = orderLine.ProductionPurpose,
                        ItemId = orderLine.ItemId,
                        Qty = qty,
                        QtyInput = null,
                        UomCode = null,
                        FromLocationId = null,
                        ToLocationId = targetLocation.Id,
                        FromHu = null,
                        ToHu = huCode,
                        PackSingleHu = true
                    });
                }
            }

            store.PlanProductionPallets(prdDocId, DateTime.Now);

            // The new preview fingerprint is part of the atomic confirm result: recomputed
            // in the same transaction, before the orders row lock is released. Callers must
            // not re-read business state after commit to obtain it.
            newFingerprint = BuildPlanPreviewInStore(store, order).PreviewFingerprint;
        });

        return BuildOrderPlanResult(orderId, prdDocId, wasExisting, newFingerprint);
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
        foreach (var line in remainingLines)
        {
            if (!itemsById.ContainsKey(line.ItemId))
            {
                throw new InvalidOperationException("Номенклатура строки заказа не найдена.");
            }

            // Legacy mixed grouping via order_lines.production_pallet_group is no longer a
            // planning source: it could produce an over-capacity mixed pallet. Fail fast (before
            // any writes) and route the operator to the explicit pallet constructor.
            if (orderLinesById.TryGetValue(line.OrderLineId, out var orderLine)
                && !string.IsNullOrWhiteSpace(orderLine.ProductionPalletGroup))
            {
                throw new ProductionPalletPlanException(
                    ProductionPalletPlanErrorCodes.LegacyMixedGroupNotSupported,
                    "Устаревшая группировка mixed-паллет больше не поддерживается. Используйте конструктор паллет.");
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

        // Server /plan path is single-item only: every planned line is split by its own
        // items.max_qty_per_hu. Mixed pallets are produced exclusively by the explicit
        // constructor (plan-explicit), where equal-cap is enforced.
        foreach (var line in remainingLines)
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

        IReadOnlyList<long> readyOrderIds;
        try
        {
            readyOrderIds = _data.GetProductionFillingReadyOrderIds();
        }
        catch (Exception ex) when (IsMockStoreException(ex))
        {
            readyOrderIds = Array.Empty<long>();
        }

        var candidateOrderIds = workOrderIds
            .Concat(readyOrderIds)
            .Distinct()
            .ToArray();
        if (candidateOrderIds.Length == 0)
        {
            return Array.Empty<ProductionFillingOrder>();
        }

        var ordersById = LoadOrdersById(candidateOrderIds);
        var palletsByOrderId = LoadPalletsByOrderId(candidateOrderIds);
        var orderLinesByOrderId = LoadOrderLinesByOrderId(candidateOrderIds);
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
                completions);
            if (row != null && !ShouldExcludeFromFillingList(row))
            {
                rows[row.OrderId] = row;
            }
        }

        foreach (var orderId in readyOrderIds)
        {
            if (rows.ContainsKey(orderId)
                || !ordersById.TryGetValue(orderId, out var order)
                || IsTerminalFillingOrder(order))
            {
                continue;
            }

            var row = BuildReadyFillingOrderFromPreloaded(order, palletsByOrderId, orderLinesByOrderId, completions);
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

        var pallets = GetProductionPalletsByOrder(_data, orderId);
        var fillingPallets = BuildFillingPalletViews(_data, orderId, pallets);
        var openDoc = FindPreparedOpenProductionReceiptForFilling(_data, orderId, fillingPallets, requireRemaining: false);
        if (openDoc == null)
        {
            var progress = BuildOperationProgress(_data, orderId, fillingPallets);
            if (progress.CanClose && fillingPallets.Count > 0)
            {
                return BuildFillingContext(orderId, fillingPallets[0].PrdDocId, fillingPallets);
            }

            if (HasCompletedPalletizedProduction(fillingPallets))
            {
                throw new InvalidOperationException("Выпуск по заказу уже завершён. Нет паллет к наполнению.");
            }

            throw new InvalidOperationException("Для заказа не сформирован план паллет. Сформируйте и напечатайте паллетные этикетки перед наполненением.");
        }

        return BuildFillingContext(orderId, openDoc.Id, fillingPallets);
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

        if (!HasOnlyValidFillingPalletLines(_data, pallet))
        {
            return ProductionPalletScanResult.Failure(
                ProductionFillingErrorCodes.PalletPlanInvalid, "Строка заказа для паллеты не найдена.");
        }

        var palletLines = GetPalletLines(pallet);
        var docLinesById = _data.GetDocLines(doc.Id).ToDictionary(line => line.Id, line => line);
        foreach (var palletLine in palletLines)
        {
            if (!docLinesById.TryGetValue(palletLine.DocLineId, out var docLine)
                || docLine.ItemId != palletLine.ItemId
                || docLine.OrderLineId != palletLine.OrderLineId)
            {
                return ProductionPalletScanResult.Failure(
                    ProductionFillingErrorCodes.PalletPlanInvalid, "План паллеты не совпадает со строкой выпуска.");
            }

            if (pallet.OrderId.HasValue && palletLine.OrderLineId.HasValue)
            {
                var orderLine = _data.GetOrderLines(pallet.OrderId.Value)
                    .FirstOrDefault(line => line.Id == palletLine.OrderLineId.Value);
                if (orderLine == null || orderLine.ItemId != palletLine.ItemId)
                {
                    return ProductionPalletScanResult.Failure(
                        ProductionFillingErrorCodes.PalletPlanInvalid, "Строка заказа для паллеты не найдена.");
                }

                if (!string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                {
                    var alreadyFilled = GetFillGuardFilledQty(_data, pallet.OrderId.Value, orderLine.Id, pallet.Id);
                    if (alreadyFilled + palletLine.PlannedQty > orderLine.QtyOrdered + QtyTolerance)
                    {
                        return ProductionPalletScanResult.Failure(
                            ProductionFillingErrorCodes.FillExceedsRemaining, "Выпуск превышает остаток по строке заказа");
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

    public ProductionPalletFillResult Fill(string? huCode, string? deviceId, long? orderId = null, long? prdDocId = null)
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
                var pallet = store.GetProductionPalletByHuForUpdate(normalizedHu);
                if (pallet == null)
                {
                    result = ProductionPalletFillResult.Failure(
                        ProductionFillingErrorCodes.PalletNotFound, "Паллета не найдена в плане выпуска.");
                    return;
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

                if (!HasOnlyValidFillingPalletLines(store, pallet))
                {
                    result = ProductionPalletFillResult.Failure(
                        ProductionFillingErrorCodes.PalletPlanInvalid, "Строка заказа для паллеты не найдена.");
                    return;
                }

                if (string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                {
                    result = ApplyAutoCloseAfterFillInTransaction(store, new ProductionPalletFillResult
                    {
                        Success = true,
                        Error = ProductionFillingErrorCodes.PalletAlreadyFilled,
                        ErrorMessage = "Паллета уже наполнена.",
                        AlreadyFilled = true,
                        Pallet = pallet,
                        Document = BuildFillingDocument(doc.Id, store.GetProductionPalletsByDoc(doc.Id), pallet.OrderId)
                    });
                    return;
                }

                if (pallet.IsMixedPallet)
                {
                    result = ProductionPalletFillResult.Failure(
                        ProductionFillingErrorCodes.MixedComponentSelectionRequired,
                        "Выберите хотя бы один незаполненный компонент микс-паллеты.");
                    return;
                }

                var palletLines = GetPalletLines(pallet);
                var docLinesById = store.GetDocLines(doc.Id).ToDictionary(line => line.Id, line => line);
                foreach (var palletLine in palletLines)
                {
                    if (!docLinesById.TryGetValue(palletLine.DocLineId, out var docLine))
                    {
                        result = ProductionPalletFillResult.Failure(
                            ProductionFillingErrorCodes.PalletPlanInvalid, "Строка паллеты не найдена в документе выпуска.");
                        return;
                    }

                    if (docLine.ItemId != palletLine.ItemId || docLine.OrderLineId != palletLine.OrderLineId)
                    {
                        result = ProductionPalletFillResult.Failure(
                            ProductionFillingErrorCodes.PalletPlanInvalid, "План паллеты не совпадает со строкой выпуска.");
                        return;
                    }

                    if (!docLine.ToLocationId.HasValue)
                    {
                        result = ProductionPalletFillResult.Failure(
                            ProductionFillingErrorCodes.PalletPlanInvalid, "Для паллеты не указано место хранения.");
                        return;
                    }

                    if (pallet.OrderId.HasValue && palletLine.OrderLineId.HasValue)
                    {
                        var orderLine = store.GetOrderLines(pallet.OrderId.Value)
                            .FirstOrDefault(line => line.Id == palletLine.OrderLineId.Value);
                        if (orderLine == null || orderLine.ItemId != palletLine.ItemId)
                        {
                            result = ProductionPalletFillResult.Failure(
                                ProductionFillingErrorCodes.PalletPlanInvalid, "Строка заказа для паллеты не найдена.");
                            return;
                        }

                        var alreadyFilled = GetFillGuardFilledQty(store, pallet.OrderId.Value, orderLine.Id, pallet.Id);
                        if (alreadyFilled + palletLine.PlannedQty > orderLine.QtyOrdered + QtyTolerance)
                        {
                            result = ProductionPalletFillResult.Failure(
                                ProductionFillingErrorCodes.FillExceedsRemaining, "Выпуск превышает остаток по строке заказа");
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

    public ProductionPalletFillResult FillMixedComponents(
        string? huCode,
        IReadOnlyCollection<long>? componentLineIds,
        string? deviceId,
        long? orderId = null,
        long? prdDocId = null)
    {
        var normalizedHu = NormalizeHu(huCode);
        var requestedIds = (componentLineIds ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (string.IsNullOrWhiteSpace(normalizedHu))
        {
            return ProductionPalletFillResult.Failure(
                ProductionFillingErrorCodes.HuRequired, "Укажите код паллеты.");
        }

        if (requestedIds.Length == 0)
        {
            return ProductionPalletFillResult.Failure(
                "COMPONENT_LINE_IDS_REQUIRED", "Выберите хотя бы один незаполненный компонент микс-паллеты.");
        }

        if (_fillClose == null || !_fillClose.AutoCloseEnabled)
        {
            return ProductionPalletFillResult.Failure(
                "PRODUCTION_AUTO_CLOSE_REQUIRED",
                "Частичное наполнение микс-паллеты требует включённого автоматического проведения выпуска.");
        }

        ProductionPalletFillResult? result = null;
        try
        {
            _data.ExecuteInTransaction(store =>
            {
                var pallet = store.GetProductionPalletByHuForUpdate(normalizedHu);
                if (pallet == null)
                {
                    result = ProductionPalletFillResult.Failure(
                        ProductionFillingErrorCodes.PalletNotFound, "Паллета не найдена в плане выпуска.");
                    return;
                }

                // Same unified classification/priority as Scan and Fill.
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

                if (string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
                {
                    result = ApplyAutoCloseAfterFillInTransaction(store, BuildMixedFillResult(
                        pallet,
                        alreadyFilled: true,
                        ledgerWritten: doc.Status == DocStatus.Closed,
                        message: "Микс-паллета уже наполнена."));
                    return;
                }

                if (doc.Status == DocStatus.Closed)
                {
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

                if (!pallet.IsMixedPallet)
                {
                    result = ProductionPalletFillResult.Failure(
                        "PALLET_NOT_MIXED", "Эта паллета не является микс-паллетой.");
                    return;
                }

                if (!HasOnlyValidFillingPalletLines(store, pallet))
                {
                    result = ProductionPalletFillResult.Failure(
                        ProductionFillingErrorCodes.PalletPlanInvalid, "Строка заказа для паллеты не найдена.");
                    return;
                }

                if (requestedIds.Any(id => pallet.Lines.All(line => line.Id != id)))
                {
                    result = ProductionPalletFillResult.Failure(
                        "COMPONENT_NOT_IN_PALLET", "Состав паллеты изменился. Отсканируйте HU повторно.");
                    return;
                }

                store.MarkProductionPalletComponentsFilled(pallet.Id, requestedIds, DateTime.Now);
                var updated = store.GetProductionPalletByHu(normalizedHu) ?? pallet;
                if (!updated.AreAllComponentsFilled)
                {
                    result = BuildMixedFillResult(
                        updated,
                        alreadyFilled: requestedIds.All(id => pallet.Lines.First(line => line.Id == id).IsCompleted),
                        ledgerWritten: false,
                        message: "Компоненты отмечены как наполненные. HU ещё не готов полностью.");
                    return;
                }

                store.MarkProductionPalletFilled(updated.Id, DateTime.Now, NormalizeDeviceId(deviceId));
                var filled = store.GetProductionPalletByHu(normalizedHu) ?? updated;
                result = ApplyAutoCloseAfterFillInTransaction(store, BuildMixedFillResult(
                    filled,
                    alreadyFilled: false,
                    ledgerWritten: false,
                    message: "Микс-паллета полностью наполнена и проведена."));
            });
        }
        catch (ProductionPalletFillRollbackException ex)
        {
            return ProductionPalletFillResult.Failure(ex.Message);
        }

        return result ?? ProductionPalletFillResult.Failure(
            "MIXED_COMPONENT_FILL_FAILED", "Не удалось наполнить компоненты микс-паллеты.");
    }

    private ProductionPalletFillResult BuildMixedFillResult(
        ProductionPallet pallet,
        bool alreadyFilled,
        bool ledgerWritten,
        string message)
    {
        return new ProductionPalletFillResult
        {
            Success = true,
            Error = alreadyFilled ? ProductionFillingErrorCodes.PalletAlreadyFilled : null,
            ErrorMessage = alreadyFilled ? "Паллета уже наполнена." : null,
            AlreadyFilled = alreadyFilled,
            Pallet = pallet,
            EffectiveStatus = pallet.EffectiveStatus,
            FilledComponentCount = pallet.FilledComponentCount,
            TotalComponentCount = pallet.TotalComponentCount,
            LedgerWritten = ledgerWritten,
            Message = message
        };
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
        var completions = LoadFillingCompletions(new[] { orderId });
        return BuildFillingOrderFromPreloaded(order, workItems, palletsByOrderId, orderLinesByOrderId, completions);
    }

    private ProductionFillingOrder? BuildFillingOrderFromPreloaded(
        Order order,
        IReadOnlyList<ProductionPalletWorkItem> workItems,
        IReadOnlyDictionary<long, IReadOnlyList<ProductionPallet>> palletsByOrderId,
        IReadOnlyDictionary<long, IReadOnlyList<OrderLine>> orderLinesByOrderId,
        IReadOnlyList<ProductionFillingCompletion> completions)
    {
        if (order.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            return null;
        }

        palletsByOrderId.TryGetValue(order.Id, out var rawPallets);
        rawPallets ??= Array.Empty<ProductionPallet>();
        orderLinesByOrderId.TryGetValue(order.Id, out var orderLines);
        orderLines ??= Array.Empty<OrderLine>();
        var orderLinesById = orderLines.ToDictionary(line => line.Id);

        var fillingPallets = BuildOrderOwnedPalletViews(order.Id, rawPallets, orderLinesById);
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
            BuildOperationProgress(order.Id, fillingPallets, completions));
    }

    private ProductionFillingOrder? BuildReadyFillingOrderFromPreloaded(
        Order order,
        IReadOnlyDictionary<long, IReadOnlyList<ProductionPallet>> palletsByOrderId,
        IReadOnlyDictionary<long, IReadOnlyList<OrderLine>> orderLinesByOrderId,
        IReadOnlyList<ProductionFillingCompletion> completions)
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
        return MapProductionFillingOrder(order, docId, docRef, BuildSummary(pallets), progress);
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
        ProductionOperationProgress progress)
    {
        return new ProductionFillingOrder
        {
            OrderId = order.Id,
            OrderRef = order.OrderRef,
            OrderType = OrderStatusMapper.TypeToString(order.Type),
            OrderTypeDisplay = OrderStatusMapper.TypeToDisplayName(order.Type),
            OrderStatus = OrderStatusMapper.StatusToString(order.Status),
            OrderStatusDisplay = OrderStatusMapper.StatusToDisplayName(order.Status, order.Type),
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
            Document = BuildFillingDocument(doc.Id, pallets),
            Progress = BuildOperationProgress(_data, orderId, pallets)
        };
    }

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
        var required = pallets.Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase)).ToList();
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

    private ProductionPalletOrderPlanResult BuildOrderPlanResult(
        long orderId,
        long prdDocId,
        bool wasExisting,
        string? newPreviewFingerprint = null)
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
            Document = document,
            NewPreviewFingerprint = newPreviewFingerprint ?? string.Empty
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
        var palletsToCancel = openPallets.Where(pallet => palletIdsToCancel.Contains(pallet.Id)).ToArray();
        if (palletsToCancel.Any(pallet => pallet.HasComponentProgress))
        {
            throw new InvalidOperationException("Паллетный план находится в фактическом состоянии: есть частично наполненная микс-паллета.");
        }

        TombstoneProductionPalletDocLines(store, palletsToCancel);
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

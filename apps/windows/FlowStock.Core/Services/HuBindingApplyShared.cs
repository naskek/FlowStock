using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

/// <summary>
/// Общие чистые вспомогательные методы для применения окончательного набора готовых HU
/// к строкам клиентского заказа. Используются как order-scoped сервисом
/// <see cref="OrderHuBindingApplyFinalService"/>, так и (в дальнейшем) batch-сервисом
/// управления привязками. Перенесены без изменения поведения.
/// </summary>
internal static class HuBindingApplyShared
{
    internal const double QtyTolerance = StockQuantityRules.QtyTolerance;
    internal const string DetachPlanSyncSource = "HU_BINDING_DETACH";

    internal static OrderHuBindingApplyFinalException Error(
        string code,
        string message,
        IReadOnlyList<string>? problems = null) =>
        new(code, message, problems);

    internal static void ValidateRequestShape(OrderHuBindingApplyFinalRequest request)
    {
        if (!string.Equals(request.Mode, OrderHuBindingApplyFinalRequest.ReplaceFinalSelectionMode, StringComparison.Ordinal))
        {
            throw Error("INVALID_REQUEST", "Некорректный режим применения HU.");
        }

        if (request.Lines == null || request.Lines.Count == 0)
        {
            throw Error("INVALID_REQUEST", "Не переданы строки для применения HU.");
        }

        var lineIds = new HashSet<long>();
        foreach (var line in request.Lines)
        {
            if (line.OrderLineId <= 0)
            {
                throw Error("ORDER_LINE_NOT_FOUND", "Строка заказа не указана.");
            }

            if (!lineIds.Add(line.OrderLineId))
            {
                throw Error("INVALID_REQUEST", $"Строка {line.OrderLineId} передана более одного раза.");
            }

            if (line.ExpectedBoundHuCodes == null)
            {
                throw Error("INVALID_REQUEST", "Не передан expected_bound_hu_codes.");
            }

            if (line.FinalHuCodes == null)
            {
                throw Error("INVALID_REQUEST", "Не передан final_hu_codes.");
            }
        }
    }

    internal static Dictionary<string, HuReservationCandidateResult> BuildCandidatesByHu(
        HuReservationCandidatesService candidatesService,
        long customerOrderId,
        OrderLine orderLine)
    {
        var result = candidatesService.Build(new HuReservationCandidatesQuery
        {
            OrderId = customerOrderId,
            Lines =
            [
                new HuReservationCandidatesLineQuery
                {
                    ClientLineKey = orderLine.Id.ToString(),
                    OrderLineId = orderLine.Id,
                    ItemId = orderLine.ItemId,
                    QtyOrdered = orderLine.QtyOrdered
                }
            ],
            ExcludeHuCodes = Array.Empty<string>()
        });

        return result.Lines
            .SelectMany(line => line.Candidates)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.HuCode))
            .GroupBy(candidate => NormalizeHu(candidate.HuCode)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<OrderReceiptPlanLine> BuildReplacementPlanLines(
        long customerOrderId,
        OrderLine orderLine,
        IReadOnlyList<HuReservationCandidateResult> finalCandidates)
    {
        var replacementLines = new List<OrderReceiptPlanLine>(finalCandidates.Count);
        for (var index = 0; index < finalCandidates.Count; index++)
        {
            var candidate = finalCandidates[index];
            replacementLines.Add(new OrderReceiptPlanLine
            {
                OrderId = customerOrderId,
                OrderLineId = orderLine.Id,
                ItemId = orderLine.ItemId,
                QtyPlanned = candidate.Qty,
                ToHu = candidate.HuCode,
                SortOrder = index
            });
        }

        return replacementLines;
    }

    internal static void RestoreProductionPlanForOrderLine(
        IDataStore store,
        long customerOrderId,
        long orderLineId,
        double orderedQty)
    {
        new ProductionPalletService(store).SyncOrderLinePlanInStore(
            store,
            customerOrderId,
            orderLineId,
            orderedQty,
            oldOrderedQty: null,
            source: DetachPlanSyncSource);
    }

    internal static long[] SelectSafeWholePlannedPalletsToCancel(
        IDataStore store,
        long customerOrderId,
        OrderLine orderLine,
        double qtyToCover)
    {
        var activePallets = GetActiveProductionPalletsForOrderLine(store, customerOrderId, orderLine.Id).ToArray();
        if (activePallets.Length == 0)
        {
            return [];
        }

        var safe = new List<(ProductionPallet Pallet, double Qty)>();
        foreach (var pallet in activePallets)
        {
            if (!IsSafeWholePlannedCustomerPallet(customerOrderId, orderLine, pallet, out var qty, out var rejectionReason))
            {
                throw Error(
                    "HU_BINDING_PLAN_CONFLICT",
                    "Строка имеет производственные паллеты, которые нельзя безопасно заменить готовым HU.",
                    [$"production_pallet_id={pallet.Id}", rejectionReason ?? "unsafe_pallet_state"]);
            }

            safe.Add((pallet, qty));
        }

        var selected = SelectExactPalletSubset(safe, qtyToCover);
        if (selected.Count == 0)
        {
            throw Error(
                "HU_BINDING_PLAN_CONFLICT",
                "Готовый HU не может заменить целые плановые паллеты без частичной отмены.",
                [$"order_line_id={orderLine.Id}", $"qty_to_cover={qtyToCover:0.###}"]);
        }

        return selected.Select(pallet => pallet.Id).ToArray();
    }

    internal static bool IsSafeWholePlannedCustomerPallet(
        long customerOrderId,
        OrderLine orderLine,
        ProductionPallet pallet,
        out double qty,
        out string rejectionReason)
    {
        qty = ResolvePalletQtyForOrderLine(pallet, orderLine.Id);
        if (qty <= QtyTolerance)
        {
            rejectionReason = "no_planned_qty_for_order_line";
            return false;
        }

        if (pallet.OrderId != customerOrderId)
        {
            rejectionReason = $"order_id_mismatch expected={customerOrderId} actual={pallet.OrderId}";
            return false;
        }

        if (!string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase))
        {
            rejectionReason = $"status={pallet.Status ?? "(null)"} expected=PLANNED";
            return false;
        }

        if (pallet.PrintedAt.HasValue)
        {
            rejectionReason = $"printed_at={pallet.PrintedAt:O}";
            return false;
        }

        if (pallet.FilledAt.HasValue)
        {
            rejectionReason = $"filled_at={pallet.FilledAt:O}";
            return false;
        }

        var lines = pallet.Lines ?? Array.Empty<ProductionPalletComponentLine>();
        if (lines.Count > 1)
        {
            rejectionReason = "mixed_component_lines";
            return false;
        }

        var unsafeComponentLine = lines.FirstOrDefault(line => line.FilledQty > QtyTolerance
                                                               || line.OrderLineId != orderLine.Id
                                                               || line.ItemId != orderLine.ItemId);
        if (unsafeComponentLine != null)
        {
            if (unsafeComponentLine.FilledQty > QtyTolerance)
            {
                rejectionReason = $"component_filled_qty={unsafeComponentLine.FilledQty:0.###}";
            }
            else
            {
                rejectionReason = "order_line_or_item_mismatch";
            }

            return false;
        }

        if (lines.Count == 0 && (pallet.OrderLineId != orderLine.Id || pallet.ItemId != orderLine.ItemId))
        {
            rejectionReason = "order_line_or_item_mismatch";
            return false;
        }

        rejectionReason = string.Empty;
        return true;
    }

    internal static IReadOnlyList<ProductionPallet> GetActiveProductionPalletsForOrderLine(
        IDataStore store,
        long orderId,
        long orderLineId)
    {
        return store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.ProductionReceipt)
            .SelectMany(doc => store.GetProductionPalletsByDoc(doc.Id))
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.Ordinal))
            .Where(pallet => PalletAppliesToOrderLine(pallet, orderLineId))
            .OrderBy(pallet => pallet.Id)
            .ToArray();
    }

    internal static double SumActiveProductionPalletQty(
        IDataStore store,
        long orderId,
        long orderLineId)
    {
        return GetActiveProductionPalletsForOrderLine(store, orderId, orderLineId)
            .Sum(pallet => ResolvePalletQtyForOrderLine(pallet, orderLineId));
    }

    internal static bool PalletAppliesToOrderLine(ProductionPallet pallet, long orderLineId)
    {
        return pallet.OrderLineId == orderLineId
               || (pallet.Lines ?? Array.Empty<ProductionPalletComponentLine>())
               .Any(line => line.OrderLineId == orderLineId);
    }

    internal static double ResolvePalletQtyForOrderLine(ProductionPallet pallet, long orderLineId)
    {
        var componentQty = (pallet.Lines ?? Array.Empty<ProductionPalletComponentLine>())
            .Where(line => line.OrderLineId == orderLineId)
            .Sum(line => Math.Max(0, line.PlannedQty));
        return componentQty > QtyTolerance ? componentQty : Math.Max(0, pallet.PlannedQty);
    }

    internal static IReadOnlyList<ProductionPallet> SelectExactPalletSubset(
        IReadOnlyList<(ProductionPallet Pallet, double Qty)> pallets,
        double targetQty)
    {
        var targetUnits = ToQtyUnits(targetQty);
        if (targetUnits <= 0)
        {
            return [];
        }

        var bestByTotal = new Dictionary<long, List<ProductionPallet>>
        {
            [0] = []
        };
        foreach (var entry in pallets.OrderBy(entry => entry.Pallet.Id))
        {
            var units = ToQtyUnits(entry.Qty);
            if (units <= 0)
            {
                continue;
            }

            foreach (var snapshot in bestByTotal.ToArray())
            {
                var total = snapshot.Key + units;
                if (total > targetUnits || bestByTotal.ContainsKey(total))
                {
                    continue;
                }

                var selected = new List<ProductionPallet>(snapshot.Value) { entry.Pallet };
                bestByTotal[total] = selected;
            }
        }

        return bestByTotal.TryGetValue(targetUnits, out var exact)
            ? exact
            : Array.Empty<ProductionPallet>();
    }

    internal static long ToQtyUnits(double qty) => (long)Math.Round(Math.Max(0, qty) * 1_000_000d);

    internal static double ResolveShipmentRemaining(
        OrderLine orderLine,
        IReadOnlyDictionary<long, OrderShipmentLine> shipmentRemainingByLine)
    {
        return shipmentRemainingByLine.TryGetValue(orderLine.Id, out var shipmentLine)
            ? Math.Max(0, shipmentLine.QtyRemaining)
            : Math.Max(0, orderLine.QtyOrdered);
    }

    internal static void ValidateDuplicateHuInFinalSelection(
        IReadOnlyList<string> finalHuCodes,
        long orderLineId,
        IDictionary<string, long> huToOrderLine)
    {
        foreach (var huCode in finalHuCodes)
        {
            if (huToOrderLine.TryGetValue(huCode, out var existingOrderLineId)
                && existingOrderLineId != orderLineId)
            {
                throw Error(
                    "DUPLICATE_HU_IN_REQUEST",
                    $"HU '{huCode}' выбран более одного раза в одном запросе.",
                    [$"HU '{huCode}': lines {existingOrderLineId} и {orderLineId}"]);
            }

            huToOrderLine[huCode] = orderLineId;
        }
    }

    internal static void ValidateHuNotReservedOnOtherUnaffectedLine(
        long customerOrderId,
        long orderLineId,
        IReadOnlyList<string> finalHuCodes,
        IReadOnlySet<long> affectedOrderLineIds,
        IReadOnlyList<OrderReceiptPlanLine> existingPlanLines)
    {
        if (finalHuCodes.Count == 0)
        {
            return;
        }

        var finalSet = finalHuCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var planLine in existingPlanLines)
        {
            var huCode = NormalizeHu(planLine.ToHu);
            if (planLine.OrderId != customerOrderId
                || planLine.OrderLineId == orderLineId
                || affectedOrderLineIds.Contains(planLine.OrderLineId)
                || string.IsNullOrWhiteSpace(huCode)
                || !finalSet.Contains(huCode))
            {
                continue;
            }

            throw Error(
                "HU_RESERVED_BY_OTHER_ORDER",
                $"HU '{huCode}' уже зарезервирован другой строкой этого заказа.",
                [$"HU '{huCode}': order_line_id={planLine.OrderLineId}"]);
        }
    }

    internal static HashSet<string> NormalizeHuSet(IReadOnlyList<string>? huCodes) =>
        NormalizeHuList(huCodes).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> NormalizeHuList(IReadOnlyList<string>? huCodes)
    {
        if (huCodes == null || huCodes.Count == 0)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var huCode in huCodes)
        {
            var normalized = NormalizeHu(huCode);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    internal static string? NormalizeHu(string? huCode)
    {
        return string.IsNullOrWhiteSpace(huCode)
            ? null
            : huCode.Trim().ToUpperInvariant();
    }

    internal static Order CopyOrderWithReservedStock(Order order)
    {
        return new Order
        {
            Id = order.Id,
            OrderRef = order.OrderRef,
            Type = order.Type,
            PartnerId = order.PartnerId,
            DueDate = order.DueDate,
            Status = order.Status,
            Comment = order.Comment,
            CreatedAt = order.CreatedAt,
            ShippedAt = order.ShippedAt,
            PartnerName = order.PartnerName,
            PartnerCode = order.PartnerCode,
            UseReservedStock = true,
            MarkingStatus = order.MarkingStatus,
            IsLegacyExcelGeneratedMarkingStatus = order.IsLegacyExcelGeneratedMarkingStatus,
            MarkingRequired = order.MarkingRequired,
            MarkingApplies = order.MarkingApplies,
            MarkingCodeCovered = order.MarkingCodeCovered,
            MarkingExcelGeneratedAt = order.MarkingExcelGeneratedAt,
            MarkingPrintedAt = order.MarkingPrintedAt
        };
    }
}

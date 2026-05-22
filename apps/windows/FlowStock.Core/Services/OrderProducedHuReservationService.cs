using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class OrderProducedHuReservationService
{
    private const double QtyTolerance = 0.000001d;
    private readonly IDataStore _data;

    public OrderProducedHuReservationService(IDataStore data)
    {
        _data = data;
    }

    public OrderProducedHuReservationResult Reserve(OrderProducedHuReservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        OrderProducedHuReservationResult? result = null;
        _data.ExecuteInTransaction(store =>
        {
            result = ReserveCore(store, request);
        });

        return result ?? throw new InvalidOperationException("Резервирование готовых HU не выполнено.");
    }

    internal static OrderProducedHuReservationResult ReserveCore(
        IDataStore store,
        OrderProducedHuReservationRequest request)
    {
        if (request.TargetCustomerOrderId <= 0
            || request.SourceInternalOrderId <= 0
            || request.ItemId <= 0)
        {
            throw new InvalidOperationException("Некорректный запрос резервирования готовых HU.");
        }

        var sourceOrder = store.GetOrder(request.SourceInternalOrderId)
                          ?? throw new InvalidOperationException("Внутренний заказ-источник не найден.");
        var targetOrder = store.GetOrder(request.TargetCustomerOrderId)
                          ?? throw new InvalidOperationException("Клиентский заказ-получатель не найден.");

        if (sourceOrder.Type != OrderType.Internal)
        {
            throw new InvalidOperationException("Источник резервирования должен быть внутренним заказом.");
        }

        if (targetOrder.Type != OrderType.Customer)
        {
            throw new InvalidOperationException("Получатель резервирования должен быть клиентским заказом.");
        }

        if (sourceOrder.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
        {
            throw new InvalidOperationException("Внутренний заказ-источник недоступен для резервирования.");
        }

        if (targetOrder.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            throw new InvalidOperationException("Клиентский заказ-получатель недоступен для резервирования.");
        }

        if (request.SourceInternalOrderId == request.TargetCustomerOrderId)
        {
            throw new InvalidOperationException("Нельзя зарезервировать HU в тот же заказ.");
        }

        if (!targetOrder.UseReservedStock)
        {
            throw new InvalidOperationException(
                "Для резервирования выпущенных HU у клиентского заказа должен быть включен резерв складского остатка.");
        }

        if (!ItemTypeUsesOrderReservation(store, request.ItemId))
        {
            throw new InvalidOperationException(
                "Тип номенклатуры не поддерживает резервирование HU под клиентский заказ.");
        }

        var sourceLine = ResolveSourceLine(store, request.SourceInternalOrderId, request.ItemId);
        var targetLine = ResolveTargetLine(
            store,
            request.TargetCustomerOrderId,
            request.ItemId,
            request.TargetOrderLineId);

        var sourceQtyBefore = sourceLine.QtyOrdered;
        var producedBefore = OrderReceiptRemainingCalculator.BuildProducedTotalsByOrderLine(
            store,
            request.SourceInternalOrderId,
            new[] { sourceLine });
        var producedQtyBefore = producedBefore.TryGetValue(sourceLine.Id, out var producedQty) ? producedQty : 0d;

        var huCodes = ResolveHuCodes(store, request);
        if (huCodes.Count == 0)
        {
            throw new InvalidOperationException("Не указаны готовые HU для резервирования.");
        }

        var stockByHu = store.GetHuStockRows()
            .Where(row => row.ItemId == request.ItemId && row.Qty > QtyTolerance)
            .GroupBy(row => NormalizeHu(row.HuCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key!, group => group.Sum(entry => entry.Qty), StringComparer.OrdinalIgnoreCase);

        var contextByHu = store.GetHuOrderContextRows()
            .Where(row => row.ItemId == request.ItemId)
            .GroupBy(row => NormalizeHu(row.HuCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key!, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var customerPlan = store.GetOrderReceiptPlanLines(request.TargetCustomerOrderId).ToList();
        var nextSortOrder = customerPlan.Count == 0
            ? 0
            : customerPlan.Max(line => line.SortOrder) + 1;
        var reservedHuCodes = new List<string>();
        var reservedQty = 0d;
        var palletOrderIdsBefore = CaptureFilledPalletOrderIds(
            store,
            request.SourceInternalOrderId,
            request.ItemId,
            huCodes);

        foreach (var huCode in huCodes)
        {
            if (!TryGetReadyHuFromInternal(
                    store,
                    request.SourceInternalOrderId,
                    request.ItemId,
                    huCode,
                    out var readyPallet,
                    out var validationError))
            {
                throw new InvalidOperationException(validationError);
            }

            if (!stockByHu.TryGetValue(huCode, out var stockQty) || stockQty <= QtyTolerance)
            {
                throw new InvalidOperationException(
                    $"HU {huCode} не имеет положительного остатка на складе для резервирования.");
            }

            if (contextByHu.TryGetValue(huCode, out var context)
                && context.ReservedCustomerOrderId.HasValue
                && context.ReservedCustomerOrderId.Value != request.TargetCustomerOrderId)
            {
                throw new InvalidOperationException($"HU {huCode} уже зарезервирован под другой клиентский заказ.");
            }

            if (customerPlan.Any(line => string.Equals(NormalizeHu(line.ToHu), huCode, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var takeQty = Math.Min(stockQty, readyPallet!.PlannedQty);
            if (takeQty <= QtyTolerance)
            {
                throw new InvalidOperationException($"HU {huCode} не содержит выпущенного объема для резервирования.");
            }

            var locationId = customerPlan.FirstOrDefault(line => line.ToLocationId.HasValue)?.ToLocationId
                             ?? readyPallet.ToLocationId
                             ?? store.GetLocations().FirstOrDefault()?.Id;
            customerPlan.Add(new OrderReceiptPlanLine
            {
                OrderId = request.TargetCustomerOrderId,
                OrderLineId = targetLine.Id,
                ItemId = request.ItemId,
                QtyPlanned = takeQty,
                ToLocationId = locationId,
                ToHu = huCode,
                SortOrder = nextSortOrder++
            });
            reservedHuCodes.Add(huCode);
            reservedQty += takeQty;
            AppendReplacementPlannedHu(store, sourceLine, readyPallet!, takeQty);
        }

        if (reservedHuCodes.Count == 0)
        {
            throw new InvalidOperationException("Все указанные HU уже зарезервированы под этот клиентский заказ.");
        }

        store.ReplaceOrderReceiptPlanLines(request.TargetCustomerOrderId, customerPlan);

        var sourceLineAfter = store.GetOrderLines(request.SourceInternalOrderId)
            .First(line => line.Id == sourceLine.Id);
        if (Math.Abs(sourceLineAfter.QtyOrdered - sourceQtyBefore) > QtyTolerance)
        {
            throw new InvalidOperationException("Резервирование готовых HU не должно изменять qty_ordered внутреннего заказа.");
        }

        var producedAfter = OrderReceiptRemainingCalculator.BuildProducedTotalsByOrderLine(
            store,
            request.SourceInternalOrderId,
            new[] { sourceLineAfter });
        var producedQtyAfter = producedAfter.TryGetValue(sourceLine.Id, out var producedAfterQty) ? producedAfterQty : 0d;
        if (Math.Abs(producedQtyAfter - producedQtyBefore) > QtyTolerance)
        {
            throw new InvalidOperationException("Резервирование готовых HU не должно изменять produced qty внутреннего заказа.");
        }

        foreach (var pair in CaptureFilledPalletOrderIds(store, request.SourceInternalOrderId, request.ItemId, reservedHuCodes))
        {
            if (!palletOrderIdsBefore.TryGetValue(pair.Key, out var orderIdBefore)
                || orderIdBefore != pair.Value
                || pair.Value != request.SourceInternalOrderId)
            {
                throw new InvalidOperationException(
                    "Резервирование готовых HU не должно изменять production_pallets.order_id.");
            }
        }

        return new OrderProducedHuReservationResult
        {
            SourceInternalOrderId = request.SourceInternalOrderId,
            TargetCustomerOrderId = request.TargetCustomerOrderId,
            ItemId = request.ItemId,
            TargetOrderLineId = targetLine.Id,
            ReservedHuCodes = reservedHuCodes,
            QtyReserved = reservedQty,
            SourceQtyOrdered = sourceQtyBefore,
            SourceProducedQty = producedQtyBefore
        };
    }

    private static void AppendReplacementPlannedHu(
        IDataStore store,
        OrderLine sourceLine,
        ProductionPallet takenPallet,
        double qty)
    {
        if (qty <= QtyTolerance)
        {
            return;
        }

        var sourceDoc = store.GetDoc(takenPallet.PrdDocId);
        if (sourceDoc == null || sourceDoc.Status == DocStatus.Closed)
        {
            return;
        }

        var locationId = takenPallet.ToLocationId
                         ?? store.GetLocations()
                             .OrderBy(location => location.Code, StringComparer.OrdinalIgnoreCase)
                             .FirstOrDefault(location => location.AutoHuDistributionEnabled)?.Id
                         ?? store.GetLocations()
                             .OrderBy(location => location.Code, StringComparer.OrdinalIgnoreCase)
                             .FirstOrDefault()?.Id;
        if (!locationId.HasValue)
        {
            throw new InvalidOperationException("Нет доступной локации для replacement HU внутреннего заказа.");
        }

        store.AddDocLine(new DocLine
        {
            DocId = sourceDoc.Id,
            OrderLineId = sourceLine.Id,
            ProductionPurpose = sourceLine.ProductionPurpose,
            ItemId = sourceLine.ItemId,
            Qty = qty,
            QtyInput = null,
            UomCode = null,
            FromLocationId = null,
            ToLocationId = locationId,
            FromHu = null,
            ToHu = store.CreateProductionPalletHuCode("INTERNAL-REPLACEMENT-HU"),
            PackSingleHu = true
        });
        store.PlanProductionPallets(sourceDoc.Id, DateTime.Now);
    }

    private static IReadOnlyList<string> ResolveHuCodes(
        IDataStore store,
        OrderProducedHuReservationRequest request)
    {
        var explicitHuCodes = (request.HuCodes ?? Array.Empty<string>())
            .Select(NormalizeHu)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
        if (explicitHuCodes.Count > 0)
        {
            return explicitHuCodes;
        }

        if (request.Qty is not > QtyTolerance)
        {
            return Array.Empty<string>();
        }

        var stockByHu = store.GetHuStockRows()
            .Where(row => row.ItemId == request.ItemId && row.Qty > QtyTolerance)
            .GroupBy(row => NormalizeHu(row.HuCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key!, group => group.Sum(entry => entry.Qty), StringComparer.OrdinalIgnoreCase);

        var contextByHu = store.GetHuOrderContextRows()
            .Where(row => row.ItemId == request.ItemId)
            .GroupBy(row => NormalizeHu(row.HuCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key!, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var customerPlanHu = store.GetOrderReceiptPlanLines(request.TargetCustomerOrderId)
            .Select(line => NormalizeHu(line.ToHu))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selected = new List<string>();
        var remaining = request.Qty.Value;
        foreach (var huCode in ListReadyHuCodesFromInternal(store, request.SourceInternalOrderId, request.ItemId)
                     .OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
        {
            if (remaining <= QtyTolerance)
            {
                break;
            }

            if (customerPlanHu.Contains(huCode))
            {
                continue;
            }

            if (!stockByHu.TryGetValue(huCode, out var stockQty) || stockQty <= QtyTolerance)
            {
                continue;
            }

            if (contextByHu.TryGetValue(huCode, out var context)
                && context.ReservedCustomerOrderId.HasValue
                && context.ReservedCustomerOrderId.Value != request.TargetCustomerOrderId)
            {
                continue;
            }

            if (!TryGetReadyHuFromInternal(store, request.SourceInternalOrderId, request.ItemId, huCode, out _, out _))
            {
                continue;
            }

            selected.Add(huCode);
            remaining -= Math.Min(remaining, stockQty);
        }

        if (remaining > QtyTolerance)
        {
            throw new InvalidOperationException(
                "Недостаточно готовых HU по внутреннему заказу для резервирования запрошенного объема.");
        }

        return selected;
    }

    private static IEnumerable<string> ListReadyHuCodesFromInternal(
        IDataStore store,
        long sourceInternalOrderId,
        long itemId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in store.GetDocsByOrder(sourceInternalOrderId)
                     .Where(doc => doc.Type == DocType.ProductionReceipt))
        {
            foreach (var pallet in store.GetProductionPalletsByDoc(doc.Id)
                         .Where(pallet => pallet.ItemId == itemId
                                            && string.Equals(
                                                pallet.Status,
                                                ProductionPalletStatus.Filled,
                                                StringComparison.OrdinalIgnoreCase)))
            {
                var hu = NormalizeHu(pallet.HuCode);
                if (!string.IsNullOrWhiteSpace(hu))
                {
                    result.Add(hu);
                }
            }
        }

        return result;
    }

    private static bool TryGetReadyHuFromInternal(
        IDataStore store,
        long sourceInternalOrderId,
        long itemId,
        string huCode,
        out ProductionPallet? pallet,
        out string validationError)
    {
        pallet = null;
        validationError = string.Empty;

        foreach (var doc in store.GetDocsByOrder(sourceInternalOrderId)
                     .Where(doc => doc.Type == DocType.ProductionReceipt))
        {
            foreach (var candidate in store.GetProductionPalletsByDoc(doc.Id)
                         .Where(row => row.ItemId == itemId
                                       && string.Equals(
                                           row.Status,
                                           ProductionPalletStatus.Filled,
                                           StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(NormalizeHu(row.HuCode), huCode, StringComparison.OrdinalIgnoreCase)))
            {
                if (candidate.OrderId != sourceInternalOrderId)
                {
                    validationError =
                        $"HU {huCode} выпущен не по указанному внутреннему заказу.";
                    return false;
                }

                pallet = candidate;
                validationError = string.Empty;
                return true;
            }
        }

        validationError = $"HU {huCode} не является готовой (FILLED) паллетой внутреннего заказа.";
        return false;
    }

    private static Dictionary<string, long> CaptureFilledPalletOrderIds(
        IDataStore store,
        long sourceInternalOrderId,
        long itemId,
        IReadOnlyCollection<string> huCodes)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var huCode in huCodes)
        {
            if (TryGetReadyHuFromInternal(store, sourceInternalOrderId, itemId, huCode, out var pallet, out _)
                && pallet?.OrderId is long orderId)
            {
                result[huCode] = orderId;
            }
        }

        return result;
    }

    private static OrderLine ResolveSourceLine(IDataStore store, long sourceInternalOrderId, long itemId)
    {
        return store.GetOrderLines(sourceInternalOrderId)
                   .Where(line => line.ItemId == itemId && line.QtyOrdered > QtyTolerance)
                   .OrderBy(line => line.Id)
                   .FirstOrDefault()
               ?? throw new InvalidOperationException("Позиция не найдена во внутреннем заказе.");
    }

    private static OrderLine ResolveTargetLine(
        IDataStore store,
        long targetCustomerOrderId,
        long itemId,
        long? targetOrderLineId)
    {
        var lines = store.GetOrderLines(targetCustomerOrderId)
            .Where(line => line.ItemId == itemId)
            .OrderBy(line => line.Id)
            .ToList();
        if (lines.Count == 0)
        {
            throw new InvalidOperationException("Позиция не найдена в клиентском заказе.");
        }

        if (targetOrderLineId.HasValue)
        {
            return lines.FirstOrDefault(line => line.Id == targetOrderLineId.Value)
                   ?? throw new InvalidOperationException("Указанная строка клиентского заказа не найдена.");
        }

        return lines[0];
    }

    private static bool ItemTypeUsesOrderReservation(IDataStore store, long itemId)
    {
        var item = store.FindItemById(itemId);
        if (item?.ItemTypeId is not long itemTypeId)
        {
            return false;
        }

        return store.GetItemType(itemTypeId)?.EnableOrderReservation == true;
    }

    private static string NormalizeHu(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }
}

public sealed class OrderProducedHuReservationRequest
{
    public long SourceInternalOrderId { get; init; }
    public long TargetCustomerOrderId { get; init; }
    public long ItemId { get; init; }
    public long? TargetOrderLineId { get; init; }
    public IReadOnlyList<string>? HuCodes { get; init; }
    public double? Qty { get; init; }
}

public sealed class OrderProducedHuReservationResult
{
    public long SourceInternalOrderId { get; init; }
    public long TargetCustomerOrderId { get; init; }
    public long ItemId { get; init; }
    public long TargetOrderLineId { get; init; }
    public IReadOnlyList<string> ReservedHuCodes { get; init; } = Array.Empty<string>();
    public double QtyReserved { get; init; }
    public double SourceQtyOrdered { get; init; }
    public double SourceProducedQty { get; init; }
}

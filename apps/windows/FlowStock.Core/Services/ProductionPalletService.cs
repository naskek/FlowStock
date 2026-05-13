using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class ProductionPalletService
{
    private const double QtyTolerance = 0.000001d;
    private readonly IDataStore _data;

    public ProductionPalletService(IDataStore data)
    {
        _data = data;
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

    public ProductionPalletDocument Get(long docId)
    {
        var doc = RequireProductionReceipt(docId);
        var pallets = _data.GetProductionPalletsByDoc(doc.Id);
        return BuildDocument(doc.Id, pallets);
    }

    public IReadOnlyList<ProductionPalletWorkItem> GetActiveWorkItems()
    {
        return _data.GetActiveProductionPalletWorkItems();
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
            return ProductionPalletScanResult.Failure("Эта паллета относится к другому заказу/выпуску");
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

        if (string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            return ProductionPalletScanResult.Failure("Паллета отменена");
        }

        var docLine = _data.GetDocLines(doc.Id).FirstOrDefault(line => line.Id == pallet.DocLineId);
        if (docLine == null || docLine.ItemId != pallet.ItemId || docLine.OrderLineId != pallet.OrderLineId)
        {
            return ProductionPalletScanResult.Failure("План паллеты не совпадает со строкой выпуска.");
        }

        if (pallet.OrderId.HasValue && pallet.OrderLineId.HasValue)
        {
            var orderLine = _data.GetOrderLines(pallet.OrderId.Value)
                .FirstOrDefault(line => line.Id == pallet.OrderLineId.Value);
            if (orderLine == null || orderLine.ItemId != pallet.ItemId)
            {
                return ProductionPalletScanResult.Failure("Строка заказа для паллеты не найдена.");
            }

            if (!string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
            {
                var alreadyFilled = _data.GetFilledProductionPalletQtyByOrderLine(orderLine.Id, pallet.Id);
                if (alreadyFilled + pallet.PlannedQty > orderLine.QtyOrdered + QtyTolerance)
                {
                    return ProductionPalletScanResult.Failure("Выпуск превышает остаток по строке заказа");
                }
            }
        }

        var pallets = _data.GetProductionPalletsByDoc(doc.Id);
        var activePallets = pallets
            .Where(row => !string.Equals(row.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.Id)
            .ToList();
        var index = activePallets.FindIndex(row => row.Id == pallet.Id);
        var item = _data.FindItemById(pallet.ItemId);
        var order = pallet.OrderId.HasValue ? _data.GetOrder(pallet.OrderId.Value) : null;

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
            ItemId = pallet.ItemId,
            ItemName = item?.Name ?? pallet.ItemName,
            ItemBrand = item?.Brand,
            BaseUom = string.IsNullOrWhiteSpace(item?.BaseUom) ? "шт" : item!.BaseUom,
            PlannedQty = pallet.PlannedQty,
            PalletIndex = index >= 0 ? index + 1 : 0,
            PalletCount = activePallets.Count,
            PalletStatus = pallet.Status,
            Document = BuildDocument(doc.Id, pallets)
        };
    }

    public ProductionPalletFillResult Fill(string? huCode, string? deviceId)
    {
        var normalizedHu = NormalizeHu(huCode);
        if (string.IsNullOrWhiteSpace(normalizedHu))
        {
            return ProductionPalletFillResult.Failure("Укажите код паллеты.");
        }

        ProductionPalletFillResult? result = null;
        _data.ExecuteInTransaction(store =>
        {
            var pallet = store.GetProductionPalletByHu(normalizedHu);
            if (pallet == null)
            {
                result = ProductionPalletFillResult.Failure("Паллета не найдена в плане выпуска.");
                return;
            }

            var doc = store.GetDoc(pallet.PrdDocId);
            if (doc == null || doc.Type != DocType.ProductionReceipt)
            {
                result = ProductionPalletFillResult.Failure("Документ выпуска не найден.");
                return;
            }

            if (doc.Status == DocStatus.Closed)
            {
                result = ProductionPalletFillResult.Failure("Документ выпуска уже закрыт.");
                return;
            }

            if (string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            {
                result = ProductionPalletFillResult.Failure("Паллета отменена.");
                return;
            }

            if (string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
            {
                result = new ProductionPalletFillResult
                {
                    Success = true,
                    AlreadyFilled = true,
                    Pallet = pallet,
                    Document = BuildDocument(doc.Id, store.GetProductionPalletsByDoc(doc.Id))
                };
                return;
            }

            var docLine = store.GetDocLines(doc.Id).FirstOrDefault(line => line.Id == pallet.DocLineId);
            if (docLine == null)
            {
                result = ProductionPalletFillResult.Failure("Строка паллеты не найдена в документе выпуска.");
                return;
            }

            if (docLine.ItemId != pallet.ItemId || docLine.OrderLineId != pallet.OrderLineId)
            {
                result = ProductionPalletFillResult.Failure("План паллеты не совпадает со строкой выпуска.");
                return;
            }

            if (!docLine.ToLocationId.HasValue)
            {
                result = ProductionPalletFillResult.Failure("Для паллеты не указано место хранения.");
                return;
            }

            if (pallet.OrderId.HasValue && pallet.OrderLineId.HasValue)
            {
                var orderLine = store.GetOrderLines(pallet.OrderId.Value)
                    .FirstOrDefault(line => line.Id == pallet.OrderLineId.Value);
                if (orderLine == null || orderLine.ItemId != pallet.ItemId)
                {
                    result = ProductionPalletFillResult.Failure("Строка заказа для паллеты не найдена.");
                    return;
                }

                var alreadyFilled = store.GetFilledProductionPalletQtyByOrderLine(orderLine.Id, pallet.Id);
                if (alreadyFilled + pallet.PlannedQty > orderLine.QtyOrdered + QtyTolerance)
                {
                    result = ProductionPalletFillResult.Failure("Выпуск превышает остаток по строке заказа");
                    return;
                }
            }

            var filledAt = DateTime.Now;
            store.AddLedgerEntry(new LedgerEntry
            {
                Timestamp = filledAt,
                DocId = doc.Id,
                ItemId = pallet.ItemId,
                LocationId = docLine.ToLocationId.Value,
                QtyDelta = pallet.PlannedQty,
                HuCode = pallet.HuCode
            });
            store.MarkProductionPalletFilled(pallet.Id, filledAt, NormalizeDeviceId(deviceId));

            var filledPallet = store.GetProductionPalletByHu(normalizedHu) ?? pallet;
            result = new ProductionPalletFillResult
            {
                Success = true,
                AlreadyFilled = false,
                Pallet = filledPallet,
                Document = BuildDocument(doc.Id, store.GetProductionPalletsByDoc(doc.Id))
            };
        });

        return result ?? ProductionPalletFillResult.Failure("Не удалось наполнить паллету.");
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

    private ProductionPalletDocument BuildDocument(long docId, IReadOnlyList<ProductionPallet> pallets)
    {
        var summary = BuildSummary(pallets);
        var orderLineIds = pallets
            .Where(pallet => pallet.OrderId.HasValue && pallet.OrderLineId.HasValue)
            .Select(pallet => (OrderId: pallet.OrderId!.Value, OrderLineId: pallet.OrderLineId!.Value))
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

        var lines = pallets
            .GroupBy(pallet => new { pallet.OrderLineId, pallet.ItemId, pallet.ItemName })
            .Select(group =>
            {
                var orderedQty = group.Key.OrderLineId.HasValue
                                  && orderLinesById.TryGetValue(group.Key.OrderLineId.Value, out var orderLine)
                    ? orderLine.QtyOrdered
                    : group.Sum(pallet => pallet.PlannedQty);
                var linePallets = group.ToList();
                var lineSummary = BuildSummary(linePallets);
                return new ProductionPalletLineSummary
                {
                    OrderLineId = group.Key.OrderLineId,
                    ItemId = group.Key.ItemId,
                    ItemName = group.Key.ItemName,
                    OrderedQty = orderedQty,
                    PlannedPalletCount = lineSummary.PlannedPalletCount,
                    PlannedQty = lineSummary.PlannedQty,
                    FilledPalletCount = lineSummary.FilledPalletCount,
                    FilledQty = lineSummary.FilledQty,
                    RemainingPalletCount = lineSummary.RemainingPalletCount,
                    RemainingQty = Math.Max(0, orderedQty - lineSummary.FilledQty)
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
            Pallets = pallets
        };
    }

    private static ProductionPalletSummary BuildSummary(IReadOnlyList<ProductionPallet> pallets)
    {
        var active = pallets
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var filled = active
            .Where(pallet => string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new ProductionPalletSummary
        {
            PlannedPalletCount = active.Count,
            PlannedQty = active.Sum(pallet => pallet.PlannedQty),
            FilledPalletCount = filled.Count,
            FilledQty = filled.Sum(pallet => pallet.PlannedQty),
            RemainingPalletCount = active.Count - filled.Count,
            RemainingQty = Math.Max(0, active.Sum(pallet => pallet.PlannedQty) - filled.Sum(pallet => pallet.PlannedQty))
        };
    }

    private static string? NormalizeHu(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeDeviceId(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

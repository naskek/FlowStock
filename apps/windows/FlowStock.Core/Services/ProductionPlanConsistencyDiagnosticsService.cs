using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class ProductionPlanConsistencyDiagnosticsService(IDataStore dataStore)
{
    private readonly IDataStore _dataStore = dataStore;

    public IReadOnlyList<ProductionPlanConsistencyDiagnosticItem> GetItems()
    {
        if (_dataStore is IProductionPlanConsistencyDiagnosticsStore diagnosticsStore)
        {
            return diagnosticsStore.GetProductionPlanConsistencyDiagnostics();
        }

        return Array.Empty<ProductionPlanConsistencyDiagnosticItem>();
    }

    public bool BlocksPrdClose(long prdDocId)
    {
        return GetBlockingItemsForPrd(prdDocId).Count > 0
               || HasPrdPalletLineMismatch(prdDocId);
    }

    public IReadOnlyList<ProductionPlanConsistencyDiagnosticItem> GetBlockingItemsForPrd(long prdDocId)
    {
        return GetItems()
            .Where(item => IsBlockingSeverity(item.Severity)
                           && IsBlockingProblem(item.ProblemCode)
                           && (item.PrdDocs.Any(doc => doc.DocId == prdDocId)
                               || item.Pallets.Any(pallet => pallet.PrdDocId == prdDocId))
                           && !IsCustomerTakenReplacementExceed(item, prdDocId))
            .ToArray();
    }

    public static bool IsBlockingSeverity(string severity)
    {
        return string.Equals(severity, ProductionPlanConsistencySeverity.Error, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBlockingProblem(string problemCode)
    {
        return problemCode is ProductionPlanConsistencyProblemCode.OrderZeroButPalletsExist
            or ProductionPlanConsistencyProblemCode.PalletsExceedOrderQty
            or ProductionPlanConsistencyProblemCode.PrdLinesExceedOrderQty
            or ProductionPlanConsistencyProblemCode.MergedOrderWithPalletPlan;
    }

    private bool HasPrdPalletLineMismatch(long prdDocId)
    {
        var doc = _dataStore.GetDoc(prdDocId);
        if (doc == null || doc.Status == DocStatus.Closed || !_dataStore.HasProductionPallets(prdDocId))
        {
            return false;
        }

        if (doc.OrderId.HasValue)
        {
            var order = _dataStore.GetOrder(doc.OrderId.Value);
            if (order is { Type: OrderType.Customer, Status: OrderStatus.Shipped })
            {
                return false;
            }
        }

        var docLines = _dataStore.GetDocLines(prdDocId)
            .Where(line => line.Qty > StockQuantityRules.QtyTolerance)
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Qty));
        var palletLines = _dataStore.GetProductionPalletsByDoc(prdDocId)
            .Where(pallet => !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .SelectMany(pallet => pallet.Lines.Count > 0
                ? pallet.Lines
                : new[]
                {
                    new ProductionPalletComponentLine
                    {
                        ItemId = pallet.ItemId,
                        PlannedQty = pallet.PlannedQty
                    }
                })
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.PlannedQty));

        var itemIds = docLines.Keys.Union(palletLines.Keys).ToArray();
        foreach (var itemId in itemIds)
        {
            docLines.TryGetValue(itemId, out var docQty);
            palletLines.TryGetValue(itemId, out var palletQty);
            if (Math.Abs(docQty - palletQty) > StockQuantityRules.QtyTolerance)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsCustomerTakenReplacementExceed(ProductionPlanConsistencyDiagnosticItem item, long prdDocId)
    {
        if (item.ProblemCode is not (ProductionPlanConsistencyProblemCode.PalletsExceedOrderQty
            or ProductionPlanConsistencyProblemCode.PrdLinesExceedOrderQty))
        {
            return false;
        }

        if (!string.Equals(item.OrderType, "INTERNAL", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var takenQty = item.Pallets
            .Where(pallet => pallet.PrdDocId == prdDocId
                             && pallet.ItemId == item.ItemId
                             && string.Equals(pallet.Status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase)
                             && IsHuTakenByCustomer(pallet.HuCode))
            .Sum(pallet => pallet.PlannedQty);
        if (takenQty <= StockQuantityRules.QtyTolerance)
        {
            return false;
        }

        var adjustedPalletQty = Math.Max(0, item.OpenPalletPlannedQty - takenQty);
        var adjustedPrdDocQty = Math.Max(0, item.OpenPrdDocQty - takenQty);
        return item.ProblemCode switch
        {
            ProductionPlanConsistencyProblemCode.PalletsExceedOrderQty =>
                adjustedPalletQty <= item.OrderQty + StockQuantityRules.QtyTolerance,
            ProductionPlanConsistencyProblemCode.PrdLinesExceedOrderQty =>
                adjustedPrdDocQty <= item.OrderQty + StockQuantityRules.QtyTolerance,
            _ => false
        };
    }

    private bool IsHuTakenByCustomer(string? huCode)
    {
        if (string.IsNullOrWhiteSpace(huCode))
        {
            return false;
        }

        var normalizedHu = huCode.Trim();
        foreach (var order in _dataStore.GetOrders().Where(order => order.Type == OrderType.Customer))
        {
            if (_dataStore.GetOrderReceiptPlanLines(order.Id)
                .Any(line => line.QtyPlanned > StockQuantityRules.QtyTolerance
                             && string.Equals(line.ToHu?.Trim(), normalizedHu, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        foreach (var doc in _dataStore.GetDocs().Where(doc => doc.Type == DocType.Outbound))
        {
            if (!doc.OrderId.HasValue)
            {
                continue;
            }

            var order = _dataStore.GetOrder(doc.OrderId.Value);
            if (order?.Type != OrderType.Customer)
            {
                continue;
            }

            if (_dataStore.GetDocLines(doc.Id)
                .Any(line => line.Qty > StockQuantityRules.QtyTolerance
                             && string.Equals(line.FromHu?.Trim(), normalizedHu, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}

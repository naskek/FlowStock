using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class ProductionPalletStockBackfillDecision
{
    public static FilledProductionPalletStockAnalysis Analyze(FilledProductionPalletStockMetrics metrics)
    {
        var tolerance = StockQuantityRules.QtyTolerance;
        var plannedQty = metrics.PlannedQty;
        var currentLedgerQty = metrics.CurrentLedgerQty;
        var outboundBySameHu = metrics.OutboundBySameHuQty;
        var outboundByOrderItem = metrics.OutboundByOrderItemQty;
        var orderShipped = string.Equals(metrics.OrderStatus, OrderStatusMapper.StatusToString(OrderStatus.Shipped), StringComparison.OrdinalIgnoreCase);

        if (outboundBySameHu + tolerance >= plannedQty)
        {
            return Build(metrics, ProductionPalletStockBackfillDecisionCodes.AlreadyShippedSkip, 0, 0,
                "Паллета уже отгружена по тому же HU.");
        }

        if (metrics.OrderId.HasValue && outboundByOrderItem + tolerance >= plannedQty)
        {
            return Build(metrics, ProductionPalletStockBackfillDecisionCodes.AlreadyShippedSkip, 0, 0,
                "Товар уже отгружен по заказу/номенклатуре.");
        }

        if (orderShipped && outboundBySameHu <= tolerance)
        {
            return Build(metrics, ProductionPalletStockBackfillDecisionCodes.AmbiguousRequiresManualReview, null, 0,
                "Заказ выполнен, но OUTBOUND по этому HU не найден. Нужна ручная проверка.");
        }

        var missingQty = Math.Max(0, plannedQty - currentLedgerQty);
        return Build(metrics, ProductionPalletStockBackfillDecisionCodes.SafeToBackfill, plannedQty, missingQty, null);
    }

    public static bool IsReverseCandidate(FilledProductionPalletStockAnalysis analysis)
    {
        if (!StockQuantityRules.IsActiveStockQty(analysis.CurrentLedgerQty))
        {
            return false;
        }

        var tolerance = StockQuantityRules.QtyTolerance;
        return analysis.OutboundBySameHuQty + tolerance >= analysis.PlannedQty
               || (analysis.OrderId.HasValue && analysis.OutboundByOrderItemQty + tolerance >= analysis.PlannedQty);
    }

    public static FilledStockReverseCandidate ToReverseCandidate(FilledProductionPalletStockAnalysis analysis)
    {
        var reverseQty = Math.Min(analysis.CurrentLedgerQty, analysis.PlannedQty);
        var reason = analysis.OutboundBySameHuQty + StockQuantityRules.QtyTolerance >= analysis.PlannedQty
            ? "OUTBOUND по тому же HU покрывает planned_qty."
            : "OUTBOUND по заказу/номенклатуре покрывает planned_qty.";

        return new FilledStockReverseCandidate
        {
            PalletId = analysis.PalletId,
            PrdDocId = analysis.PrdDocId,
            PrdDocRef = analysis.PrdDocRef,
            OrderId = analysis.OrderId,
            OrderRef = analysis.OrderRef,
            OrderStatus = analysis.OrderStatus,
            ItemId = analysis.ItemId,
            ItemName = analysis.ItemName,
            HuCode = analysis.HuCode,
            LocationId = analysis.ToLocationId,
            LocationCode = analysis.ToLocationCode,
            PlannedQty = analysis.PlannedQty,
            CurrentHuStock = analysis.CurrentLedgerQty,
            OutboundBySameHuQty = analysis.OutboundBySameHuQty,
            OutboundDocsBySameHu = analysis.OutboundDocsBySameHu,
            OutboundByOrderItemQty = analysis.OutboundByOrderItemQty,
            OutboundDocsByOrderItem = analysis.OutboundDocsByOrderItem,
            ReverseQty = reverseQty,
            Reason = reason
        };
    }

    private static FilledProductionPalletStockAnalysis Build(
        FilledProductionPalletStockMetrics metrics,
        string decision,
        double? expectedCurrentQty,
        double missingQty,
        string? reason) =>
        new()
        {
            PalletId = metrics.PalletId,
            PrdDocId = metrics.PrdDocId,
            PrdDocRef = metrics.PrdDocRef,
            OrderId = metrics.OrderId,
            OrderRef = metrics.OrderRef,
            OrderStatus = metrics.OrderStatus,
            ItemId = metrics.ItemId,
            ItemName = metrics.ItemName,
            HuCode = metrics.HuCode,
            ToLocationId = metrics.ToLocationId,
            ToLocationCode = metrics.ToLocationCode,
            PlannedQty = metrics.PlannedQty,
            CurrentLedgerQty = metrics.CurrentLedgerQty,
            OutboundBySameHuQty = metrics.OutboundBySameHuQty,
            OutboundDocsBySameHu = metrics.OutboundDocsBySameHu,
            OutboundByOrderItemQty = metrics.OutboundByOrderItemQty,
            OutboundDocsByOrderItem = metrics.OutboundDocsByOrderItem,
            Decision = decision,
            ExpectedCurrentQty = expectedCurrentQty,
            MissingQty = missingQty,
            Reason = reason,
            Status = metrics.Status,
            FilledAt = metrics.FilledAt
        };
}

using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class InternalOrderMergeService
{
    public const double QtyTolerance = 0.000001d;
    public const string ActivePalletPlanWarningCode = "SOURCE_INTERNAL_HAS_ACTIVE_PALLET_PLAN";
    public const string MergedInfoCode = "SOURCE_INTERNAL_MERGED";

    public static InternalOrderMergeEvaluation Evaluate(IDataStore store, long sourceInternalOrderId)
    {
        var sourceOrder = store.GetOrder(sourceInternalOrderId);
        if (sourceOrder == null)
        {
            return InternalOrderMergeEvaluation.NotEligible("SOURCE_ORDER_NOT_FOUND", "Внутренний заказ не найден.");
        }

        if (sourceOrder.Type != OrderType.Internal)
        {
            return InternalOrderMergeEvaluation.NotEligible("SOURCE_NOT_INTERNAL", "Заказ не является внутренним.");
        }

        if (sourceOrder.Status == OrderStatus.Merged)
        {
            return InternalOrderMergeEvaluation.AlreadyMerged();
        }

        if (sourceOrder.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            return InternalOrderMergeEvaluation.NotEligible(
                "SOURCE_ORDER_NOT_EDITABLE",
                "Внутренний заказ недоступен для перевода в статус «Объединён».");
        }

        var lines = store.GetOrderLines(sourceInternalOrderId);
        if (lines.Any(line => line.QtyOrdered > QtyTolerance))
        {
            return InternalOrderMergeEvaluation.NotEligible(
                "SOURCE_HAS_REMAINING_DEMAND",
                "У внутреннего заказа осталась неперенесённая потребность.");
        }

        if (HasAnyProduction(store, sourceInternalOrderId))
        {
            return InternalOrderMergeEvaluation.NotEligible(
                "SOURCE_HAS_PRODUCTION",
                "По внутреннему заказу уже есть выпуск.");
        }

        if (HasClosedProductionReceiptOrLedger(store, sourceInternalOrderId))
        {
            return InternalOrderMergeEvaluation.NotEligible(
                "SOURCE_HAS_CLOSED_PRD_OR_LEDGER",
                "По внутреннему заказу есть закрытый выпуск или движения склада.");
        }

        if (HasActiveProductionPalletPlan(store, sourceInternalOrderId))
        {
            return InternalOrderMergeEvaluation.BlockedByActivePalletPlan();
        }

        return InternalOrderMergeEvaluation.Eligible();
    }

    public static InternalOrderMergeResult TryMarkAsMerged(
        IDataStore store,
        long sourceInternalOrderId,
        long targetOrderId,
        string targetOrderRef)
    {
        var evaluation = Evaluate(store, sourceInternalOrderId);
        if (evaluation.IsAlreadyMerged)
        {
            return InternalOrderMergeResult.AlreadyMerged();
        }

        if (evaluation.WarningCode == ActivePalletPlanWarningCode)
        {
            return InternalOrderMergeResult.Warning(
                evaluation.WarningCode,
                evaluation.WarningMessage ?? "У внутреннего заказа остался активный план паллет.");
        }

        if (!evaluation.CanMerge)
        {
            return InternalOrderMergeResult.Skipped();
        }

        var sourceOrder = store.GetOrder(sourceInternalOrderId)
                          ?? throw new InvalidOperationException("Внутренний заказ не найден.");
        var mergeCommentLine = BuildMergeCommentLine(targetOrderRef, targetOrderId);
        var commentUpdated = AppendCommentIfNeeded(store, sourceOrder, mergeCommentLine);
        store.UpdateOrderStatus(sourceInternalOrderId, OrderStatus.Merged);
        EmptyDraftProductionReceiptCleanup.CleanupEmptyDraftProductionReceiptsForOrder(store, sourceInternalOrderId);

        return new InternalOrderMergeResult
        {
            IsMerged = true,
            CommentUpdated = commentUpdated,
            InfoCode = MergedInfoCode,
            InfoMessage = $"Внутренний заказ №{sourceOrder.OrderRef} объединён с заказом №{targetOrderRef}."
        };
    }

    public static bool HasActiveProductionPalletPlan(IDataStore store, long orderId)
    {
        return store.GetDocsByOrder(orderId)
            .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status != DocStatus.Closed)
            .Any(doc => store.HasProductionPallets(doc.Id));
    }

    private static bool HasAnyProduction(IDataStore store, long orderId)
    {
        var lines = store.GetOrderLines(orderId);
        var producedByLine = OrderReceiptRemainingCalculator.BuildProducedTotalsByOrderLine(store, orderId, lines);
        return lines.Any(line =>
        {
            var produced = producedByLine.TryGetValue(line.Id, out var qty) ? qty : 0d;
            return produced > QtyTolerance;
        });
    }

    private static bool HasClosedProductionReceiptOrLedger(IDataStore store, long orderId)
    {
        foreach (var doc in store.GetDocsByOrder(orderId).Where(doc => doc.Type == DocType.ProductionReceipt))
        {
            if (doc.Status == DocStatus.Closed)
            {
                return true;
            }

            if (store.CountLedgerEntriesByDocId(doc.Id) > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AppendCommentIfNeeded(IDataStore store, Order sourceOrder, string mergeCommentLine)
    {
        var existingComment = sourceOrder.Comment ?? string.Empty;
        if (existingComment.Contains(mergeCommentLine, StringComparison.Ordinal))
        {
            return false;
        }

        var nextComment = string.IsNullOrWhiteSpace(existingComment)
            ? mergeCommentLine
            : $"{existingComment.TrimEnd()}{Environment.NewLine}{mergeCommentLine}";
        store.UpdateOrder(new Order
        {
            Id = sourceOrder.Id,
            OrderRef = sourceOrder.OrderRef,
            Type = sourceOrder.Type,
            PartnerId = sourceOrder.PartnerId,
            DueDate = sourceOrder.DueDate,
            Status = sourceOrder.Status,
            Comment = nextComment,
            CreatedAt = sourceOrder.CreatedAt,
            ShippedAt = sourceOrder.ShippedAt,
            PartnerName = sourceOrder.PartnerName,
            PartnerCode = sourceOrder.PartnerCode,
            UseReservedStock = sourceOrder.UseReservedStock,
            MarkingStatus = sourceOrder.MarkingStatus,
            MarkingExcelGeneratedAt = sourceOrder.MarkingExcelGeneratedAt,
            MarkingPrintedAt = sourceOrder.MarkingPrintedAt
        });
        return true;
    }

    private static string BuildMergeCommentLine(string targetOrderRef, long targetOrderId) =>
        $"Объединён с заказом №{targetOrderRef} ({targetOrderId}) при переносе потребности. Выпуск по этому заказу не требуется.";
}

public sealed class InternalOrderMergeEvaluation
{
    public bool CanMerge { get; init; }
    public bool IsAlreadyMerged { get; init; }
    public string? WarningCode { get; init; }
    public string? WarningMessage { get; init; }
    public string? ReasonCode { get; init; }
    public string? ReasonMessage { get; init; }

    public static InternalOrderMergeEvaluation Eligible() => new() { CanMerge = true };

    public static InternalOrderMergeEvaluation AlreadyMerged() => new() { IsAlreadyMerged = true, CanMerge = true };

    public static InternalOrderMergeEvaluation BlockedByActivePalletPlan() => new()
    {
        WarningCode = InternalOrderMergeService.ActivePalletPlanWarningCode,
        WarningMessage = "Внутренний заказ уже без количества, но у него остался активный план паллет. Сначала удалите или перенесите план паллет."
    };

    public static InternalOrderMergeEvaluation NotEligible(string code, string message) => new()
    {
        ReasonCode = code,
        ReasonMessage = message
    };
}

public sealed class InternalOrderMergeResult
{
    public bool IsMerged { get; init; }
    public bool CommentUpdated { get; init; }
    public string? WarningCode { get; init; }
    public string? WarningMessage { get; init; }
    public string? InfoCode { get; init; }
    public string? InfoMessage { get; init; }

    public static InternalOrderMergeResult AlreadyMerged() => new() { IsMerged = true };

    public static InternalOrderMergeResult Skipped() => new();

    public static InternalOrderMergeResult Warning(string code, string message) => new()
    {
        WarningCode = code,
        WarningMessage = message
    };
}

using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server;

public static class OrderCustomerSaveFollowUpBuilder
{
    private const double QtyTolerance = 0.000001d;

    public static OrderAutoRedistributeEnvelope Build(
        IDataStore store,
        OrderAutoRedistributionApplyResult applyResult)
    {
        var order = store.GetOrder(applyResult.TargetOrderId);
        var bindReservedStock = order?.UseReservedStock == true;
        var reservationLines = bindReservedStock
            ? BuildReservationLines(store, applyResult.TargetOrderId)
            : Array.Empty<OrderReservationPlanLineDto>();

        var warnings = new List<OrderSaveFollowUpWarningDto>();
        if (!string.IsNullOrWhiteSpace(applyResult.SkippedReason))
        {
            var description = OrderAutoRedistributionReasonCodes.DescribeSkippedReason(applyResult.SkippedReason);
            if (!string.IsNullOrWhiteSpace(description))
            {
                warnings.Add(new OrderSaveFollowUpWarningDto
                {
                    Code = applyResult.SkippedReason!,
                    Message = description
                });
            }
        }

        if (bindReservedStock && reservationLines.Count == 0)
        {
            warnings.Add(new OrderSaveFollowUpWarningDto
            {
                Code = "NO_HU_RESERVED",
                Message = "Резерв включён, но в плане заказа нет закреплённых HU (нет подходящего складского остатка или тип номенклатуры без резерва)."
            });
        }

        var transfers = applyResult.Transfers
            .Select(transfer => new OrderAutoRedistributeTransferDto
            {
                SourceOrderId = transfer.SourceOrderId,
                SourceOrderRef = transfer.SourceOrderRef,
                TargetOrderId = transfer.TargetOrderId,
                ItemId = transfer.ItemId,
                QtyTransferred = transfer.QtyTransferred,
                QtyFromUnproduced = transfer.QtyFromUnproduced,
                QtyFromProducedStock = transfer.QtyFromProducedStock,
                TransferredHuCodes = transfer.TransferredHuCodes
            })
            .ToList();

        var ignoredAttempts = applyResult.IgnoredAttempts
            .Select(attempt => new OrderAutoRedistributeIgnoredDto
            {
                SourceOrderId = attempt.SourceOrderId,
                SourceOrderRef = attempt.SourceOrderRef,
                ItemId = attempt.ItemId,
                Qty = attempt.Qty,
                ReasonCode = OrderAutoRedistributionReasonCodes.MapFromExceptionMessage(attempt.Reason),
                Reason = attempt.Reason
            })
            .ToList();

        if (ignoredAttempts.Count > 0)
        {
            warnings.Add(new OrderSaveFollowUpWarningDto
            {
                Code = "AUTO_TRANSFER_PARTIAL",
                Message = "Часть автопереносов с внутренних заказов не выполнена — см. список пропущенных попыток."
            });
        }

        var result = applyResult.HasTransfers ? "REDISTRIBUTED" : "NO_TRANSFERS";
        if (applyResult.HasTransfers && ignoredAttempts.Count > 0)
        {
            result = "PARTIALLY_REDISTRIBUTED";
        }

        return new OrderAutoRedistributeEnvelope
        {
            Ok = true,
            Success = true,
            Result = result,
            TargetOrderId = applyResult.TargetOrderId,
            BindReservedStock = bindReservedStock,
            SkippedReason = applyResult.SkippedReason,
            ReservationLines = reservationLines,
            Warnings = warnings,
            Transfers = transfers,
            IgnoredAttempts = ignoredAttempts
        };
    }

    private static IReadOnlyList<OrderReservationPlanLineDto> BuildReservationLines(IDataStore store, long orderId)
    {
        return store.GetOrderReceiptPlanLines(orderId)
            .Where(line => line.QtyPlanned > QtyTolerance && !string.IsNullOrWhiteSpace(line.ToHu))
            .OrderBy(line => line.SortOrder)
            .ThenBy(line => line.Id)
            .Select(line => new OrderReservationPlanLineDto
            {
                ItemId = line.ItemId,
                OrderLineId = line.OrderLineId,
                HuCode = line.ToHu!.Trim(),
                QtyPlanned = line.QtyPlanned
            })
            .ToList();
    }
}

using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class OrderAutoRedistributionEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/orders/{targetCustomerOrderId:long}/auto-redistribute-from-internal", HandleApplyAsync);
    }

    private static Task<IResult> HandleApplyAsync(long targetCustomerOrderId, IDataStore store)
    {
        if (targetCustomerOrderId <= 0)
        {
            return Task.FromResult<IResult>(Results.BadRequest(new ApiResult(false, "INVALID_REQUEST")));
        }

        var service = new OrderAutoRedistributionService(store);
        var applyResult = service.ApplyFromOpenInternalOrders(targetCustomerOrderId);

        return Task.FromResult<IResult>(Results.Ok(new OrderAutoRedistributeEnvelope
        {
            Ok = true,
            Result = applyResult.HasTransfers ? "REDISTRIBUTED" : "NO_TRANSFERS",
            TargetOrderId = applyResult.TargetOrderId,
            SkippedReason = applyResult.SkippedReason,
            Transfers = applyResult.Transfers
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
                .ToList(),
            IgnoredAttempts = applyResult.IgnoredAttempts
                .Select(attempt => new OrderAutoRedistributeIgnoredDto
                {
                    SourceOrderId = attempt.SourceOrderId,
                    SourceOrderRef = attempt.SourceOrderRef,
                    ItemId = attempt.ItemId,
                    Qty = attempt.Qty,
                    Reason = attempt.Reason
                })
                .ToList()
        }));
    }
}

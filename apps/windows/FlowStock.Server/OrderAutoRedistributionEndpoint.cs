using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FlowStock.Server;

public static class OrderAutoRedistributionEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/orders/{targetCustomerOrderId:long}/auto-redistribute-from-internal", HandleApplyAsync);
    }

    private static Task<IResult> HandleApplyAsync(long targetCustomerOrderId, IDataStore store, ILoggerFactory loggerFactory)
    {
        if (targetCustomerOrderId <= 0)
        {
            return Task.FromResult<IResult>(Results.BadRequest(new OrderAutoRedistributeErrorEnvelope
            {
                Ok = false,
                Success = false,
                ErrorCode = "INVALID_REQUEST",
                Message = "Некорректный идентификатор заказа."
            }));
        }

        var order = store.GetOrder(targetCustomerOrderId);
        if (order == null)
        {
            return Task.FromResult<IResult>(Results.NotFound(new OrderAutoRedistributeErrorEnvelope
            {
                Ok = false,
                Success = false,
                ErrorCode = "ORDER_NOT_FOUND",
                Message = "Заказ не найден."
            }));
        }

        if (order.Type != Core.Models.OrderType.Customer)
        {
            return Task.FromResult<IResult>(Results.BadRequest(new OrderAutoRedistributeErrorEnvelope
            {
                Ok = false,
                Success = false,
                ErrorCode = "TARGET_NOT_CUSTOMER",
                Message = "Автоперенос доступен только для клиентского заказа."
            }));
        }

        var service = new OrderAutoRedistributionService(store);
        var applyResult = service.ApplyFromOpenInternalOrders(targetCustomerOrderId);
        var envelope = OrderCustomerSaveFollowUpBuilder.Build(store, applyResult);
        var logger = loggerFactory.CreateLogger("OrderAutoRedistribution");
        logger.LogInformation(
            "AutoRedistribute target_order_id={TargetOrderId} target_order_ref={TargetOrderRef} target_status={TargetStatus} use_reserved_stock={UseReservedStock} customer_lines={CustomerLineCount} refreshed_count={RefreshedCount} changed_count={ChangedCount} open_internal_candidates={OpenInternalCandidateCount} matching_internal_candidates={MatchingInternalCandidateCount} transfers_count={TransfersCount} skipped_reason={SkippedReason} warnings={Warnings}",
            order.Id,
            order.OrderRef,
            order.Status,
            order.UseReservedStock,
            applyResult.CustomerLineCount,
            applyResult.InternalStatusRefresh.RefreshedCount,
            applyResult.InternalStatusRefresh.ChangedCount,
            applyResult.OpenInternalCandidateCount,
            applyResult.MatchingInternalCandidateCount,
            applyResult.Transfers.Count,
            applyResult.SkippedReason,
            string.Join(",", applyResult.Warnings.Select(warning => warning.Code).Distinct(StringComparer.OrdinalIgnoreCase)));

        return Task.FromResult<IResult>(Results.Ok(envelope));
    }
}

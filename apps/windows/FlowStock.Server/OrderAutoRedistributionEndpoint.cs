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

        return Task.FromResult<IResult>(Results.Ok(envelope));
    }
}

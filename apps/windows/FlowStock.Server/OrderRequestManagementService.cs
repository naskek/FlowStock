using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server;

public sealed class OrderRequestManagementService(IDataStore dataStore, PartnerRoleResolver partnerRoles)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool Supports(string? requestType) =>
        string.Equals(requestType, OrderRequestType.CreateOrder, StringComparison.OrdinalIgnoreCase)
        || string.Equals(requestType, OrderRequestType.SetOrderStatus, StringComparison.OrdinalIgnoreCase);

    public OrderRequestManagementResult Confirm(long requestId, string actor) =>
        Execute(requestId, actor, approve: true);

    public OrderRequestManagementResult Reject(long requestId, string actor) =>
        Execute(requestId, actor, approve: false);

    private OrderRequestManagementResult Execute(long requestId, string actor, bool approve)
    {
        try
        {
            OrderRequestManagementResult? result = null;
            dataStore.ExecuteInTransaction(store =>
            {
                if (store is not IOrderRequestManagementStore requestStore)
                {
                    throw new InvalidOperationException("IDataStore не поддерживает атомарное управление заявками.");
                }

                var request = requestStore.GetOrderRequestForUpdate(requestId)
                    ?? throw new RequestManagementException(OrderRequestManagementResultKind.NotFound, "ORDER_REQUEST_NOT_FOUND");
                if (!string.Equals(request.Status, OrderRequestStatus.Pending, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RequestManagementException(OrderRequestManagementResultKind.Conflict, "ORDER_REQUEST_ALREADY_RESOLVED");
                }

                if (!Supports(request.RequestType))
                {
                    throw new RequestManagementException(
                        OrderRequestManagementResultKind.ValidationFailed,
                        "UNSUPPORTED_ORDER_REQUEST_TYPE");
                }

                long? appliedOrderId = null;
                string note;
                var status = approve ? OrderRequestStatus.Approved : OrderRequestStatus.Rejected;
                if (approve)
                {
                    (appliedOrderId, note) = DispatchConfirmation(store, request);
                }
                else
                {
                    note = "Заявка отклонена администратором.";
                }

                if (!requestStore.TryResolvePendingOrderRequest(
                        request.Id,
                        status,
                        DateTime.Now,
                        actor,
                        note,
                        appliedOrderId))
                {
                    throw new RequestManagementException(OrderRequestManagementResultKind.Conflict, "ORDER_REQUEST_ALREADY_RESOLVED");
                }

                result = new OrderRequestManagementResult(
                    OrderRequestManagementResultKind.Success,
                    status,
                    request.Id,
                    appliedOrderId,
                    note);
            });

            return result ?? new OrderRequestManagementResult(
                OrderRequestManagementResultKind.Failure,
                null,
                requestId,
                null,
                "ORDER_REQUEST_MANAGEMENT_FAILED");
        }
        catch (RequestManagementException ex)
        {
            return new OrderRequestManagementResult(ex.Kind, null, requestId, null, ex.Message);
        }
        catch (JsonException)
        {
            return Validation(requestId, "INVALID_ORDER_REQUEST_PAYLOAD");
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or InvalidOperationException
                                   or OrderItemActivityException
                                   or CommercialTermsException)
        {
            return Validation(requestId, ex.Message);
        }
    }

    private (long AppliedOrderId, string Note) DispatchConfirmation(IDataStore store, OrderRequest request)
    {
        if (string.Equals(request.RequestType, OrderRequestType.CreateOrder, StringComparison.OrdinalIgnoreCase))
        {
            return ConfirmCreateOrder(store, request.PayloadJson);
        }

        if (string.Equals(request.RequestType, OrderRequestType.SetOrderStatus, StringComparison.OrdinalIgnoreCase))
        {
            return ConfirmSetOrderStatus(store, request.PayloadJson);
        }

        throw new RequestManagementException(
            OrderRequestManagementResultKind.ValidationFailed,
            "UNSUPPORTED_ORDER_REQUEST_TYPE");
    }

    private (long AppliedOrderId, string Note) ConfirmCreateOrder(IDataStore store, string payloadJson)
    {
        var payload = JsonSerializer.Deserialize<CreateOrderPayload>(payloadJson, JsonOptions)
            ?? throw new RequestManagementException(OrderRequestManagementResultKind.ValidationFailed, "INVALID_ORDER_REQUEST_PAYLOAD");
        var orderType = string.IsNullOrWhiteSpace(payload.OrderType)
            ? OrderType.Customer
            : OrderStatusMapper.TypeFromString(payload.OrderType)
              ?? throw new RequestManagementException(OrderRequestManagementResultKind.ValidationFailed, "INVALID_TYPE");

        DateTime? dueDate = null;
        if (!string.IsNullOrWhiteSpace(payload.DueDate))
        {
            if (!DateTime.TryParseExact(
                    payload.DueDate.Trim(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedDate))
            {
                throw new RequestManagementException(OrderRequestManagementResultKind.ValidationFailed, "INVALID_DUE_DATE");
            }

            dueDate = parsedDate.Date;
        }

        var lines = (payload.Lines ?? [])
            .Select(line =>
            {
                if (line.ItemId <= 0 || line.QtyOrdered <= 0)
                {
                    throw new RequestManagementException(OrderRequestManagementResultKind.ValidationFailed, "INVALID_ORDER_LINE");
                }

                var item = store.FindItemById(line.ItemId)
                    ?? throw new RequestManagementException(OrderRequestManagementResultKind.ValidationFailed, "ITEM_NOT_FOUND");
                return new OrderLineView
                {
                    ItemId = item.Id,
                    ItemName = item.Name,
                    QtyOrdered = line.QtyOrdered,
                    ProductionPurpose = orderType == OrderType.Customer
                        ? ProductionLinePurpose.CustomerOrder
                        : ProductionLinePurposeMapper.FromDbValue(line.ProductionPurpose)
                };
            })
            .ToList();
        if (lines.Count == 0)
        {
            throw new RequestManagementException(OrderRequestManagementResultKind.ValidationFailed, "MISSING_LINES");
        }

        if (orderType == OrderType.Customer
            && payload.PartnerId.HasValue
            && !partnerRoles.IsCustomer(payload.PartnerId.Value))
        {
            throw new RequestManagementException(OrderRequestManagementResultKind.ValidationFailed, "PARTNER_IS_SUPPLIER");
        }

        var orderId = OrderService.CreateOrderInTransaction(
            store,
            payload.OrderRef?.Trim() ?? string.Empty,
            orderType == OrderType.Customer ? payload.PartnerId : null,
            dueDate,
            payload.Comment,
            lines,
            orderType);
        return (orderId, $"Создан заказ ID={orderId}.");
    }

    private static (long AppliedOrderId, string Note) ConfirmSetOrderStatus(IDataStore store, string payloadJson)
    {
        var payload = JsonSerializer.Deserialize<SetOrderStatusPayload>(payloadJson, JsonOptions)
            ?? throw new RequestManagementException(OrderRequestManagementResultKind.ValidationFailed, "INVALID_ORDER_REQUEST_PAYLOAD");
        var status = OrderStatusMapper.StatusFromString(payload.Status)
            ?? throw new RequestManagementException(OrderRequestManagementResultKind.ValidationFailed, "INVALID_STATUS");
        OrderService.ChangeOrderStatusInTransaction(store, payload.OrderId, status);
        return (payload.OrderId, $"Статус изменён на «{OrderStatusMapper.StatusToDisplayName(status)}».");
    }

    private static OrderRequestManagementResult Validation(long requestId, string message) =>
        new(OrderRequestManagementResultKind.ValidationFailed, null, requestId, null, message);

    private sealed class RequestManagementException(OrderRequestManagementResultKind kind, string message)
        : Exception(message)
    {
        public OrderRequestManagementResultKind Kind { get; } = kind;
    }

    private sealed record CreateOrderPayload
    {
        [JsonPropertyName("order_ref")]
        public string? OrderRef { get; init; }

        [JsonPropertyName("order_type")]
        public string? OrderType { get; init; }

        [JsonPropertyName("partner_id")]
        public long? PartnerId { get; init; }

        [JsonPropertyName("due_date")]
        public string? DueDate { get; init; }

        [JsonPropertyName("comment")]
        public string? Comment { get; init; }

        [JsonPropertyName("lines")]
        public List<CreateOrderLinePayload>? Lines { get; init; }
    }

    private sealed record CreateOrderLinePayload
    {
        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("qty_ordered")]
        public double QtyOrdered { get; init; }

        [JsonPropertyName("production_purpose")]
        public string? ProductionPurpose { get; init; }
    }

    private sealed record SetOrderStatusPayload
    {
        [JsonPropertyName("order_id")]
        public long OrderId { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }
    }
}

public enum OrderRequestManagementResultKind
{
    Success,
    NotFound,
    Conflict,
    ValidationFailed,
    Failure
}

public sealed record OrderRequestManagementResult(
    OrderRequestManagementResultKind Kind,
    string? Status,
    long RequestId,
    long? AppliedOrderId,
    string Message);

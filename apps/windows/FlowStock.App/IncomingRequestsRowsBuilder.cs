using System.Globalization;
using System.Text.Json;
using FlowStock.Core.Models;

namespace FlowStock.App;

public enum IncomingRequestTypeFilter
{
    All,
    Item,
    Order,
    ReadyHu
}

public enum IncomingRequestRowKind
{
    Item,
    Order,
    ReadyHu
}

public sealed record IncomingRequestRow
{
    public const string ReadyHuBindingRequestType = "READY_HU_BINDING_AVAILABLE";

    public required IncomingRequestRowKind Kind { get; init; }
    public ItemRequest? ItemRequest { get; init; }
    public OrderRequest? OrderRequest { get; init; }
    public WpfReadyHuBindingReadModel? ReadyHuBinding { get; init; }
    public required string SourceDisplay { get; init; }
    public required string TypeDisplay { get; init; }
    public required string Summary { get; init; }
    public required string RequestedBy { get; init; }
    public required string StatusDisplay { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string? ResolutionNote { get; init; }
    public bool CanApprove { get; init; }
    public bool CanReject { get; init; }
    public bool CanOpenDetails { get; init; }
    public string RequestTypeCode { get; init; } = string.Empty;
    public string? DetailsPreview { get; init; }
}

public static class IncomingRequestsRowsBuilder
{
    public static IReadOnlyList<IncomingRequestRow> Build(
        IReadOnlyList<ItemRequest> itemRequests,
        IReadOnlyList<OrderRequest> orderRequests,
        WpfReadyHuBindingReadModel? readyHuBinding,
        IncomingRequestTypeFilter filter,
        DateTime now)
    {
        var merged = new List<IncomingRequestRow>();

        foreach (var itemRequest in itemRequests)
        {
            merged.Add(new IncomingRequestRow
            {
                Kind = IncomingRequestRowKind.Item,
                ItemRequest = itemRequest,
                SourceDisplay = "Товары",
                TypeDisplay = "Запрос товара",
                Summary = BuildItemSummary(itemRequest),
                RequestedBy = BuildRequestedBy(itemRequest.Login, itemRequest.DeviceId),
                StatusDisplay = GetItemStatusDisplay(itemRequest.Status),
                CreatedAt = itemRequest.CreatedAt,
                ResolvedAt = itemRequest.ResolvedAt,
                ResolutionNote = itemRequest.Status.Equals("RESOLVED", StringComparison.OrdinalIgnoreCase)
                    ? "Отмечено обработанным."
                    : null,
                CanApprove = !itemRequest.Status.Equals("RESOLVED", StringComparison.OrdinalIgnoreCase),
                CanReject = false,
                CanOpenDetails = false
            });
        }

        foreach (var orderRequest in orderRequests)
        {
            var isPending = string.Equals(orderRequest.Status, OrderRequestStatus.Pending, StringComparison.OrdinalIgnoreCase);
            merged.Add(new IncomingRequestRow
            {
                Kind = IncomingRequestRowKind.Order,
                OrderRequest = orderRequest,
                SourceDisplay = "Заказы (веб)",
                TypeDisplay = GetOrderTypeDisplay(orderRequest.RequestType),
                Summary = BuildOrderSummary(orderRequest),
                RequestedBy = BuildRequestedBy(orderRequest.CreatedByLogin, orderRequest.CreatedByDeviceId),
                StatusDisplay = GetOrderStatusDisplay(orderRequest.Status),
                CreatedAt = orderRequest.CreatedAt,
                ResolvedAt = orderRequest.ResolvedAt,
                ResolutionNote = orderRequest.ResolutionNote,
                CanApprove = isPending,
                CanReject = isPending,
                CanOpenDetails = true,
                RequestTypeCode = orderRequest.RequestType
            });
        }

        if (readyHuBinding is { HuCount: > 0 })
        {
            merged.Add(new IncomingRequestRow
            {
                Kind = IncomingRequestRowKind.ReadyHu,
                ReadyHuBinding = readyHuBinding,
                SourceDisplay = "Склад",
                TypeDisplay = "Готовые HU",
                Summary = $"Свободных HU: {readyHuBinding.HuCount} · подходящих заказов: {readyHuBinding.OrderCount} · строк: {readyHuBinding.LineCount}",
                RequestedBy = "-",
                StatusDisplay = "Доступно",
                CreatedAt = now,
                ResolutionNote = "Computed notification",
                CanApprove = false,
                CanReject = false,
                CanOpenDetails = true,
                RequestTypeCode = IncomingRequestRow.ReadyHuBindingRequestType,
                DetailsPreview = BuildReadyHuDetailsPreview(readyHuBinding)
            });
        }

        return merged
            .Where(row => MatchesFilter(row, filter))
            .OrderByDescending(row => row.CreatedAt)
            .ToArray();
    }

    private static bool MatchesFilter(IncomingRequestRow row, IncomingRequestTypeFilter filter) =>
        filter switch
        {
            IncomingRequestTypeFilter.Item => row.Kind == IncomingRequestRowKind.Item,
            IncomingRequestTypeFilter.Order => row.Kind == IncomingRequestRowKind.Order,
            IncomingRequestTypeFilter.ReadyHu => row.Kind == IncomingRequestRowKind.ReadyHu,
            _ => true
        };

    private static string BuildItemSummary(ItemRequest request)
    {
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? "-" : request.Comment.Trim();
        return $"ШК: {request.Barcode} · Комментарий: {comment}";
    }

    private static string BuildOrderSummary(OrderRequest request)
    {
        try
        {
            using var doc = JsonDocument.Parse(request.PayloadJson);
            var root = doc.RootElement;
            if (string.Equals(request.RequestType, OrderRequestType.CreateOrder, StringComparison.OrdinalIgnoreCase))
            {
                var orderRef = root.TryGetProperty("order_ref", out var refEl) ? refEl.GetString() : null;
                var partnerId = root.TryGetProperty("partner_id", out var partnerEl) ? partnerEl.GetInt64() : 0;
                var lineCount = root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array
                    ? linesEl.GetArrayLength()
                    : 0;
                return $"Создать заказ {orderRef ?? "-"} · контрагент ID={partnerId} · строк: {lineCount}";
            }

            if (string.Equals(request.RequestType, OrderRequestType.SetOrderStatus, StringComparison.OrdinalIgnoreCase))
            {
                var orderId = root.TryGetProperty("order_id", out var orderEl) ? orderEl.GetInt64() : 0;
                var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
                var displayStatus = OrderStatusMapper.StatusFromString(status) is { } parsed
                    ? OrderStatusMapper.StatusToDisplayName(parsed)
                    : status ?? "-";
                return $"Смена статуса · заказ ID={orderId} -> {displayStatus}";
            }
        }
        catch
        {
            // keep fallback summary
        }

        return request.PayloadJson;
    }

    private static string BuildReadyHuDetailsPreview(WpfReadyHuBindingReadModel model)
    {
        var rows = model.HuRows
            .Take(3)
            .Select(row =>
            {
                var orders = row.CompatibleOrders
                    .Select(order => string.IsNullOrWhiteSpace(order.OrderRef) ? $"ID={order.OrderId}" : order.OrderRef)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray();
                var orderText = orders.Length == 0 ? "-" : string.Join(", ", orders);
                var location = string.IsNullOrWhiteSpace(row.LocationDisplay) ? "-" : row.LocationDisplay;
                var origin = string.IsNullOrWhiteSpace(row.OriginInternalOrderRef) ? string.Empty : $" · источник: {row.OriginInternalOrderRef}";
                return $"{row.HuCode}: {row.ItemName}, {row.Qty.ToString("G", CultureInfo.InvariantCulture)} шт · {location}{origin} · заказы: {orderText}";
            })
            .ToArray();

        return rows.Length == 0 ? string.Empty : string.Join(Environment.NewLine, rows);
    }

    private static string BuildRequestedBy(string? login, string? deviceId)
    {
        var normalizedLogin = login?.Trim();
        var normalizedDeviceId = deviceId?.Trim();

        if (!string.IsNullOrWhiteSpace(normalizedLogin) && !string.IsNullOrWhiteSpace(normalizedDeviceId))
        {
            return $"{normalizedLogin} ({normalizedDeviceId})";
        }

        if (!string.IsNullOrWhiteSpace(normalizedLogin))
        {
            return normalizedLogin;
        }

        if (!string.IsNullOrWhiteSpace(normalizedDeviceId))
        {
            return normalizedDeviceId;
        }

        return "-";
    }

    private static string GetItemStatusDisplay(string status)
    {
        return status.Equals("RESOLVED", StringComparison.OrdinalIgnoreCase)
            ? "Обработан"
            : "Новый";
    }

    private static string GetOrderTypeDisplay(string requestType)
    {
        if (string.Equals(requestType, OrderRequestType.CreateOrder, StringComparison.OrdinalIgnoreCase))
        {
            return "Создание заказа";
        }

        if (string.Equals(requestType, OrderRequestType.SetOrderStatus, StringComparison.OrdinalIgnoreCase))
        {
            return "Смена статуса заказа";
        }

        return requestType;
    }

    private static string GetOrderStatusDisplay(string status)
    {
        if (string.Equals(status, OrderRequestStatus.Pending, StringComparison.OrdinalIgnoreCase))
        {
            return "Ожидает";
        }

        if (string.Equals(status, OrderRequestStatus.Approved, StringComparison.OrdinalIgnoreCase))
        {
            return "Подтвержден";
        }

        if (string.Equals(status, OrderRequestStatus.Rejected, StringComparison.OrdinalIgnoreCase))
        {
            return "Отклонен";
        }

        return status;
    }
}

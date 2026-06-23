using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class HuBindingManageReadEndpoint
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/orders/hu-bindings/manage/items", HandleItems);
        app.MapGet("/api/orders/hu-bindings/manage/items/{itemId:long}/hus", HandleHus);
        app.MapGet("/api/orders/hu-bindings/manage/items/{itemId:long}/targets", HandleTargets);
    }

    private static IResult HandleItems(HttpRequest request, IDataStore store)
    {
        if (store is not IHuBindingManagementReadStore)
        {
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        }

        if (!TryParseLimit(request, out var limit, out var limitError))
        {
            return limitError!;
        }

        var search = request.Query.TryGetValue("search", out var searchValues) ? searchValues.ToString() : null;
        var items = new HuBindingManageReadModelService(store).GetItems(search, limit);

        return Results.Ok(new ItemsResponse
        {
            Ok = true,
            Items = items.Select(item => new ItemResponse
            {
                ItemId = item.ItemId,
                ItemName = item.ItemName,
                HuCount = item.HuCount
            }).ToArray()
        });
    }

    private static IResult HandleHus(long itemId, HttpRequest request, IDataStore store)
    {
        if (store is not IHuBindingManagementReadStore)
        {
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        }

        if (itemId <= 0)
        {
            return Results.BadRequest(new ErrorResponse { Ok = false, Error = "INVALID_ITEM_ID", Message = "Некорректный товар." });
        }

        if (!TryParseState(request, out var state, out var stateError))
        {
            return stateError!;
        }

        if (!TryParseLimit(request, out var limit, out var limitError))
        {
            return limitError!;
        }

        if (!TryParseOffset(request, out var offset, out var offsetError))
        {
            return offsetError!;
        }

        var filter = new HuBindingManageHuFilter
        {
            HuSearch = QueryValue(request, "hu_search"),
            OrderSearch = QueryValue(request, "order_search"),
            PartnerSearch = QueryValue(request, "partner_search"),
            State = state,
            Limit = limit,
            Offset = offset
        };

        var page = new HuBindingManageReadModelService(store).GetHuRows(itemId, filter);

        return Results.Ok(new HusResponse
        {
            Ok = true,
            ItemId = page.ItemId,
            ItemName = page.ItemName,
            Total = page.Total,
            Limit = page.Limit,
            Offset = page.Offset,
            HuRows = page.HuRows.Select(MapHuRow).ToArray()
        });
    }

    private static IResult HandleTargets(long itemId, IDataStore store)
    {
        if (store is not IHuBindingManagementReadStore)
        {
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        }

        if (itemId <= 0)
        {
            return Results.BadRequest(new ErrorResponse { Ok = false, Error = "INVALID_ITEM_ID", Message = "Некорректный товар." });
        }

        var targets = new HuBindingManageReadModelService(store).GetTargets(itemId);

        return Results.Ok(new TargetsResponse
        {
            Ok = true,
            ItemId = itemId,
            TargetLines = targets.Select(line => new TargetLineResponse
            {
                OrderId = line.OrderId,
                OrderRef = line.OrderRef,
                PartnerName = line.PartnerName,
                OrderStatus = line.OrderStatus,
                DueAt = line.DueAt,
                OrderLineId = line.OrderLineId,
                ItemId = line.ItemId,
                QtyOrdered = line.QtyOrdered,
                QtyShipped = line.QtyShipped,
                CurrentBoundHuCodes = line.CurrentBoundHuCodes,
                CurrentBoundQty = line.CurrentBoundQty,
                MaxAdditionalBindQty = line.MaxAdditionalBindQty
            }).ToArray()
        });
    }

    private static HuRowResponse MapHuRow(HuBindingManageHuRow row)
    {
        return new HuRowResponse
        {
            HuCode = row.HuCode,
            ItemId = row.ItemId,
            ItemName = row.ItemName,
            Qty = row.Qty,
            LocationDisplay = row.LocationDisplay,
            State = row.State,
            IsMixed = row.IsMixed,
            OriginInternalOrderId = row.OriginInternalOrderId,
            OriginInternalOrderRef = row.OriginInternalOrderRef,
            FirstReceiptAt = row.FirstReceiptAt,
            CurrentAssignment = row.CurrentAssignment == null
                ? null
                : new AssignmentResponse
                {
                    OrderId = row.CurrentAssignment.OrderId,
                    OrderRef = row.CurrentAssignment.OrderRef,
                    PartnerName = row.CurrentAssignment.PartnerName,
                    OrderLineId = row.CurrentAssignment.OrderLineId,
                    OrderStatus = row.CurrentAssignment.OrderStatus,
                    ReservedQty = row.CurrentAssignment.ReservedQty
                }
        };
    }

    private static string? QueryValue(HttpRequest request, string key) =>
        request.Query.TryGetValue(key, out var values) && !string.IsNullOrWhiteSpace(values.ToString())
            ? values.ToString()
            : null;

    private static bool TryParseState(HttpRequest request, out HuBindingManageStateFilter state, out IResult? error)
    {
        state = HuBindingManageStateFilter.All;
        error = null;
        if (!request.Query.TryGetValue("state", out var values))
        {
            return true;
        }

        var raw = values.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        switch (raw.Trim().ToUpperInvariant())
        {
            case "ALL":
                state = HuBindingManageStateFilter.All;
                return true;
            case "FREE":
                state = HuBindingManageStateFilter.Free;
                return true;
            case "BOUND":
                state = HuBindingManageStateFilter.Bound;
                return true;
            default:
                error = Results.BadRequest(new ErrorResponse { Ok = false, Error = "INVALID_STATE", Message = "Допустимые значения state: ALL, FREE, BOUND." });
                return false;
        }
    }

    private static bool TryParseLimit(HttpRequest request, out int limit, out IResult? error)
    {
        limit = DefaultLimit;
        error = null;
        if (!request.Query.TryGetValue("limit", out var values) || string.IsNullOrWhiteSpace(values.ToString()))
        {
            return true;
        }

        if (!int.TryParse(values.ToString(), out var parsed) || parsed <= 0)
        {
            error = Results.BadRequest(new ErrorResponse { Ok = false, Error = "INVALID_LIMIT", Message = "limit должен быть положительным числом." });
            return false;
        }

        if (parsed > MaxLimit)
        {
            error = Results.BadRequest(new ErrorResponse { Ok = false, Error = "INVALID_LIMIT", Message = $"limit не должен превышать {MaxLimit}." });
            return false;
        }

        limit = parsed;
        return true;
    }

    private static bool TryParseOffset(HttpRequest request, out int offset, out IResult? error)
    {
        offset = 0;
        error = null;
        if (!request.Query.TryGetValue("offset", out var values) || string.IsNullOrWhiteSpace(values.ToString()))
        {
            return true;
        }

        if (!int.TryParse(values.ToString(), out var parsed) || parsed < 0)
        {
            error = Results.BadRequest(new ErrorResponse { Ok = false, Error = "INVALID_OFFSET", Message = "offset должен быть неотрицательным числом." });
            return false;
        }

        offset = parsed;
        return true;
    }

    private sealed class ItemsResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; init; }
        [JsonPropertyName("items")] public IReadOnlyList<ItemResponse> Items { get; init; } = Array.Empty<ItemResponse>();
    }

    private sealed class ItemResponse
    {
        [JsonPropertyName("item_id")] public long ItemId { get; init; }
        [JsonPropertyName("item_name")] public string ItemName { get; init; } = string.Empty;
        [JsonPropertyName("hu_count")] public int HuCount { get; init; }
    }

    private sealed class HusResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; init; }
        [JsonPropertyName("item_id")] public long ItemId { get; init; }
        [JsonPropertyName("item_name")] public string ItemName { get; init; } = string.Empty;
        [JsonPropertyName("total")] public int Total { get; init; }
        [JsonPropertyName("limit")] public int Limit { get; init; }
        [JsonPropertyName("offset")] public int Offset { get; init; }
        [JsonPropertyName("hu_rows")] public IReadOnlyList<HuRowResponse> HuRows { get; init; } = Array.Empty<HuRowResponse>();
    }

    private sealed class HuRowResponse
    {
        [JsonPropertyName("hu_code")] public string HuCode { get; init; } = string.Empty;
        [JsonPropertyName("item_id")] public long ItemId { get; init; }
        [JsonPropertyName("item_name")] public string ItemName { get; init; } = string.Empty;
        [JsonPropertyName("qty")] public double Qty { get; init; }
        [JsonPropertyName("location_display")] public string LocationDisplay { get; init; } = string.Empty;
        [JsonPropertyName("state")] public string State { get; init; } = string.Empty;
        [JsonPropertyName("is_mixed")] public bool IsMixed { get; init; }
        [JsonPropertyName("origin_internal_order_id")] public long? OriginInternalOrderId { get; init; }
        [JsonPropertyName("origin_internal_order_ref")] public string? OriginInternalOrderRef { get; init; }
        [JsonPropertyName("first_receipt_at")] public DateTime? FirstReceiptAt { get; init; }
        [JsonPropertyName("current_assignment")] public AssignmentResponse? CurrentAssignment { get; init; }
    }

    private sealed class AssignmentResponse
    {
        [JsonPropertyName("order_id")] public long OrderId { get; init; }
        [JsonPropertyName("order_ref")] public string OrderRef { get; init; } = string.Empty;
        [JsonPropertyName("partner_name")] public string? PartnerName { get; init; }
        [JsonPropertyName("order_line_id")] public long OrderLineId { get; init; }
        [JsonPropertyName("order_status")] public string OrderStatus { get; init; } = string.Empty;
        [JsonPropertyName("reserved_qty")] public double ReservedQty { get; init; }
    }

    private sealed class TargetsResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; init; }
        [JsonPropertyName("item_id")] public long ItemId { get; init; }
        [JsonPropertyName("target_lines")] public IReadOnlyList<TargetLineResponse> TargetLines { get; init; } = Array.Empty<TargetLineResponse>();
    }

    private sealed class TargetLineResponse
    {
        [JsonPropertyName("order_id")] public long OrderId { get; init; }
        [JsonPropertyName("order_ref")] public string OrderRef { get; init; } = string.Empty;
        [JsonPropertyName("partner_name")] public string? PartnerName { get; init; }
        [JsonPropertyName("order_status")] public string OrderStatus { get; init; } = string.Empty;
        [JsonPropertyName("due_at")] public DateTime? DueAt { get; init; }
        [JsonPropertyName("order_line_id")] public long OrderLineId { get; init; }
        [JsonPropertyName("item_id")] public long ItemId { get; init; }
        [JsonPropertyName("qty_ordered")] public double QtyOrdered { get; init; }
        [JsonPropertyName("qty_shipped")] public double QtyShipped { get; init; }
        [JsonPropertyName("current_bound_hu_codes")] public IReadOnlyList<string> CurrentBoundHuCodes { get; init; } = Array.Empty<string>();
        [JsonPropertyName("current_bound_qty")] public double CurrentBoundQty { get; init; }
        [JsonPropertyName("max_additional_bind_qty")] public double MaxAdditionalBindQty { get; init; }
    }

    private sealed class ErrorResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; init; }
        [JsonPropertyName("error")] public string Error { get; init; } = string.Empty;
        [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    }
}

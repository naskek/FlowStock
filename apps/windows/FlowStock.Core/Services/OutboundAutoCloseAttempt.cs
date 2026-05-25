using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

internal sealed class OutboundAutoCloseAttempt
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? ErrorCode { get; init; }
    public long? ClosedDocId { get; init; }
    public string? ClosedDocRef { get; init; }
    public OutboundPickingOrderDetails? Order { get; init; }

    public static OutboundAutoCloseAttempt Failure(string errorCode, string message)
    {
        return new OutboundAutoCloseAttempt
        {
            Success = false,
            ErrorCode = errorCode,
            Message = message
        };
    }

    public static OutboundAutoCloseAttempt AlreadyClosed(Doc draft, OutboundPickingOrderDetails order)
    {
        return new OutboundAutoCloseAttempt
        {
            Success = true,
            ClosedDocId = draft.Id,
            ClosedDocRef = draft.DocRef,
            Message = $"Отгрузка уже проведена ({draft.DocRef}).",
            Order = order
        };
    }

    public static OutboundAutoCloseAttempt Closed(string docRef, long docId, OutboundPickingOrderDetails order)
    {
        return new OutboundAutoCloseAttempt
        {
            Success = true,
            ClosedDocId = docId,
            ClosedDocRef = docRef,
            Message = $"Отгрузка проведена ({docRef}).",
            Order = order
        };
    }
}

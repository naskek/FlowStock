using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

/// <summary>
/// Durable, atomic protection against duplicate PC Web CREATE_ORDER submissions.
/// Null means that the key has never been used; a conflict means that the key
/// belongs to another actor or business payload.
/// </summary>
public interface IOrderRequestIdempotencyStore
{
    OrderRequestIdempotencyResult? FindCreateOrderRequest(
        Guid requestKey, string payloadHash, string? actorLogin, string? actorDeviceId);

    OrderRequestIdempotencyResult AddCreateOrderRequest(
        OrderRequest request, Guid requestKey, string payloadHash);
}

public sealed record OrderRequestIdempotencyResult(
    long RequestId, string Status, bool WasCreated, bool IsConflict);

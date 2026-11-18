namespace FlowStock.Core.Abstractions;

/// <summary>
/// Opt-in server-owned synthetic quantity evidence and issuance.
/// No public request handler or DI activation is permitted by this interface.
/// </summary>
public interface IMarkingQuantityIssueStore
{
    MarkingTaskQuantityEvidence ReadMarkingTaskQuantityEvidence(Guid taskId);

    /// <summary>
    /// Uses the caller's READ COMMITTED order-export transaction. The task,
    /// immutable pool and journal are locked and rechecked before issuing.
    /// Returns the count of newly issued units (zero on an exact re-export).
    /// </summary>
    long EnsureMarkingTaskQuantityIssued(Guid taskId, DateTime generatedAt);

    /// <summary>
    /// Fail closed when planned INTERNAL HU adoption is not backed by an
    /// attributable marking receipt; PRINTED pallet labels are not evidence.
    /// </summary>
    void RequireResolvedCustomerAdoptedMarking(long customerOrderId);
}

public sealed record MarkingTaskQuantityEvidence(
    long ActiveQuantity,
    bool HasHistoricalCodes,
    bool HasQuantityPool,
    long AppliedQuantity);

public sealed class MarkingQuantityIssueException : Exception
{
    public MarkingQuantityIssueException(string message) : base(message) { }
}

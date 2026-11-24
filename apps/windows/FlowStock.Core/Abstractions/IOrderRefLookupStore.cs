namespace FlowStock.Core.Abstractions;

/// <summary>
/// Narrow order-reference lookup for API validation without loading the
/// expensive order read model. The implementation must include orders in
/// all persisted states, including cancelled and merged.
/// </summary>
public interface IOrderRefLookupStore
{
    bool HasOtherOrderWithRef(long excludedOrderId, string orderRef);

    IReadOnlyList<string> GetAllOrderRefsForNextNumber();
}

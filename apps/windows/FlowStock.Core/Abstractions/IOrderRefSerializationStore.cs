namespace FlowStock.Core.Abstractions;

/// <summary>Acquired before request/order/item/HU locks by every order-reference writer.
/// Held until the enclosing READ COMMITTED transaction commits or rolls back.</summary>
public interface IOrderRefSerializationStore
{
    void LockOrderReferences();
}

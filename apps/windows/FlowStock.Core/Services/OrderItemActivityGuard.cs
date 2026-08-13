using FlowStock.Core.Abstractions;

namespace FlowStock.Core.Services;

public static class OrderItemActivityGuard
{
    public const string ItemInactiveForOrder = "ITEM_INACTIVE_FOR_ORDER";

    public static void EnsureActiveForAdditionalOrderQuantity(
        IDataStore store,
        IEnumerable<long> itemIds)
    {
        var ids = NormalizeItemIds(itemIds);
        if (ids.Length == 0)
        {
            return;
        }

        // Locking serializes PostgreSQL order writes with item deactivation. It is
        // deliberately separate from semantic validation so in-memory/test stores
        // still enforce existence and IsActive even when their lock is a no-op.
        store.LockItemsForOrderValidation(ids);
        ValidateNormalizedItemsActive(store, ids);
    }

    public static void ValidateActiveForAdditionalOrderQuantity(
        IDataStore store,
        IEnumerable<long> itemIds)
    {
        ValidateNormalizedItemsActive(store, NormalizeItemIds(itemIds));
    }

    private static void ValidateNormalizedItemsActive(IDataStore store, IReadOnlyList<long> ids)
    {
        foreach (var itemId in ids)
        {
            var item = store.FindItemById(itemId);
            if (item == null)
            {
                throw new OrderItemActivityException("ITEM_NOT_FOUND", "Товар не найден.");
            }

            if (!item.IsActive)
            {
                throw new OrderItemActivityException(
                    ItemInactiveForOrder,
                    $"Товар \"{item.Name}\" выведен из оборота и недоступен для нового количества заказа.");
            }
        }
    }

    private static long[] NormalizeItemIds(IEnumerable<long> itemIds) => itemIds
        .Where(itemId => itemId > 0)
        .Distinct()
        .OrderBy(itemId => itemId)
        .ToArray();
}

public sealed class OrderItemActivityException : InvalidOperationException
{
    public OrderItemActivityException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

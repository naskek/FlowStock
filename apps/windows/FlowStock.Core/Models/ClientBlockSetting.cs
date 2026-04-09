namespace FlowStock.Core.Models;

public sealed record ClientBlockSetting(string Key, bool IsEnabled);

public sealed record ClientBlockDefinition(string Key, string Client, string Section, string Label);

public static class ClientBlockCatalog
{
    public const string PcStock = "pc_stock";
    public const string PcCatalog = "pc_catalog";
    public const string PcOrders = "pc_orders";

    public const string TsdOperations = "tsd_operations";
    public const string TsdStock = "tsd_stock";
    public const string TsdCatalog = "tsd_catalog";
    public const string TsdOrders = "tsd_orders";
    public const string TsdInbound = "tsd_inbound";
    public const string TsdProductionReceipt = "tsd_production_receipt";
    public const string TsdOutbound = "tsd_outbound";
    public const string TsdMove = "tsd_move";
    public const string TsdWriteOff = "tsd_write_off";
    public const string TsdInventory = "tsd_inventory";

    public static readonly IReadOnlyList<ClientBlockDefinition> All = new[]
    {
        new ClientBlockDefinition(PcStock, "PC", "Основные", "Остатки"),
        new ClientBlockDefinition(PcCatalog, "PC", "Основные", "Каталог"),
        new ClientBlockDefinition(PcOrders, "PC", "Основные", "Заказы"),
        new ClientBlockDefinition(TsdOperations, "TSD", "Основные", "Операции"),
        new ClientBlockDefinition(TsdStock, "TSD", "Основные", "Остатки"),
        new ClientBlockDefinition(TsdCatalog, "TSD", "Основные", "Каталог"),
        new ClientBlockDefinition(TsdOrders, "TSD", "Основные", "Заказы"),
        new ClientBlockDefinition(TsdInbound, "TSD", "Операции", "Приемка"),
        new ClientBlockDefinition(TsdProductionReceipt, "TSD", "Операции", "Выпуск продукции"),
        new ClientBlockDefinition(TsdOutbound, "TSD", "Операции", "Отгрузка"),
        new ClientBlockDefinition(TsdMove, "TSD", "Операции", "Перемещение"),
        new ClientBlockDefinition(TsdWriteOff, "TSD", "Операции", "Списание"),
        new ClientBlockDefinition(TsdInventory, "TSD", "Операции", "Инвентаризация")
    };

    public static IReadOnlyDictionary<string, bool> MergeWithDefaults(IEnumerable<ClientBlockSetting>? settings)
    {
        var result = All.ToDictionary(definition => definition.Key, _ => true, StringComparer.OrdinalIgnoreCase);
        if (settings == null)
        {
            return result;
        }

        foreach (var setting in settings)
        {
            if (string.IsNullOrWhiteSpace(setting.Key))
            {
                continue;
            }

            if (!result.ContainsKey(setting.Key))
            {
                continue;
            }

            result[setting.Key] = setting.IsEnabled;
        }

        return result;
    }

    public static bool IsKnownKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return All.Any(definition => string.Equals(definition.Key, key, StringComparison.OrdinalIgnoreCase));
    }
}

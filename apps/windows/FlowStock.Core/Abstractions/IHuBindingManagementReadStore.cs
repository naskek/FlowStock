using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

/// <summary>
/// Оптимизированный read-store экрана «Управление привязками складских HU».
/// Все выборки фильтруются по товару на стороне БД; полный склад в память не загружается.
/// PostgreSQL реализует методы SQL-запросами; тестовый harness — in-memory.
/// </summary>
public interface IHuBindingManagementReadStore
{
    /// <summary>Товары, у которых есть HU с положительным ledger-остатком. Поиск по имени, ограничение по limit.</summary>
    IReadOnlyList<HuBindingManageItemRow> GetManagementItems(string? search, int limit);

    /// <summary>HU выбранного товара с фильтрами/пагинацией и текущей привязкой.</summary>
    HuBindingManageHuPage GetManagementHuRows(long itemId, HuBindingManageHuFilter filter);

    /// <summary>
    /// Целевые активные строки клиентских заказов для выбранного товара с полным набором
    /// текущих привязанных HU. <see cref="HuBindingManageTargetLineRow.MaxAdditionalBindQty"/>
    /// заполняется на уровне read-model сервиса.
    /// </summary>
    IReadOnlyList<HuBindingManageTargetLineRow> GetManagementTargetLines(long itemId);
}

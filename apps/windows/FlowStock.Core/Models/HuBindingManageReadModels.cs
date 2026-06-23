namespace FlowStock.Core.Models;

/// <summary>Фильтр состояния HU для экрана управления привязками.</summary>
public enum HuBindingManageStateFilter
{
    All,
    Free,
    Bound
}

/// <summary>Параметры выборки HU выбранного товара (серверный поиск + пагинация).</summary>
public sealed class HuBindingManageHuFilter
{
    public string? HuSearch { get; init; }
    public string? OrderSearch { get; init; }
    public string? PartnerSearch { get; init; }
    public HuBindingManageStateFilter State { get; init; } = HuBindingManageStateFilter.All;
    public int Limit { get; init; } = 100;
    public int Offset { get; init; }
}

/// <summary>Товар, у которого есть HU с положительным ledger-остатком.</summary>
public sealed class HuBindingManageItemRow
{
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int HuCount { get; init; }
}

/// <summary>Текущая привязка HU к строке клиентского заказа.</summary>
public sealed class HuBindingManageHuAssignment
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string? PartnerName { get; init; }
    public long OrderLineId { get; init; }
    public string OrderStatus { get; init; } = string.Empty;
    public double ReservedQty { get; init; }
}

/// <summary>Складской HU выбранного товара с текущим состоянием и привязкой.</summary>
public sealed class HuBindingManageHuRow
{
    public string HuCode { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public double Qty { get; init; }
    public string LocationDisplay { get; init; } = string.Empty;
    public bool IsMixed { get; init; }
    public long? OriginInternalOrderId { get; init; }
    public string? OriginInternalOrderRef { get; init; }
    public DateTime? FirstReceiptAt { get; init; }

    /// <summary>null — HU свободен; иначе текущая привязка.</summary>
    public HuBindingManageHuAssignment? CurrentAssignment { get; init; }

    public string State => CurrentAssignment != null ? "BOUND" : "FREE";
}

/// <summary>Страница HU выбранного товара.</summary>
public sealed class HuBindingManageHuPage
{
    public long ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Limit { get; init; }
    public int Offset { get; init; }
    public IReadOnlyList<HuBindingManageHuRow> HuRows { get; init; } = Array.Empty<HuBindingManageHuRow>();
}

/// <summary>
/// Сырьевая строка целевого назначения (read-store). <see cref="MaxAdditionalBindQty"/>
/// рассчитывается read-model сервисом и в выдаче read-store равен 0.
/// </summary>
public sealed class HuBindingManageTargetLineRow
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string? PartnerName { get; init; }
    public string OrderStatus { get; init; } = string.Empty;
    public DateTime? DueAt { get; init; }
    public long OrderLineId { get; init; }
    public long ItemId { get; init; }
    public double QtyOrdered { get; init; }
    public double QtyShipped { get; init; }
    public IReadOnlyList<string> CurrentBoundHuCodes { get; init; } = Array.Empty<string>();
    public double CurrentBoundQty { get; init; }
    public double MaxAdditionalBindQty { get; init; }
}

/// <summary>Целевая строка заказа read-model (с рассчитанной доступной ёмкостью).</summary>
public sealed class HuBindingManageTargetLine
{
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string? PartnerName { get; init; }
    public string OrderStatus { get; init; } = string.Empty;
    public DateTime? DueAt { get; init; }
    public long OrderLineId { get; init; }
    public long ItemId { get; init; }
    public double QtyOrdered { get; init; }
    public double QtyShipped { get; init; }
    public IReadOnlyList<string> CurrentBoundHuCodes { get; init; } = Array.Empty<string>();
    public double CurrentBoundQty { get; init; }
    public double MaxAdditionalBindQty { get; init; }
}

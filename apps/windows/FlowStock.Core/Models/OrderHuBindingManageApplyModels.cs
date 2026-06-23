namespace FlowStock.Core.Models;

/// <summary>
/// Запрос атомарного применения окончательного набора привязок готовых HU
/// сразу по нескольким клиентским заказам (экран «Управление привязками HU»).
/// </summary>
public sealed class OrderHuBindingManageApplyRequest
{
    public const string ReplaceFinalSelectionMode = "replace_final_selection";

    public string Mode { get; init; } = string.Empty;

    /// <summary>Ожидаемое состояние каждого изменяемого HU (optimistic concurrency).</summary>
    public IReadOnlyList<ManageExpectedHuState> ExpectedHuStates { get; init; } =
        Array.Empty<ManageExpectedHuState>();

    public IReadOnlyList<OrderHuBindingManageApplyLineRequest> Lines { get; init; } =
        Array.Empty<OrderHuBindingManageApplyLineRequest>();
}

/// <summary>Ожидаемое состояние одного HU на момент загрузки экрана.</summary>
public sealed class ManageExpectedHuState
{
    public string HuCode { get; init; } = string.Empty;
    public long ItemId { get; init; }
    public double ExpectedQty { get; init; }

    /// <summary>Ожидаемый текущий владелец-заказ; null — HU свободен.</summary>
    public long? ExpectedOrderId { get; init; }

    /// <summary>Ожидаемая текущая строка-владелец; null — HU свободен или строка неизвестна.</summary>
    public long? ExpectedOrderLineId { get; init; }
}

/// <summary>Одна затронутая строка заказа с её ожидаемым и финальным набором HU.</summary>
public sealed class OrderHuBindingManageApplyLineRequest
{
    public long OrderId { get; init; }
    public long OrderLineId { get; init; }

    /// <summary>Полный текущий набор HU строки, ожидаемый клиентом (optimistic concurrency).</summary>
    public IReadOnlyList<string>? ExpectedBoundHuCodes { get; init; }

    /// <summary>Полный желаемый итоговый набор HU строки.</summary>
    public IReadOnlyList<string>? FinalHuCodes { get; init; }
}

/// <summary>Результат batch-применения по всем затронутым заказам.</summary>
public sealed class OrderHuBindingManageApplyResult
{
    public bool Ok { get; init; } = true;
    public IReadOnlyList<OrderHuBindingManageApplyOrderResult> Orders { get; init; } =
        Array.Empty<OrderHuBindingManageApplyOrderResult>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Результат по одному затронутому заказу.</summary>
public sealed class OrderHuBindingManageApplyOrderResult
{
    public long OrderId { get; init; }
    public IReadOnlyList<OrderHuBindingApplyFinalLineResult> AppliedLines { get; init; } =
        Array.Empty<OrderHuBindingApplyFinalLineResult>();
}

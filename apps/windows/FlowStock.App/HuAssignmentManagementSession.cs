using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlowStock.App;

public sealed class HuAssignmentManagementSession : INotifyPropertyChanged
{
    private const double QtyTolerance = 0.000001d;
    private readonly Dictionary<string, HuAssignmentManagementHuItem> _huByCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, HuAssignmentManagementTargetLineItem> _lineById = new();

    public HuAssignmentManagementSession(
        WpfHuBindingManageHuPage page,
        IReadOnlyList<WpfHuBindingManageTargetLine> targetLines)
    {
        RefreshCore(page, targetLines);
    }

    public long ItemId { get; private set; }
    public string ItemName { get; private set; } = string.Empty;
    public ObservableCollection<HuAssignmentManagementHuItem> HuRows { get; } = new();
    public ObservableCollection<HuAssignmentManagementTargetLineItem> TargetLines { get; } = new();
    public ObservableCollection<HuAssignmentManagementChangeItem> Changes { get; } = new();
    public bool HasStagedChanges => Changes.Count > 0;
    public string Summary => $"HU: {HuRows.Count} · строки: {TargetLines.Count} · изменений: {Changes.Count}";
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool StageBind(
        HuAssignmentManagementHuItem? hu,
        HuAssignmentManagementTargetLineItem? targetLine,
        out string message)
    {
        message = string.Empty;
        if (hu == null || targetLine == null)
        {
            message = "Выберите HU и строку заказа.";
            return false;
        }

        if (!_huByCode.TryGetValue(hu.HuCode, out var currentHu)
            || !_lineById.TryGetValue(targetLine.OrderLineId, out var currentLine))
        {
            message = "HU или строка заказа больше не доступны.";
            return false;
        }

        if (currentHu.IsMixed)
        {
            message = "Смешанный HU нельзя перепривязать на этом экране.";
            return false;
        }

        if (currentHu.ItemId != currentLine.ItemId)
        {
            message = "HU не соответствует товару строки заказа.";
            return false;
        }

        if (currentHu.FutureOrderLineId == currentLine.OrderLineId)
        {
            message = "HU уже назначен на эту строку.";
            return false;
        }

        if (!TryValidateMutationContext(currentHu, out message))
        {
            return false;
        }

        var affectedLineIds = new[]
            {
                currentHu.OriginalOrderLineId,
                currentHu.FutureOrderLineId,
                currentLine.OrderLineId
            }
            .Where(lineId => lineId.HasValue)
            .Select(lineId => lineId!.Value)
            .Distinct()
            .ToArray();
        if (!TryEvaluateProjectedStates(
                currentHu,
                currentLine.OrderLineId,
                affectedLineIds,
                out var projectedStates,
                out message))
        {
            return false;
        }

        if (projectedStates[currentLine.OrderLineId].FutureBoundQty
            > currentLine.MaxFutureBoundQty + QtyTolerance)
        {
            message = "Количество HU превышает доступный остаток строки заказа.";
            return false;
        }

        currentHu.SetFutureAssignment(currentLine.OrderId, currentLine.OrderRef, currentLine.PartnerDisplay, currentLine.OrderLineId);
        RebuildDerivedState();
        return true;
    }

    public bool StageDetach(HuAssignmentManagementHuItem? hu, out string message)
    {
        message = string.Empty;
        if (hu == null)
        {
            message = "Выберите HU.";
            return false;
        }

        if (!_huByCode.TryGetValue(hu.HuCode, out var currentHu))
        {
            message = "HU больше не доступен.";
            return false;
        }

        if (!currentHu.FutureOrderLineId.HasValue)
        {
            message = "HU уже свободен.";
            return false;
        }

        if (!TryValidateMutationContext(currentHu, out message))
        {
            return false;
        }

        var affectedLineIds = new[] { currentHu.OriginalOrderLineId, currentHu.FutureOrderLineId }
            .Where(lineId => lineId.HasValue)
            .Select(lineId => lineId!.Value)
            .Distinct()
            .ToArray();
        if (!TryEvaluateProjectedStates(
                currentHu,
                projectedFutureOrderLineId: null,
                affectedLineIds,
                out _,
                out message))
        {
            return false;
        }

        currentHu.ClearFutureAssignment();
        RebuildDerivedState();
        return true;
    }

    public bool CancelChange(HuAssignmentManagementHuItem? hu, out string message)
    {
        message = string.Empty;
        if (hu == null)
        {
            message = "Выберите HU.";
            return false;
        }

        if (!_huByCode.TryGetValue(hu.HuCode, out var currentHu))
        {
            message = "HU больше не доступен.";
            return false;
        }

        if (!currentHu.IsChanged)
        {
            message = "Для HU нет подготовленного изменения.";
            return false;
        }

        currentHu.ResetFutureToOriginal();
        RebuildDerivedState();
        return true;
    }

    public bool TryRefreshFrom(
        WpfHuBindingManageHuPage page,
        IReadOnlyList<WpfHuBindingManageTargetLine> targetLines,
        bool discardStagedChanges,
        out string message)
    {
        message = string.Empty;
        if (HasStagedChanges && !discardStagedChanges)
        {
            message = "Есть неподтвержденные изменения HU.";
            return false;
        }

        RefreshCore(page, targetLines);
        return true;
    }

    public bool TryChangeItem(
        WpfHuBindingManageHuPage page,
        IReadOnlyList<WpfHuBindingManageTargetLine> targetLines,
        bool discardStagedChanges,
        out string message) =>
        TryRefreshFrom(page, targetLines, discardStagedChanges, out message);

    public WpfHuBindingManageApplyRequest BuildApplyRequest()
    {
        var changedHu = HuRows.Where(hu => hu.IsChanged).OrderBy(hu => hu.HuCode, StringComparer.OrdinalIgnoreCase).ToArray();
        var affectedLineIds = changedHu
            .SelectMany(hu => new[] { hu.OriginalOrderLineId, hu.FutureOrderLineId })
            .Where(lineId => lineId.HasValue)
            .Select(lineId => lineId!.Value)
            .Distinct()
            .OrderBy(lineId => lineId)
            .ToArray();

        var expectedStates = changedHu
            .Select(hu => new WpfHuBindingManageExpectedHuState
            {
                HuCode = hu.HuCode,
                ItemId = hu.ItemId,
                ExpectedQty = hu.Qty,
                ExpectedOrderId = hu.OriginalOrderId,
                ExpectedOrderLineId = hu.OriginalOrderLineId
            })
            .ToArray();

        var lines = affectedLineIds
            .Select(lineId => _lineById.TryGetValue(lineId, out var line)
                ? line
                : throw new InvalidOperationException(
                    $"Полный snapshot строки {lineId} недоступен. Обновите данные и повторите действие."))
            .Select(line =>
            {
                var future = EvaluateFutureLineState(line.OrderLineId);
                return new WpfHuBindingManageApplyLineRequest
                {
                    OrderId = line.OrderId,
                    OrderLineId = line.OrderLineId,
                    ExpectedBoundHuCodes = line.OriginalBoundHuCodes.ToArray(),
                    FinalHuCodes = future.FinalHuCodes
                };
            })
            .ToArray();

        return new WpfHuBindingManageApplyRequest
        {
            ExpectedHuStates = expectedStates,
            Lines = lines
        };
    }

    public void MarkSaveSuccess()
    {
        var committedStates = TargetLines.ToDictionary(
            line => line.OrderLineId,
            line => EvaluateFutureLineState(line.OrderLineId));

        foreach (var hu in HuRows)
        {
            hu.CommitFutureAsOriginal();
        }

        foreach (var line in TargetLines)
        {
            var committed = committedStates[line.OrderLineId];
            line.ReplaceOriginalBoundState(committed.FinalHuCodes, committed.FutureBoundQty);
        }

        RebuildDerivedState();
    }

    public HuAssignmentManagementHuItem? FindHu(string? huCode)
    {
        var normalized = NormalizeHu(huCode);
        return string.IsNullOrWhiteSpace(normalized) || !_huByCode.TryGetValue(normalized, out var hu)
            ? null
            : hu;
    }

    public HuAssignmentManagementTargetLineItem? FindTargetLine(long orderLineId) =>
        _lineById.TryGetValue(orderLineId, out var line) ? line : null;

    private void RefreshCore(WpfHuBindingManageHuPage page, IReadOnlyList<WpfHuBindingManageTargetLine> targetLines)
    {
        ItemId = page.ItemId;
        ItemName = string.IsNullOrWhiteSpace(page.ItemName) ? $"Товар {page.ItemId}" : page.ItemName;

        _huByCode.Clear();
        _lineById.Clear();
        HuRows.Clear();
        TargetLines.Clear();
        Changes.Clear();

        foreach (var line in targetLines.Where(line => line.OrderLineId > 0).OrderBy(line => line.OrderRef).ThenBy(line => line.OrderLineId))
        {
            var item = new HuAssignmentManagementTargetLineItem(line);
            if (_lineById.TryAdd(item.OrderLineId, item))
            {
                TargetLines.Add(item);
            }
        }

        foreach (var row in page.HuRows.OrderBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase))
        {
            var huCode = NormalizeHu(row.HuCode);
            if (string.IsNullOrWhiteSpace(huCode) || _huByCode.ContainsKey(huCode))
            {
                continue;
            }

            var item = new HuAssignmentManagementHuItem(row, huCode);
            _huByCode[huCode] = item;
            HuRows.Add(item);
        }

        RebuildDerivedState();
    }

    private void RebuildDerivedState()
    {
        Changes.Clear();
        foreach (var change in HuRows.Where(hu => hu.IsChanged).Select(HuAssignmentManagementChangeItem.FromHu))
        {
            Changes.Add(change);
        }

        foreach (var line in TargetLines)
        {
            var future = EvaluateFutureLineState(line.OrderLineId);
            line.SetFutureBoundState(future.FinalHuCodes, future.FutureBoundQty);
        }

        OnPropertyChanged(nameof(HuRows));
        OnPropertyChanged(nameof(TargetLines));
        OnPropertyChanged(nameof(Changes));
        OnPropertyChanged(nameof(HasStagedChanges));
        OnPropertyChanged(nameof(Summary));
    }

    private HuAssignmentFutureLineState EvaluateFutureLineState(
        long orderLineId,
        HuAssignmentManagementHuItem? projectedHu = null,
        long? projectedFutureOrderLineId = null,
        bool useProjection = false)
    {
        if (!_lineById.TryGetValue(orderLineId, out var line))
        {
            throw new InvalidOperationException(
                $"Полный snapshot строки {orderLineId} недоступен. Обновите данные и повторите действие.");
        }

        var finalHuCodes = line.OriginalBoundHuCodes.ToList();
        var finalHuSet = finalHuCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var futureBoundQty = line.CurrentBoundQty;

        foreach (var hu in HuRows)
        {
            var futureOrderLineId = useProjection && ReferenceEquals(hu, projectedHu)
                ? projectedFutureOrderLineId
                : hu.FutureOrderLineId;
            if (hu.OriginalOrderLineId == futureOrderLineId)
            {
                continue;
            }

            if (hu.Qty <= QtyTolerance)
            {
                throw new InvalidOperationException(
                    $"Количество HU '{hu.HuCode}' недоступно. Обновите данные и повторите действие.");
            }

            if (hu.OriginalOrderLineId == orderLineId)
            {
                if (!finalHuSet.Remove(hu.HuCode))
                {
                    throw new InvalidOperationException(
                        $"HU '{hu.HuCode}' отсутствует в полном snapshot строки {orderLineId}. Обновите данные и повторите действие.");
                }

                finalHuCodes.RemoveAll(code => string.Equals(code, hu.HuCode, StringComparison.OrdinalIgnoreCase));
                futureBoundQty -= hu.Qty;
            }

            if (futureOrderLineId == orderLineId)
            {
                if (finalHuSet.Add(hu.HuCode))
                {
                    finalHuCodes.Add(hu.HuCode);
                    futureBoundQty += hu.Qty;
                }
            }
        }

        if (futureBoundQty < -QtyTolerance)
        {
            throw new InvalidOperationException(
                $"Количество привязок строки {orderLineId} не согласовано с полным snapshot. Обновите данные и повторите действие.");
        }

        return new HuAssignmentFutureLineState(
            finalHuCodes
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Math.Max(0, futureBoundQty));
    }

    private bool TryValidateMutationContext(HuAssignmentManagementHuItem hu, out string message)
    {
        message = string.Empty;
        if (hu.Qty <= QtyTolerance)
        {
            message = $"Количество HU '{hu.HuCode}' недоступно. Обновите данные и повторите действие.";
            return false;
        }

        if (!hu.OriginalOrderLineId.HasValue)
        {
            return true;
        }

        if (!_lineById.TryGetValue(hu.OriginalOrderLineId.Value, out var originalLine)
            || originalLine.OrderId != hu.OriginalOrderId
            || !originalLine.OriginalBoundHuCodes.Contains(hu.HuCode, StringComparer.OrdinalIgnoreCase))
        {
            message = $"HU '{hu.HuCode}' отсутствует в полном snapshot исходной строки. Обновите данные и повторите действие.";
            return false;
        }

        return true;
    }

    private bool TryEvaluateProjectedStates(
        HuAssignmentManagementHuItem hu,
        long? projectedFutureOrderLineId,
        IReadOnlyCollection<long> affectedLineIds,
        out IReadOnlyDictionary<long, HuAssignmentFutureLineState> states,
        out string message)
    {
        try
        {
            states = affectedLineIds.ToDictionary(
                lineId => lineId,
                lineId => EvaluateFutureLineState(
                    lineId,
                    hu,
                    projectedFutureOrderLineId,
                    useProjection: true));
            message = string.Empty;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            states = new Dictionary<long, HuAssignmentFutureLineState>();
            message = ex.Message;
            return false;
        }
    }

    internal static string? NormalizeHu(string? huCode) =>
        string.IsNullOrWhiteSpace(huCode) ? null : huCode.Trim().ToUpperInvariant();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed record HuAssignmentFutureLineState(
        IReadOnlyList<string> FinalHuCodes,
        double FutureBoundQty);
}

public sealed class HuAssignmentManagementHuItem
{
    internal HuAssignmentManagementHuItem(WpfHuBindingManageHuRow row, string huCode)
    {
        HuCode = huCode;
        ItemId = row.ItemId;
        ItemName = string.IsNullOrWhiteSpace(row.ItemName) ? $"Товар {row.ItemId}" : row.ItemName;
        Qty = Math.Max(0, row.Qty);
        LocationDisplay = string.IsNullOrWhiteSpace(row.LocationDisplay) ? "-" : row.LocationDisplay;
        State = string.IsNullOrWhiteSpace(row.State) ? (row.CurrentAssignment == null ? "FREE" : "BOUND") : row.State;
        IsMixed = row.IsMixed;
        OriginInternalOrderId = row.OriginInternalOrderId;
        OriginInternalOrderRef = row.OriginInternalOrderRef;
        FirstReceiptAt = row.FirstReceiptAt;

        OriginalOrderId = row.CurrentAssignment?.OrderId;
        OriginalOrderRef = row.CurrentAssignment?.OrderRef;
        OriginalPartnerName = row.CurrentAssignment?.PartnerName;
        OriginalOrderLineId = row.CurrentAssignment?.OrderLineId;
        OriginalOrderStatus = row.CurrentAssignment?.OrderStatus;
        OriginalReservedQty = row.CurrentAssignment?.ReservedQty ?? 0;
        ResetFutureToOriginal();
    }

    public string HuCode { get; }
    public long ItemId { get; }
    public string ItemName { get; }
    public double Qty { get; }
    public string LocationDisplay { get; }
    public string State { get; }
    public bool IsMixed { get; }
    public long? OriginInternalOrderId { get; }
    public string? OriginInternalOrderRef { get; }
    public DateTime? FirstReceiptAt { get; }
    public long? OriginalOrderId { get; private set; }
    public string? OriginalOrderRef { get; private set; }
    public string? OriginalPartnerName { get; private set; }
    public long? OriginalOrderLineId { get; private set; }
    public string? OriginalOrderStatus { get; }
    public double OriginalReservedQty { get; }
    public long? FutureOrderId { get; private set; }
    public string? FutureOrderRef { get; private set; }
    public string? FuturePartnerName { get; private set; }
    public long? FutureOrderLineId { get; private set; }
    public bool IsChanged => OriginalOrderId != FutureOrderId || OriginalOrderLineId != FutureOrderLineId;
    public bool IsFutureFree => !FutureOrderLineId.HasValue;
    public string StateDisplay => OriginalOrderLineId.HasValue ? "Привязан" : "Свободен";
    public string MixedDisplay => IsMixed ? "Да" : "Нет";
    public string FirstReceiptDisplay => FirstReceiptAt.HasValue ? FirstReceiptAt.Value.ToString("dd.MM.yyyy HH:mm") : "-";
    public string OriginalOrderDisplay => string.IsNullOrWhiteSpace(OriginalOrderRef) ? "-" : OriginalOrderRef!;
    public string OriginalPartnerDisplay => string.IsNullOrWhiteSpace(OriginalPartnerName) ? "-" : OriginalPartnerName!;
    public string OriginalOrderStatusDisplay => string.IsNullOrWhiteSpace(OriginalOrderStatus) ? "-" : OriginalOrderStatus!;
    public string OriginInternalOrderDisplay => string.IsNullOrWhiteSpace(OriginInternalOrderRef) ? "-" : OriginInternalOrderRef!;
    public string ChangeDisplay => !IsChanged
        ? "Без изменений"
        : !OriginalOrderLineId.HasValue && FutureOrderLineId.HasValue
            ? "Будет привязан"
            : OriginalOrderLineId.HasValue && !FutureOrderLineId.HasValue
                ? "Будет отвязан"
                : "Будет перенесён";
    public string OriginalAssignmentDisplay => BuildAssignmentDisplay(OriginalOrderRef, OriginalPartnerName, OriginalOrderLineId);
    public string FutureAssignmentDisplay => BuildAssignmentDisplay(FutureOrderRef, FuturePartnerName, FutureOrderLineId);

    public void SetFutureAssignment(long orderId, string orderRef, string? partnerName, long orderLineId)
    {
        FutureOrderId = orderId;
        FutureOrderRef = orderRef;
        FuturePartnerName = partnerName;
        FutureOrderLineId = orderLineId;
    }

    public void ClearFutureAssignment()
    {
        FutureOrderId = null;
        FutureOrderRef = null;
        FuturePartnerName = null;
        FutureOrderLineId = null;
    }

    public void ResetFutureToOriginal()
    {
        FutureOrderId = OriginalOrderId;
        FutureOrderRef = OriginalOrderRef;
        FuturePartnerName = OriginalPartnerName;
        FutureOrderLineId = OriginalOrderLineId;
    }

    public void CommitFutureAsOriginal()
    {
        OriginalOrderId = FutureOrderId;
        OriginalOrderRef = FutureOrderRef;
        OriginalPartnerName = FuturePartnerName;
        OriginalOrderLineId = FutureOrderLineId;
    }

    private static string BuildAssignmentDisplay(string? orderRef, string? partnerName, long? orderLineId)
    {
        if (!orderLineId.HasValue)
        {
            return "Свободен";
        }

        var order = string.IsNullOrWhiteSpace(orderRef) ? $"строка {orderLineId.Value}" : $"заказ {orderRef}";
        var partner = string.IsNullOrWhiteSpace(partnerName) ? string.Empty : $" · {partnerName}";
        return $"{order}{partner} · строка {orderLineId.Value}";
    }
}

public sealed class HuAssignmentManagementTargetLineItem
{
    private readonly double _maxFutureBoundQty;

    internal HuAssignmentManagementTargetLineItem(WpfHuBindingManageTargetLine line)
    {
        OrderId = line.OrderId;
        OrderRef = string.IsNullOrWhiteSpace(line.OrderRef) ? $"ID={line.OrderId}" : line.OrderRef;
        PartnerDisplay = string.IsNullOrWhiteSpace(line.PartnerName) ? "-" : line.PartnerName!;
        OrderStatus = string.IsNullOrWhiteSpace(line.OrderStatus) ? "-" : line.OrderStatus;
        DueAt = line.DueAt;
        OrderLineId = line.OrderLineId;
        ItemId = line.ItemId;
        QtyOrdered = Math.Max(0, line.QtyOrdered);
        QtyShipped = Math.Max(0, line.QtyShipped);
        CurrentBoundQty = Math.Max(0, line.CurrentBoundQty);
        MaxAdditionalBindQty = Math.Max(0, line.MaxAdditionalBindQty);
        _maxFutureBoundQty = CurrentBoundQty + MaxAdditionalBindQty;
        OriginalBoundHuCodes = NormalizeHuCodes(line.CurrentBoundHuCodes);
        FutureBoundHuCodes = OriginalBoundHuCodes;
        FutureBoundQty = CurrentBoundQty;
    }

    public long OrderId { get; }
    public string OrderRef { get; }
    public string PartnerDisplay { get; }
    public string OrderStatus { get; }
    public DateTime? DueAt { get; }
    public long OrderLineId { get; }
    public long ItemId { get; }
    public double QtyOrdered { get; }
    public double QtyShipped { get; }
    public double CurrentBoundQty { get; private set; }
    public double MaxAdditionalBindQty { get; private set; }
    public IReadOnlyList<string> OriginalBoundHuCodes { get; private set; }
    public IReadOnlyList<string> FutureBoundHuCodes { get; private set; }
    public double FutureBoundQty { get; private set; }
    public double MaxFutureBoundQty => _maxFutureBoundQty;
    public double RemainingFutureCapacity => Math.Max(0, MaxFutureBoundQty - FutureBoundQty);
    public string DueAtDisplay => DueAt.HasValue ? DueAt.Value.ToString("dd.MM.yyyy") : "-";
    public string OriginalBoundHuCodesDisplay => OriginalBoundHuCodes.Count == 0 ? "-" : string.Join(", ", OriginalBoundHuCodes);
    public string FutureBoundHuCodesDisplay => FutureBoundHuCodes.Count == 0 ? "-" : string.Join(", ", FutureBoundHuCodes);
    public string DisplayText => $"{OrderRef} · {PartnerDisplay} · строка {OrderLineId} · можно +{RemainingFutureCapacity:0.###}";

    internal void SetFutureBoundState(IReadOnlyList<string> futureHuCodes, double futureQty)
    {
        FutureBoundHuCodes = NormalizeHuCodes(futureHuCodes);
        FutureBoundQty = Math.Max(0, futureQty);
    }

    internal void ReplaceOriginalBoundState(IReadOnlyList<string> huCodes, double boundQty)
    {
        OriginalBoundHuCodes = NormalizeHuCodes(huCodes);
        CurrentBoundQty = Math.Max(0, boundQty);
        FutureBoundHuCodes = OriginalBoundHuCodes;
        FutureBoundQty = CurrentBoundQty;
        MaxAdditionalBindQty = Math.Max(0, MaxFutureBoundQty - CurrentBoundQty);
    }

    private static string[] NormalizeHuCodes(IEnumerable<string> huCodes) =>
        huCodes
            .Select(HuAssignmentManagementSession.NormalizeHu)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

public sealed class HuAssignmentManagementChangeItem
{
    private HuAssignmentManagementChangeItem(
        string huCode,
        long itemId,
        string itemName,
        double qty,
        string changeKind,
        string fromDisplay,
        string toDisplay)
    {
        HuCode = huCode;
        ItemId = itemId;
        ItemName = itemName;
        Qty = qty;
        ChangeKind = changeKind;
        FromDisplay = fromDisplay;
        ToDisplay = toDisplay;
    }

    public string HuCode { get; }
    public long ItemId { get; }
    public string ItemName { get; }
    public double Qty { get; }
    public string ChangeKind { get; }
    public string FromDisplay { get; }
    public string ToDisplay { get; }
    public string DisplayText => $"{ChangeKind}: {HuCode} · {Qty:0.###} · {FromDisplay} -> {ToDisplay}";

    internal static HuAssignmentManagementChangeItem FromHu(HuAssignmentManagementHuItem hu)
    {
        var kind = !hu.OriginalOrderLineId.HasValue && hu.FutureOrderLineId.HasValue
            ? "Привязать"
            : hu.OriginalOrderLineId.HasValue && !hu.FutureOrderLineId.HasValue
                ? "Отвязать"
                : "Перепривязать";
        return new HuAssignmentManagementChangeItem(
            hu.HuCode,
            hu.ItemId,
            hu.ItemName,
            hu.Qty,
            kind,
            hu.OriginalAssignmentDisplay,
            hu.FutureAssignmentDisplay);
    }
}

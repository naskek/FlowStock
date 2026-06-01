using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class OrderScopedHuBindingSession : INotifyPropertyChanged
{
    private const double QtyTolerance = 0.000001d;
    private readonly Dictionary<long, ReadyHuBindingLineItem> _lineById = new();
    private readonly Dictionary<string, ReadyHuBindingCandidateItem> _candidateByHu = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ReadyHuBindingCandidateItem> _candidateOrder = new();
    private readonly List<ReadyHuBindingCandidateItem> _savedReservationCandidates = new();
    private readonly HashSet<string> _expandedCandidateGroupKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedOrderGroupKeys = new(StringComparer.OrdinalIgnoreCase);

    public OrderScopedHuBindingSession(
        Order order,
        IReadOnlyList<OrderLineView> orderLines,
        IReadOnlyList<OrderReceiptPlanLine> savedPlanLines)
    {
        OrderId = order.Id;
        OrderRef = order.OrderRef;
        foreach (var line in orderLines.Where(line => line.Id > 0).OrderBy(line => line.Id))
        {
            var lineItem = new ReadyHuBindingLineItem(line);
            _lineById[line.Id] = lineItem;
            Lines.Add(lineItem);
        }

        foreach (var planLine in savedPlanLines
                     .Where(line => line.OrderLineId > 0 && !string.IsNullOrWhiteSpace(line.ToHu))
                     .OrderBy(line => line.OrderLineId)
                     .ThenBy(line => line.SortOrder)
                     .ThenBy(line => line.Id))
        {
            if (!_lineById.TryGetValue(planLine.OrderLineId, out var lineItem))
            {
                continue;
            }

            var hu = new ReadyHuBindingHuItem(
                lineItem,
                NormalizeHu(planLine.ToHu!)!,
                Math.Max(0, planLine.QtyPlanned),
                "текущая привязка",
                isSaved: true,
                isStagedNew: false);
            lineItem.AddSavedHu(hu);
            var candidate = ReadyHuBindingCandidateItem.FromSavedReservation(lineItem, hu);
            _savedReservationCandidates.Add(candidate);
        }

        RebuildRightTree();
        RebuildCandidateGroups();
    }

    public long OrderId { get; }
    public string OrderRef { get; }
    public ObservableCollection<ReadyHuBindingCandidateGroup> CandidateGroups { get; } = new();
    public ObservableCollection<ReadyHuBindingOrderGroup> OrderGroups { get; } = new();
    public ObservableCollection<ReadyHuBindingLineItem> Lines { get; } = new();
    public IReadOnlySet<string> ExpandedCandidateGroupKeys => _expandedCandidateGroupKeys;
    public IReadOnlySet<string> ExpandedOrderGroupKeys => _expandedOrderGroupKeys;
    public string? SelectedCandidateHuCode { get; private set; }
    public long? SelectedLineId { get; private set; }
    public string? SelectedHuCode { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplyCandidates(WpfHuReservationCandidatesResult result)
    {
        _candidateByHu.Clear();
        _candidateOrder.Clear();

        foreach (var lineResult in result.Lines)
        {
            var orderLineId = lineResult.OrderLineId ?? ParseLineId(lineResult.ClientLineKey);
            if (orderLineId <= 0 || !_lineById.TryGetValue(orderLineId, out var lineItem))
            {
                continue;
            }

            foreach (var candidate in lineResult.Candidates)
            {
                var huCode = NormalizeHu(candidate.HuCode);
                if (string.IsNullOrWhiteSpace(huCode)
                    || candidate.Qty <= QtyTolerance
                    || _candidateByHu.ContainsKey(huCode))
                {
                    continue;
                }

                var item = ReadyHuBindingCandidateItem.FromCandidate(lineItem, candidate);
                _candidateByHu[huCode] = item;
                _candidateOrder.Add(item);
            }
        }

        foreach (var saved in _savedReservationCandidates)
        {
            if (_candidateByHu.ContainsKey(saved.HuCode))
            {
                continue;
            }

            _candidateByHu[saved.HuCode] = saved;
        }

        RebuildCandidateGroups();
    }

    public bool StageBind(ReadyHuBindingCandidateItem? candidate, ReadyHuBindingLineItem? targetLine, out string message)
    {
        message = string.Empty;
        if (candidate == null || targetLine == null)
        {
            message = "Выберите HU и строку заказа.";
            return false;
        }

        if (candidate.ItemId != targetLine.ItemId)
        {
            message = "HU не соответствует товару строки заказа.";
            return false;
        }

        if (IsHuInFinalState(candidate.HuCode))
        {
            message = "HU уже выбран в заказе.";
            return false;
        }

        if (targetLine.FinalBoundQty + candidate.Qty > targetLine.MaxReadyHuQty + QtyTolerance)
        {
            message = "Количество HU превышает остаток строки заказа.";
            return false;
        }

        targetLine.AddFutureHu(new ReadyHuBindingHuItem(
            targetLine,
            candidate.HuCode,
            candidate.Qty,
            candidate.SourceText,
            isSaved: targetLine.SavedHuCodes.Contains(candidate.HuCode),
            isStagedNew: !targetLine.SavedHuCodes.Contains(candidate.HuCode)));
        RebuildRightTree();
        RebuildCandidateGroups();
        return true;
    }

    public bool StageDetach(ReadyHuBindingHuItem? hu, out string message)
    {
        message = string.Empty;
        if (hu == null)
        {
            message = "Выберите HU справа.";
            return false;
        }

        hu.Line.RemoveFutureHu(hu.HuCode);
        RebuildRightTree();
        RebuildCandidateGroups();
        return true;
    }

    public void StageAuto()
    {
        foreach (var line in Lines)
        {
            var remaining = Math.Max(0, line.MaxReadyHuQty - line.FinalBoundQty);
            if (remaining <= QtyTolerance)
            {
                continue;
            }

            foreach (var candidate in _candidateOrder.Where(candidate => candidate.ItemId == line.ItemId).ToArray())
            {
                if (remaining <= QtyTolerance)
                {
                    break;
                }

                if (IsHuInFinalState(candidate.HuCode) || candidate.Qty > remaining + QtyTolerance)
                {
                    continue;
                }

                if (StageBind(candidate, line, out _))
                {
                    remaining = Math.Max(0, line.MaxReadyHuQty - line.FinalBoundQty);
                }
            }
        }
    }

    public IReadOnlyList<WpfHuBindingApplyFinalLineRequest> BuildApplyFinalLines()
    {
        return Lines
            .Where(line => line.IsAffected)
            .Select(line => new WpfHuBindingApplyFinalLineRequest
            {
                OrderLineId = line.OrderLineId,
                ExpectedBoundHuCodes = line.SavedHuCodes.ToArray(),
                FinalHuCodes = line.FutureHuCodes.ToArray()
            })
            .ToArray();
    }

    public IReadOnlyList<WpfHuReservationCandidatesLineRequest> BuildCandidatesRequestLines()
    {
        return Lines
            .Where(line => line.ItemId > 0 && line.QtyOrdered > QtyTolerance)
            .Select(line => new WpfHuReservationCandidatesLineRequest
            {
                ClientLineKey = $"line-{line.OrderLineId}",
                OrderLineId = line.OrderLineId,
                ItemId = line.ItemId,
                QtyOrdered = line.QtyOrdered
            })
            .ToArray();
    }

    public void CaptureUiState(
        IEnumerable<string> expandedCandidateGroupKeys,
        IEnumerable<string> expandedOrderGroupKeys,
        string? selectedCandidateHuCode,
        long? selectedLineId,
        string? selectedHuCode)
    {
        _expandedCandidateGroupKeys.Clear();
        foreach (var key in expandedCandidateGroupKeys.Where(key => !string.IsNullOrWhiteSpace(key)))
        {
            _expandedCandidateGroupKeys.Add(key);
        }

        _expandedOrderGroupKeys.Clear();
        foreach (var key in expandedOrderGroupKeys.Where(key => !string.IsNullOrWhiteSpace(key)))
        {
            _expandedOrderGroupKeys.Add(key);
        }

        SelectedCandidateHuCode = NormalizeHu(selectedCandidateHuCode);
        SelectedLineId = selectedLineId;
        SelectedHuCode = NormalizeHu(selectedHuCode);
    }

    public void ExpandCandidateGroup(long itemId) => _expandedCandidateGroupKeys.Add(ReadyHuBindingCandidateGroup.BuildKey(itemId));

    public void ExpandOrderRoot() => _expandedOrderGroupKeys.Add(ReadyHuBindingOrderGroup.RootKey);

    public void ExpandOrderLine(long orderLineId) => _expandedOrderGroupKeys.Add(ReadyHuBindingLineItem.BuildKey(orderLineId));

    public ReadyHuBindingCandidateItem? FindCandidate(string? huCode)
    {
        var normalized = NormalizeHu(huCode);
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : CandidateGroups.SelectMany(group => group.Candidates)
                .FirstOrDefault(candidate => string.Equals(candidate.HuCode, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public ReadyHuBindingLineItem? FindLine(long? orderLineId) =>
        orderLineId.HasValue && _lineById.TryGetValue(orderLineId.Value, out var line) ? line : null;

    public ReadyHuBindingHuItem? FindFutureHu(string? huCode)
    {
        var normalized = NormalizeHu(huCode);
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : Lines.SelectMany(line => line.FutureHu)
                .FirstOrDefault(hu => string.Equals(hu.HuCode, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsHuInFinalState(string huCode) =>
        Lines.Any(line => line.FutureHuCodes.Contains(huCode, StringComparer.OrdinalIgnoreCase));

    private void RebuildRightTree()
    {
        OrderGroups.Clear();
        OrderGroups.Add(new ReadyHuBindingOrderGroup($"Заказ {OrderRef}", Lines));
        foreach (var line in Lines)
        {
            line.RefreshComputed();
        }

        OnPropertyChanged(nameof(OrderGroups));
    }

    private void RebuildCandidateGroups()
    {
        var assigned = Lines
            .SelectMany(line => line.FutureHuCodes)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = _candidateByHu.Values
            .Where(candidate => !assigned.Contains(candidate.HuCode))
            .OrderBy(candidate => candidate.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.SortIndex)
            .ToArray();

        CandidateGroups.Clear();
        foreach (var group in rows.GroupBy(candidate => new { candidate.ItemId, candidate.ItemName }))
        {
            CandidateGroups.Add(new ReadyHuBindingCandidateGroup(
                group.Key.ItemId,
                group.Key.ItemName,
                group.ToArray()));
        }

        OnPropertyChanged(nameof(CandidateGroups));
    }

    private static long ParseLineId(string? clientLineKey)
    {
        if (string.IsNullOrWhiteSpace(clientLineKey) || !clientLineKey.StartsWith("line-", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return long.TryParse(clientLineKey[5..], out var lineId) ? lineId : 0;
    }

    internal static string? NormalizeHu(string? huCode) =>
        string.IsNullOrWhiteSpace(huCode) ? null : huCode.Trim().ToUpperInvariant();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class ReadyHuBindingCandidateGroup
{
    public ReadyHuBindingCandidateGroup(long itemId, string itemName, IReadOnlyList<ReadyHuBindingCandidateItem> candidates)
    {
        ItemId = itemId;
        ItemName = itemName;
        Candidates = candidates;
    }

    public long ItemId { get; }
    public string ItemName { get; }
    public IReadOnlyList<ReadyHuBindingCandidateItem> Candidates { get; }
    public string ExpandKey => BuildKey(ItemId);

    public static string BuildKey(long itemId) => $"candidate-item-{itemId}";
}

public sealed class ReadyHuBindingCandidateItem
{
    private ReadyHuBindingCandidateItem(
        long itemId,
        string itemName,
        string huCode,
        double qty,
        string sourceText,
        int sortIndex)
    {
        ItemId = itemId;
        ItemName = itemName;
        HuCode = huCode;
        Qty = qty;
        SourceText = sourceText;
        SortIndex = sortIndex;
    }

    public long ItemId { get; }
    public string ItemName { get; }
    public string HuCode { get; }
    public double Qty { get; }
    public string SourceText { get; }
    public int SortIndex { get; }
    public string DisplayText => $"{HuCode} · {Qty:0.###} · {SourceText}";

    public static ReadyHuBindingCandidateItem FromCandidate(ReadyHuBindingLineItem line, WpfHuReservationCandidateRow candidate) =>
        new(
            line.ItemId,
            line.ItemName,
            OrderScopedHuBindingSession.NormalizeHu(candidate.HuCode) ?? string.Empty,
            Math.Max(0, candidate.Qty),
            BuildSourceText(candidate),
            sortIndex: line.NextCandidateSortIndex());

    public static ReadyHuBindingCandidateItem FromSavedReservation(ReadyHuBindingLineItem line, ReadyHuBindingHuItem hu) =>
        new(line.ItemId, line.ItemName, hu.HuCode, hu.Qty, hu.SourceText, sortIndex: int.MaxValue);

    private static string BuildSourceText(WpfHuReservationCandidateRow candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.SourceOrderRef))
        {
            return $"внутр. заказ {candidate.SourceOrderRef}";
        }

        if (!string.IsNullOrWhiteSpace(candidate.SourcePrdRef))
        {
            return $"PRD {candidate.SourcePrdRef}";
        }

        if (string.Equals(candidate.Source, "LEDGER_STOCK", StringComparison.OrdinalIgnoreCase))
        {
            return "склад";
        }

        return string.IsNullOrWhiteSpace(candidate.Source) ? "готовый HU" : candidate.Source;
    }
}

public sealed class ReadyHuBindingOrderGroup
{
    public ReadyHuBindingOrderGroup(string title, IReadOnlyList<ReadyHuBindingLineItem> lines)
    {
        Title = title;
        Lines = lines;
    }

    public string Title { get; }
    public IReadOnlyList<ReadyHuBindingLineItem> Lines { get; }
    public string ExpandKey => RootKey;
    public const string RootKey = "order-root";
}

public sealed class ReadyHuBindingLineItem : INotifyPropertyChanged
{
    private int _candidateSortIndex;

    public ReadyHuBindingLineItem(OrderLineView line)
    {
        OrderLineId = line.Id;
        ItemId = line.ItemId;
        ItemName = string.IsNullOrWhiteSpace(line.ItemName) ? $"Товар {line.ItemId}" : line.ItemName;
        QtyOrdered = Math.Max(0, line.QtyOrdered);
        QtyShipped = Math.Max(0, line.QtyShipped);
        PlannedPalletQty = Math.Max(0, line.PlannedPalletQty);
        ProductionHuDisplayEntries = line.ProductionHuDisplayEntries;
    }

    public long OrderLineId { get; }
    public long ItemId { get; }
    public string ItemName { get; }
    public double QtyOrdered { get; }
    public double QtyShipped { get; }
    public double PlannedPalletQty { get; }
    public double MaxReadyHuQty => Math.Max(0, QtyOrdered - QtyShipped);
    public IReadOnlyList<OrderLineHuDisplayEntry> ProductionHuDisplayEntries { get; }
    public ObservableCollection<ReadyHuBindingHuItem> FutureHu { get; } = new();
    public HashSet<string> SavedHuCodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> FutureHuCodes => FutureHu.Select(hu => hu.HuCode).ToArray();
    public double FinalBoundQty => FutureHu.Sum(hu => Math.Max(0, hu.Qty));
    public bool IsAffected => !SavedHuCodes.SetEquals(FutureHuCodes);
    public string Header => $"{ItemName} · заказано {QtyOrdered:0.###} · отгружено {QtyShipped:0.###}";
    public string Summary => $"Будет привязано {FinalBoundQty:0.###} из {MaxReadyHuQty:0.###}";
    public string ExpandKey => BuildKey(OrderLineId);
    public event PropertyChangedEventHandler? PropertyChanged;

    public void AddSavedHu(ReadyHuBindingHuItem hu)
    {
        if (SavedHuCodes.Add(hu.HuCode))
        {
            FutureHu.Add(hu);
        }

        RefreshComputed();
    }

    public void AddFutureHu(ReadyHuBindingHuItem hu)
    {
        if (FutureHu.Any(existing => string.Equals(existing.HuCode, hu.HuCode, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        FutureHu.Add(hu);
        RefreshComputed();
    }

    public void RemoveFutureHu(string huCode)
    {
        var existing = FutureHu.FirstOrDefault(hu => string.Equals(hu.HuCode, huCode, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            FutureHu.Remove(existing);
        }

        RefreshComputed();
    }

    public int NextCandidateSortIndex() => _candidateSortIndex++;

    public static string BuildKey(long orderLineId) => $"order-line-{orderLineId}";

    public void RefreshComputed()
    {
        OnPropertyChanged(nameof(FutureHu));
        OnPropertyChanged(nameof(FutureHuCodes));
        OnPropertyChanged(nameof(FinalBoundQty));
        OnPropertyChanged(nameof(IsAffected));
        OnPropertyChanged(nameof(Summary));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class ReadyHuBindingHuItem
{
    public ReadyHuBindingHuItem(
        ReadyHuBindingLineItem line,
        string huCode,
        double qty,
        string sourceText,
        bool isSaved,
        bool isStagedNew)
    {
        Line = line;
        HuCode = huCode;
        Qty = qty;
        SourceText = sourceText;
        IsSaved = isSaved;
        IsStagedNew = isStagedNew;
    }

    public ReadyHuBindingLineItem Line { get; }
    public string HuCode { get; }
    public double Qty { get; }
    public string SourceText { get; }
    public bool IsSaved { get; }
    public bool IsStagedNew { get; }
    public string DisplayText => IsStagedNew
        ? $"{HuCode} · {Qty:0.###} · {SourceText} [новое]"
        : $"{HuCode} · {Qty:0.###} · {SourceText}";
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlowStock.App;

public sealed class GlobalReadyHuBindingSession : INotifyPropertyChanged
{
    private const double QtyTolerance = 0.000001d;
    private readonly Dictionary<string, GlobalReadyHuCandidateItem> _candidateByHu = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, GlobalReadyHuCompatibleLineItem> _lineById = new();
    private readonly List<GlobalReadyHuCandidateItem> _fifoCandidates = new();

    public GlobalReadyHuBindingSession(WpfReadyHuBindingReadModel readModel)
    {
        RefreshFrom(readModel);
    }

    public ObservableCollection<GlobalReadyHuCandidateGroup> CandidateGroups { get; } = new();
    public ObservableCollection<GlobalReadyHuCompatibleOrderGroup> CompatibleOrderGroups { get; } = new();
    public ObservableCollection<GlobalReadyHuStagedBinding> StagedBindings { get; } = new();
    public GlobalReadyHuCandidateItem? SelectedHu { get; private set; }
    public bool HasStagedChanges => StagedBindings.Count > 0;
    public string Summary => $"Свободных HU: {AvailableCandidateCount} · подготовлено: {StagedBindings.Count}";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshFrom(WpfReadyHuBindingReadModel readModel)
    {
        _candidateByHu.Clear();
        _lineById.Clear();
        _fifoCandidates.Clear();
        StagedBindings.Clear();
        SelectedHu = null;

        var sortIndex = 0;
        foreach (var huRow in readModel.HuRows)
        {
            var huCode = NormalizeHu(huRow.HuCode);
            if (string.IsNullOrWhiteSpace(huCode) || huRow.Qty <= QtyTolerance || _candidateByHu.ContainsKey(huCode))
            {
                continue;
            }

            var candidate = new GlobalReadyHuCandidateItem(huRow, huCode, sortIndex++);
            _candidateByHu[huCode] = candidate;
            _fifoCandidates.Add(candidate);

            foreach (var order in huRow.CompatibleOrders)
            {
                foreach (var line in order.Lines)
                {
                    if (line.OrderLineId <= 0)
                    {
                        continue;
                    }

                    if (!_lineById.ContainsKey(line.OrderLineId))
                    {
                        _lineById[line.OrderLineId] = new GlobalReadyHuCompatibleLineItem(candidate.HuCode, order, line);
                    }
                }
            }
        }

        RebuildCandidateGroups();
        RebuildCompatibleOrderGroups();
        NotifyAll();
    }

    public void SelectHu(GlobalReadyHuCandidateItem? hu)
    {
        SelectedHu = hu == null ? null : FindCandidate(hu.HuCode);
        RebuildCompatibleOrderGroups();
        OnPropertyChanged(nameof(SelectedHu));
    }

    public bool StageBind(
        GlobalReadyHuCandidateItem? hu,
        GlobalReadyHuCompatibleLineItem? line,
        out string message)
    {
        message = string.Empty;
        if (hu == null || line == null)
        {
            message = "Выберите HU и строку заказа.";
            return false;
        }

        var candidate = FindCandidate(hu.HuCode);
        if (candidate == null)
        {
            message = "HU уже подготовлен к привязке или недоступен.";
            return false;
        }

        if (!string.Equals(candidate.HuCode, line.HuCode, StringComparison.OrdinalIgnoreCase))
        {
            message = "Строка не относится к выбранному HU.";
            return false;
        }

        if (candidate.ItemId != line.ItemId)
        {
            message = "HU не соответствует товару строки заказа.";
            return false;
        }

        if (IsHuStaged(candidate.HuCode))
        {
            message = "HU уже подготовлен к привязке.";
            return false;
        }

        if (candidate.Qty > GetRemainingCapacity(line.OrderLineId) + QtyTolerance)
        {
            message = "Количество HU превышает доступный остаток строки заказа.";
            return false;
        }

        StagedBindings.Add(new GlobalReadyHuStagedBinding(candidate, line));
        SelectedHu = null;
        RebuildCandidateGroups();
        RebuildCompatibleOrderGroups();
        NotifyAll();
        return true;
    }

    public bool StageDetach(GlobalReadyHuStagedBinding? stagedBinding, out string message)
    {
        message = string.Empty;
        if (stagedBinding == null)
        {
            message = "Выберите подготовленную привязку.";
            return false;
        }

        var existing = StagedBindings.FirstOrDefault(binding => ReferenceEquals(binding, stagedBinding)
            || string.Equals(binding.HuCode, stagedBinding.HuCode, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            message = "Подготовленная привязка не найдена.";
            return false;
        }

        StagedBindings.Remove(existing);
        RebuildCandidateGroups();
        RebuildCompatibleOrderGroups();
        NotifyAll();
        return true;
    }

    public void StageAuto()
    {
        foreach (var candidate in _fifoCandidates.ToArray())
        {
            if (FindCandidate(candidate.HuCode) == null)
            {
                continue;
            }

            var line = BuildCompatibleLines(candidate)
                .Where(line => candidate.Qty <= GetRemainingCapacity(line.OrderLineId) + QtyTolerance)
                .OrderBy(line => line.DueDate.HasValue ? 0 : 1)
                .ThenBy(line => line.DueDate ?? DateTime.MaxValue)
                .ThenBy(line => line.OrderCreatedAt)
                .ThenBy(line => line.OrderRef, StringComparer.OrdinalIgnoreCase)
                .ThenBy(line => line.OrderId)
                .ThenBy(line => line.OrderLineId)
                .FirstOrDefault();
            if (line != null)
            {
                StageBind(candidate, line, out _);
            }
        }
    }

    public IReadOnlyList<GlobalReadyHuBindingApplyOrderBatch> BuildApplyFinalByOrder()
    {
        return StagedBindings
            .GroupBy(binding => new { binding.OrderId, binding.OrderRef })
            .OrderBy(group => group.Key.OrderId)
            .Select(group =>
            {
                var lines = group
                    .GroupBy(binding => binding.OrderLineId)
                    .OrderBy(lineGroup => lineGroup.Key)
                    .Select(lineGroup =>
                    {
                        var first = lineGroup.First();
                        var finalHuCodes = first.CurrentBoundHuCodes
                            .Concat(lineGroup.Select(binding => binding.HuCode))
                            .ToArray();
                        return new WpfHuBindingApplyFinalLineRequest
                        {
                            OrderLineId = lineGroup.Key,
                            ExpectedBoundHuCodes = first.CurrentBoundHuCodes.ToArray(),
                            FinalHuCodes = finalHuCodes
                        };
                    })
                    .ToArray();

                return new GlobalReadyHuBindingApplyOrderBatch(group.Key.OrderId, group.Key.OrderRef, lines);
            })
            .ToArray();
    }

    public void MarkOrderApplySuccess(long orderId)
    {
        var successful = StagedBindings
            .Where(binding => binding.OrderId == orderId)
            .ToArray();
        foreach (var binding in successful)
        {
            StagedBindings.Remove(binding);
        }

        RebuildCandidateGroups();
        RebuildCompatibleOrderGroups();
        NotifyAll();
    }

    public GlobalReadyHuCandidateItem? FindCandidate(string? huCode)
    {
        var normalized = NormalizeHu(huCode);
        if (string.IsNullOrWhiteSpace(normalized) || !_candidateByHu.TryGetValue(normalized, out var candidate))
        {
            return null;
        }

        return IsHuStaged(candidate.HuCode) ? null : candidate;
    }

    public GlobalReadyHuCompatibleLineItem? FindCompatibleLine(long orderLineId) =>
        CompatibleOrderGroups
            .SelectMany(group => group.Lines)
            .FirstOrDefault(line => line.OrderLineId == orderLineId);

    private int AvailableCandidateCount => _candidateByHu.Values.Count(candidate => !IsHuStaged(candidate.HuCode));

    private void RebuildCandidateGroups()
    {
        var rows = _candidateByHu.Values
            .Where(candidate => !IsHuStaged(candidate.HuCode))
            .OrderBy(candidate => candidate.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.SortIndex)
            .ToArray();

        CandidateGroups.Clear();
        foreach (var group in rows.GroupBy(row => new { row.ItemId, row.ItemName }))
        {
            CandidateGroups.Add(new GlobalReadyHuCandidateGroup(
                group.Key.ItemId,
                group.Key.ItemName,
                group.ToArray()));
        }
    }

    private void RebuildCompatibleOrderGroups()
    {
        CompatibleOrderGroups.Clear();
        if (SelectedHu == null || FindCandidate(SelectedHu.HuCode) == null)
        {
            OnPropertyChanged(nameof(CompatibleOrderGroups));
            return;
        }

        var lines = BuildCompatibleLines(SelectedHu)
            .Where(line => GetRemainingCapacity(line.OrderLineId) + QtyTolerance >= SelectedHu.Qty)
            .ToArray();

        foreach (var group in lines.GroupBy(line => new
                 {
                     line.OrderId,
                     line.OrderRef,
                     line.PartnerDisplay,
                     line.Status,
                     line.DueDate,
                     line.OrderCreatedAt
                 }))
        {
            CompatibleOrderGroups.Add(new GlobalReadyHuCompatibleOrderGroup(
                group.Key.OrderId,
                group.Key.OrderRef,
                group.Key.PartnerDisplay,
                group.Key.Status,
                group.Key.DueDate,
                group.Key.OrderCreatedAt,
                group.OrderBy(line => line.OrderLineId).ToArray()));
        }

        OnPropertyChanged(nameof(CompatibleOrderGroups));
    }

    private IReadOnlyList<GlobalReadyHuCompatibleLineItem> BuildCompatibleLines(GlobalReadyHuCandidateItem candidate)
    {
        return candidate.CompatibleOrders
            .SelectMany(order => order.Lines.Select(line => new GlobalReadyHuCompatibleLineItem(candidate.HuCode, order, line)))
            .Where(line => candidate.ItemId == line.ItemId)
            .ToArray();
    }

    private double GetRemainingCapacity(long orderLineId)
    {
        var stagedQty = StagedBindings
            .Where(binding => binding.OrderLineId == orderLineId)
            .Sum(binding => Math.Max(0, binding.Qty));
        if (_lineById.TryGetValue(orderLineId, out var line))
        {
            return Math.Max(0, line.MaxAdditionalBindQty - stagedQty);
        }

        return 0;
    }

    private bool IsHuStaged(string huCode) =>
        StagedBindings.Any(binding => string.Equals(binding.HuCode, huCode, StringComparison.OrdinalIgnoreCase));

    internal static string? NormalizeHu(string? huCode) =>
        string.IsNullOrWhiteSpace(huCode) ? null : huCode.Trim().ToUpperInvariant();

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(CandidateGroups));
        OnPropertyChanged(nameof(CompatibleOrderGroups));
        OnPropertyChanged(nameof(StagedBindings));
        OnPropertyChanged(nameof(HasStagedChanges));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SelectedHu));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class GlobalReadyHuCandidateGroup
{
    public GlobalReadyHuCandidateGroup(long itemId, string itemName, IReadOnlyList<GlobalReadyHuCandidateItem> candidates)
    {
        ItemId = itemId;
        ItemName = string.IsNullOrWhiteSpace(itemName) ? $"Товар {itemId}" : itemName;
        Candidates = candidates;
    }

    public long ItemId { get; }
    public string ItemName { get; }
    public IReadOnlyList<GlobalReadyHuCandidateItem> Candidates { get; }
}

public sealed class GlobalReadyHuCandidateItem
{
    public GlobalReadyHuCandidateItem(WpfReadyHuBindingHuRow row, string huCode, int sortIndex)
    {
        HuCode = huCode;
        ItemId = row.ItemId;
        ItemName = string.IsNullOrWhiteSpace(row.ItemName) ? $"Товар {row.ItemId}" : row.ItemName;
        Qty = Math.Max(0, row.Qty);
        LocationDisplay = string.IsNullOrWhiteSpace(row.LocationDisplay) ? "-" : row.LocationDisplay;
        Source = string.IsNullOrWhiteSpace(row.Source) ? "LEDGER_STOCK" : row.Source;
        OriginInternalOrderRef = row.OriginInternalOrderRef;
        FirstReceiptAt = row.FirstReceiptAt;
        FirstReceiptDocId = row.FirstReceiptDocId;
        CompatibleOrders = row.CompatibleOrders;
        SortIndex = sortIndex;
    }

    public string HuCode { get; }
    public long ItemId { get; }
    public string ItemName { get; }
    public double Qty { get; }
    public string LocationDisplay { get; }
    public string Source { get; }
    public string? OriginInternalOrderRef { get; }
    public DateTime? FirstReceiptAt { get; }
    public long? FirstReceiptDocId { get; }
    public IReadOnlyList<WpfReadyHuBindingCompatibleOrderRow> CompatibleOrders { get; }
    public int SortIndex { get; }
    public string SourceDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OriginInternalOrderRef))
            {
                return $"внутр. заказ {OriginInternalOrderRef}";
            }

            return Source.Equals("LEDGER_STOCK", StringComparison.OrdinalIgnoreCase) ? "склад" : Source;
        }
    }

    public string DisplayText => $"{HuCode} · {Qty:0.###} · {LocationDisplay} · {SourceDisplay}";
}

public sealed class GlobalReadyHuCompatibleOrderGroup
{
    public GlobalReadyHuCompatibleOrderGroup(
        long orderId,
        string orderRef,
        string partnerDisplay,
        string status,
        DateTime? dueDate,
        DateTime orderCreatedAt,
        IReadOnlyList<GlobalReadyHuCompatibleLineItem> lines)
    {
        OrderId = orderId;
        OrderRef = string.IsNullOrWhiteSpace(orderRef) ? $"ID={orderId}" : orderRef;
        PartnerDisplay = partnerDisplay;
        Status = string.IsNullOrWhiteSpace(status) ? "-" : status;
        DueDate = dueDate;
        OrderCreatedAt = orderCreatedAt;
        Lines = lines;
    }

    public long OrderId { get; }
    public string OrderRef { get; }
    public string PartnerDisplay { get; }
    public string Status { get; }
    public DateTime? DueDate { get; }
    public DateTime OrderCreatedAt { get; }
    public IReadOnlyList<GlobalReadyHuCompatibleLineItem> Lines { get; }
    public string Header => $"Заказ {OrderRef} · {PartnerDisplay} · {Status} · срок: {(DueDate.HasValue ? DueDate.Value.ToString("dd/MM/yyyy") : "-")}";
}

public sealed class GlobalReadyHuCompatibleLineItem
{
    public GlobalReadyHuCompatibleLineItem(
        string huCode,
        WpfReadyHuBindingCompatibleOrderRow order,
        WpfReadyHuBindingCompatibleLineRow line)
    {
        HuCode = huCode;
        OrderId = order.OrderId;
        OrderRef = string.IsNullOrWhiteSpace(order.OrderRef) ? $"ID={order.OrderId}" : order.OrderRef;
        PartnerDisplay = !string.IsNullOrWhiteSpace(order.PartnerName)
            ? order.PartnerName!
            : !string.IsNullOrWhiteSpace(order.PartnerCode) ? order.PartnerCode! : "-";
        Status = string.IsNullOrWhiteSpace(order.Status) ? "-" : order.Status;
        DueDate = order.DueDate;
        OrderCreatedAt = order.CreatedAt;
        OrderLineId = line.OrderLineId;
        ItemId = line.ItemId;
        ItemName = string.IsNullOrWhiteSpace(line.ItemName) ? $"Товар {line.ItemId}" : line.ItemName;
        QtyOrdered = Math.Max(0, line.QtyOrdered);
        QtyShipped = Math.Max(0, line.QtyShipped);
        ShipmentRemainingQty = Math.Max(0, line.ShipmentRemainingQty);
        CurrentBoundHuCodes = line.CurrentBoundHuCodes
            .Select(GlobalReadyHuBindingSession.NormalizeHu)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        CurrentBoundQty = Math.Max(0, line.CurrentBoundQty);
        MaxAdditionalBindQty = Math.Max(0, line.MaxAdditionalBindQty);
    }

    public string HuCode { get; }
    public long OrderId { get; }
    public string OrderRef { get; }
    public string PartnerDisplay { get; }
    public string Status { get; }
    public DateTime? DueDate { get; }
    public DateTime OrderCreatedAt { get; }
    public long OrderLineId { get; }
    public long ItemId { get; }
    public string ItemName { get; }
    public double QtyOrdered { get; }
    public double QtyShipped { get; }
    public double ShipmentRemainingQty { get; }
    public IReadOnlyList<string> CurrentBoundHuCodes { get; }
    public double CurrentBoundQty { get; }
    public double MaxAdditionalBindQty { get; }
    public string DisplayText => $"{ItemName} · заказ {QtyOrdered:0.###} · отгр. {QtyShipped:0.###} · уже HU {CurrentBoundQty:0.###} · можно +{MaxAdditionalBindQty:0.###}";
}

public sealed class GlobalReadyHuStagedBinding
{
    public GlobalReadyHuStagedBinding(GlobalReadyHuCandidateItem hu, GlobalReadyHuCompatibleLineItem line)
    {
        HuCode = hu.HuCode;
        ItemId = hu.ItemId;
        ItemName = hu.ItemName;
        Qty = hu.Qty;
        OrderId = line.OrderId;
        OrderRef = line.OrderRef;
        PartnerDisplay = line.PartnerDisplay;
        OrderLineId = line.OrderLineId;
        CurrentBoundHuCodes = line.CurrentBoundHuCodes;
    }

    public string HuCode { get; }
    public long ItemId { get; }
    public string ItemName { get; }
    public double Qty { get; }
    public long OrderId { get; }
    public string OrderRef { get; }
    public string PartnerDisplay { get; }
    public long OrderLineId { get; }
    public IReadOnlyList<string> CurrentBoundHuCodes { get; }
    public string DisplayText => $"{HuCode} -> заказ {OrderRef}, строка {OrderLineId}";
}

public sealed record GlobalReadyHuBindingApplyOrderBatch(
    long OrderId,
    string OrderRef,
    IReadOnlyList<WpfHuBindingApplyFinalLineRequest> Lines);

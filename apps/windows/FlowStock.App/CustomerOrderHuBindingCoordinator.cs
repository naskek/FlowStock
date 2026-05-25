using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using FlowStock.Core.Models;

namespace FlowStock.App;

public sealed class CustomerOrderHuBindingCoordinator : IDisposable
{
    private const double QtyTolerance = 0.000001;
    private readonly WpfReadApiService _readApi;
    private readonly Func<long, IReadOnlyList<OrderReceiptPlanLine>> _loadPlanLines;
    private readonly DispatcherTimer _debounceTimer;
    private readonly Dictionary<string, CustomerOrderLineHuState> _states = new(StringComparer.OrdinalIgnoreCase);
    private long? _orderId;
    private bool _isLoading;
    private bool _isCustomerOrder;
    private int _refreshGeneration;

    public CustomerOrderHuBindingCoordinator(
        WpfReadApiService readApi,
        Func<long, IReadOnlyList<OrderReceiptPlanLine>> loadPlanLines)
    {
        _readApi = readApi;
        _loadPlanLines = loadPlanLines;
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _ = RefreshCandidatesAsync();
        };
    }

    public ObservableCollection<CustomerOrderLinePresentation> Lines { get; } = new();

    public bool IsCustomerOrderActive => _isCustomerOrder;

    public void ResetForNewOrder()
    {
        _orderId = null;
        _isCustomerOrder = true;
        _states.Clear();
        Lines.Clear();
        _debounceTimer.Stop();
    }

    public void BeginLoad()
    {
        _isLoading = true;
        _debounceTimer.Stop();
    }

    public void EndLoad()
    {
        _isLoading = false;
        ScheduleCandidatesRefresh();
    }

    public bool EnsureLineCandidatesLoaded(string clientLineKey)
    {
        if (!_isCustomerOrder)
        {
            return false;
        }

        _debounceTimer.Stop();
        RefreshCandidatesAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        return _states.TryGetValue(clientLineKey, out var state) && !state.CandidatesLoadFailed;
    }

    public IReadOnlySet<string> GetSelectedHuCodesOnOtherLines(string clientLineKey)
    {
        return CustomerOrderHuPickerRules
            .BuildExcludeHuCodesForOtherLines(_states.Values, clientLineKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void SetOrderContext(long? orderId, OrderType orderType, IEnumerable<OrderLineView?>? lines)
    {
        _orderId = orderId;
        _isCustomerOrder = orderType == OrderType.Customer;
        if (!_isCustomerOrder)
        {
            _states.Clear();
            Lines.Clear();
            _debounceTimer.Stop();
            return;
        }

        var existingByKey = _states.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        _states.Clear();
        Lines.Clear();

        var index = 0;
        foreach (var line in lines ?? Array.Empty<OrderLineView>())
        {
            if (line == null)
            {
                continue;
            }

            var key = ResolveClientLineKey(line, index++);
            if (!existingByKey.TryGetValue(key, out var state))
            {
                state = new CustomerOrderLineHuState(key);
            }

            state.AttachLine(line, orderId);
            _states[key] = state;
            Lines.Add(new CustomerOrderLinePresentation(state));
        }

        if (orderId.HasValue)
        {
            ApplyExistingPlanLines(orderId.Value);
        }

        ScheduleCandidatesRefresh();
    }

    public void NotifyLineChanged(OrderLineView? line)
    {
        if (!_isCustomerOrder || _isLoading)
        {
            return;
        }

        if (line == null)
        {
            return;
        }

        var key = FindClientLineKey(line) ?? ResolveClientLineKey(line, Lines.Count);
        if (!_states.TryGetValue(key, out var state))
        {
            state = new CustomerOrderLineHuState(key);
            state.AttachLine(line, _orderId);
            _states[key] = state;
            Lines.Add(new CustomerOrderLinePresentation(state));
        }
        else
        {
            state.AttachLine(line, _orderId);
        }

        ScheduleCandidatesRefresh();
    }

    public void RemoveLine(OrderLineView? line)
    {
        if (line == null)
        {
            return;
        }

        var key = FindClientLineKey(line);
        if (key != null && _states.Remove(key, out _))
        {
            var row = Lines.FirstOrDefault(entry => string.Equals(entry.ClientLineKey, key, StringComparison.OrdinalIgnoreCase));
            if (row != null)
            {
                Lines.Remove(row);
            }
        }

        ScheduleCandidatesRefresh();
    }

    public void ApplyPickerSelection(string clientLineKey, IReadOnlyCollection<string> selectedHuCodes)
    {
        if (!_states.TryGetValue(clientLineKey, out var state))
        {
            return;
        }

        state.ApplyManualSelection(selectedHuCodes);
    }

    public IReadOnlyList<WpfHuReservationApplyLineRequest> BuildApplyLines()
    {
        if (!_orderId.HasValue)
        {
            return Array.Empty<WpfHuReservationApplyLineRequest>();
        }

        var requests = new List<WpfHuReservationApplyLineRequest>();
        foreach (var state in _states.Values)
        {
            if (!state.ShouldSendOnApply)
            {
                continue;
            }

            if (state.Line.Id <= 0)
            {
                continue;
            }

            requests.Add(new WpfHuReservationApplyLineRequest
            {
                OrderLineId = state.Line.Id,
                SelectedHuCodes = state.SelectedHuCodes.ToArray()
            });
        }

        return requests;
    }

    public void MarkApplyCommitted()
    {
        foreach (var state in _states.Values)
        {
            state.MarkApplyCommitted();
        }
    }

    public void RefreshCandidatesForApply()
    {
        _debounceTimer.Stop();
        RefreshCandidatesAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _debounceTimer.Stop();
    }

    private void ApplyExistingPlanLines(long orderId)
    {
        foreach (var planLine in _loadPlanLines(orderId) ?? Array.Empty<OrderReceiptPlanLine>())
        {
            if (planLine.OrderLineId <= 0 || string.IsNullOrWhiteSpace(planLine.ToHu))
            {
                continue;
            }

            var state = _states.Values.FirstOrDefault(candidate => candidate.Line.Id == planLine.OrderLineId);
            if (state == null)
            {
                continue;
            }

            state.MergeExistingReservation(planLine.ToHu!, planLine.QtyPlanned);
        }
    }

    private void ScheduleCandidatesRefresh()
    {
        if (!_isCustomerOrder || _isLoading)
        {
            return;
        }

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private Task RefreshCandidatesAsync()
    {
        if (!_isCustomerOrder || _isLoading)
        {
            return Task.CompletedTask;
        }

        var generation = ++_refreshGeneration;
        if (!_orderId.HasValue)
        {
            foreach (var state in _states.Values)
            {
                state.SetPreviewWithoutOrder();
            }

            return Task.CompletedTask;
        }

        var requestLines = _states.Values
            .Where(state => state.Line.ItemId > 0 && state.Line.QtyOrdered > QtyTolerance)
            .Select(state => new WpfHuReservationCandidatesLineRequest
            {
                ClientLineKey = state.ClientLineKey,
                OrderLineId = state.Line.Id > 0 ? state.Line.Id : null,
                ItemId = state.Line.ItemId,
                QtyOrdered = state.Line.QtyOrdered
            })
            .ToArray();

        if (requestLines.Length == 0)
        {
            foreach (var state in _states.Values)
            {
                state.ClearCandidates();
            }

            return Task.CompletedTask;
        }

        if (!_readApi.TryGetHuReservationCandidates(
                _orderId,
                requestLines,
                Array.Empty<string>(),
                out var result))
        {
            foreach (var state in _states.Values)
            {
                state.SetCandidatesLoadFailed();
            }

            return Task.CompletedTask;
        }

        if (generation != _refreshGeneration)
        {
            return Task.CompletedTask;
        }

        var resultByKey = result.Lines.ToDictionary(line => line.ClientLineKey, StringComparer.OrdinalIgnoreCase);
        foreach (var state in _states.Values)
        {
            if (resultByKey.TryGetValue(state.ClientLineKey, out var lineResult))
            {
                state.ApplyCandidates(lineResult);
            }
            else
            {
                state.ClearCandidates();
            }
        }

        return Task.CompletedTask;
    }

    private string? FindClientLineKey(OrderLineView? line)
    {
        if (line == null)
        {
            return null;
        }

        if (line.Id > 0)
        {
            return $"line-{line.Id}";
        }

        return _states.Values.FirstOrDefault(state => ReferenceEquals(state.Line, line))?.ClientLineKey;
    }

    private static string ResolveClientLineKey(OrderLineView? line, int index)
    {
        if (line == null)
        {
            return $"draft-missing-{index}";
        }

        return line.Id > 0 ? $"line-{line.Id}" : $"draft-{line.ItemId}-{index}";
    }
}

public sealed class CustomerOrderLineHuState : INotifyPropertyChanged
{
    private const double QtyTolerance = 0.000001;
    private readonly List<WpfHuReservationCandidateRow> _candidates = new();
    private readonly HashSet<string> _selectedHuCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _selectedQtyByHu = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WpfHuReservationCandidateRow> _candidateByHu = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExistingReservationSnapshot> _existingOnlyReservations = new(StringComparer.OrdinalIgnoreCase);

    private OrderLineView _line = new();
    private long? _orderId;
    private double _availableQty;
    private double _lineRemainingQty;
    private bool _manualSelectionTouched;
    private bool _candidatesLoadFailed;
    private bool _awaitingSaveForCandidates;

    public CustomerOrderLineHuState(string clientLineKey)
    {
        ClientLineKey = clientLineKey;
    }

    public string ClientLineKey { get; }

    public OrderLineView Line => _line;

    public IReadOnlyList<WpfHuReservationCandidateRow> Candidates => _candidates;

    public IReadOnlyList<WpfHuReservationCandidateRow> GetPickerCandidates()
    {
        var rows = _candidates.ToList();
        var known = new HashSet<string>(_candidateByHu.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var huCode in _selectedHuCodes)
        {
            if (known.Contains(huCode))
            {
                continue;
            }

            var source = _existingOnlyReservations.TryGetValue(huCode, out var snapshot)
                ? snapshot.Source
                : "CURRENT_RESERVATION";
            rows.Add(new WpfHuReservationCandidateRow
            {
                HuCode = huCode,
                Source = source,
                Qty = _selectedQtyByHu.TryGetValue(huCode, out var qty) ? qty : 0,
                ShipReady = string.Equals(source, "LEDGER_STOCK", StringComparison.OrdinalIgnoreCase),
                Note = "Выбранный HU"
            });
        }

        return rows;
    }

    public IReadOnlyCollection<string> SelectedHuCodes => _selectedHuCodes;

    public bool ManualSelectionTouched => _manualSelectionTouched;

    public bool ShouldSendOnApply =>
        _manualSelectionTouched
        || _selectedHuCodes.Count > 0
        || _existingOnlyReservations.Count > 0;

    public string AvailableHuDisplay => _awaitingSaveForCandidates
        ? "После сохранения"
        : _candidatesLoadFailed
            ? "—"
            : FormatQty(_availableQty);

    public string BoundHuDisplay => _selectedHuCodes.Count == 0
        ? "0"
        : $"{FormatQty(BoundQty)} ({_selectedHuCodes.Count} HU)";

    public IReadOnlyList<CustomerOrderLineHuDisplayRow> HuDisplayRows =>
        _selectedHuCodes
            .Select(huCode => new CustomerOrderLineHuDisplayRow(
                huCode,
                "склад",
                _selectedQtyByHu.TryGetValue(huCode, out var qty) ? qty : 0,
                IsBold: true,
                SortOrder: 1))
            .Concat(_line.ProductionHuDisplayEntries.Select(entry => new CustomerOrderLineHuDisplayRow(
                entry.HuCode,
                entry.Label,
                entry.Qty,
                IsBold: false,
                SortOrder: entry.SortOrder <= 0 ? 2 : entry.SortOrder)))
            .OrderBy(row => row.SortOrder)
            .ThenBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public string RemainingHuDisplay => _awaitingSaveForCandidates
        ? "—"
        : FormatQty(Math.Max(0, _lineRemainingQty - BoundQty));

    public string HuPickerLabel => CustomerOrderHuPickerRules.BuildHuPickerLabel(
        _orderId.HasValue,
        _line,
        BoundQty,
        _selectedHuCodes.Count,
        _awaitingSaveForCandidates,
        _candidatesLoadFailed);

    public string? HuPickerToolTip => CustomerOrderHuPickerRules.BuildHuPickerToolTip(
        _line,
        BoundQty,
        _awaitingSaveForCandidates,
        _candidatesLoadFailed,
        IsHuPickerEnabled);

    public double ManualBindingCapacity => _lineRemainingQty;

    public double ManualBindableRemaining =>
        CustomerOrderHuPickerRules.ComputeManualBindableRemaining(_line, BoundQty);

    public bool CandidatesLoadFailed => _candidatesLoadFailed;

    public bool IsHuPickerEnabled => CustomerOrderHuPickerRules.IsHuPickerEnabled(
        _orderId.HasValue,
        _line,
        BoundQty,
        _awaitingSaveForCandidates);

    public bool IsSelectionOverRemaining => BoundQty > _lineRemainingQty + QtyTolerance;

    public string HuCoverageTone
    {
        get
        {
            if (_lineRemainingQty <= QtyTolerance)
            {
                return "neutral";
            }

            return BoundQty + QtyTolerance >= _lineRemainingQty
                ? "covered"
                : "missing";
        }
    }

    public string HuCoverageToolTip
    {
        get
        {
            var itemName = string.IsNullOrWhiteSpace(_line.ItemName)
                ? "Товар без названия"
                : _line.ItemName.Trim();
            if (_lineRemainingQty <= QtyTolerance)
            {
                return $"{itemName}: остатка к отгрузке нет";
            }

            var boundQty = Math.Min(BoundQty, _lineRemainingQty);
            var missingQty = Math.Max(0, _lineRemainingQty - BoundQty);
            if (missingQty <= QtyTolerance)
            {
                return $"{itemName}: привязано {FormatQty(boundQty)} из {FormatQty(_lineRemainingQty)}";
            }

            return $"{itemName}: привязано {FormatQty(boundQty)} из {FormatQty(_lineRemainingQty)}, не хватает {FormatQty(missingQty)}";
        }
    }

    public double BoundQty => _selectedHuCodes.Sum(huCode =>
        _selectedQtyByHu.TryGetValue(huCode, out var qty) ? qty : 0);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void AttachLine(OrderLineView? line, long? orderId)
    {
        if (!ReferenceEquals(_line, line))
        {
            _line.PropertyChanged -= Line_PropertyChanged;
        }

        _line = line ?? new OrderLineView();
        _line.PropertyChanged -= Line_PropertyChanged;
        _line.PropertyChanged += Line_PropertyChanged;
        _orderId = orderId;
        _lineRemainingQty = CustomerOrderHuPickerRules.ComputeManualBindingCapacity(_line);
        _awaitingSaveForCandidates = !orderId.HasValue;
        RaiseAll();
    }

    public void MergeExistingReservation(string huCode, double qty)
    {
        var normalized = huCode.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        _existingOnlyReservations[normalized] = new ExistingReservationSnapshot(normalized, qty, "CURRENT_RESERVATION");
        if (!_manualSelectionTouched)
        {
            _selectedHuCodes.Add(normalized);
            _selectedQtyByHu[normalized] = qty;
        }

        RaiseAll();
    }

    public void ApplyManualSelection(IReadOnlyCollection<string> selectedHuCodes)
    {
        _manualSelectionTouched = true;
        _selectedHuCodes.Clear();
        _selectedQtyByHu.Clear();
        foreach (var huCode in selectedHuCodes)
        {
            var normalized = huCode.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(normalized) || !_selectedHuCodes.Add(normalized))
            {
                continue;
            }

            _selectedQtyByHu[normalized] = ResolveQtyForHu(normalized);
        }

        RaiseAll();
    }

    public void ApplyCandidates(WpfHuReservationCandidatesLineResult lineResult)
    {
        _candidatesLoadFailed = false;
        _awaitingSaveForCandidates = false;
        _availableQty = lineResult.AvailableQty;
        _candidates.Clear();
        _candidateByHu.Clear();
        foreach (var candidate in lineResult.Candidates)
        {
            _candidates.Add(candidate);
            _candidateByHu[candidate.HuCode] = candidate;
        }

        foreach (var existing in _existingOnlyReservations.Values)
        {
            if (_candidateByHu.ContainsKey(existing.HuCode))
            {
                continue;
            }

            _candidates.Add(new WpfHuReservationCandidateRow
            {
                HuCode = existing.HuCode,
                Source = existing.Source,
                Qty = existing.Qty,
                ShipReady = string.Equals(existing.Source, "LEDGER_STOCK", StringComparison.OrdinalIgnoreCase),
                Note = "Текущий резерв строки"
            });
            _candidateByHu[existing.HuCode] = _candidates[^1];
        }

        if (!_manualSelectionTouched)
        {
            if (_selectedHuCodes.Count == 0)
            {
                ApplyInitialAutoSelection(lineResult);
            }
            else
            {
                SyncSelectedQtyFromCandidates();
            }
        }
        else
        {
            PruneSelectionToKnownCandidates();
            SyncSelectedQtyFromCandidates();
        }

        RaiseAll();
    }

    public void SetPreviewWithoutOrder()
    {
        _awaitingSaveForCandidates = true;
        _candidatesLoadFailed = false;
        _availableQty = 0;
        _candidates.Clear();
        _candidateByHu.Clear();
        RaiseAll();
    }

    public void SetCandidatesLoadFailed()
    {
        _candidatesLoadFailed = true;
        _awaitingSaveForCandidates = false;
        RaiseAll();
    }

    public void ClearCandidates()
    {
        _availableQty = 0;
        _candidates.Clear();
        _candidateByHu.Clear();
        RaiseAll();
    }

    public void MarkApplyCommitted()
    {
        _existingOnlyReservations.Clear();
        foreach (var huCode in _selectedHuCodes)
        {
            _existingOnlyReservations[huCode] = new ExistingReservationSnapshot(
                huCode,
                _selectedQtyByHu.TryGetValue(huCode, out var qty) ? qty : 0,
                _candidateByHu.TryGetValue(huCode, out var candidate) ? candidate.Source : "CURRENT_RESERVATION");
        }
    }

    private void ApplyInitialAutoSelection(WpfHuReservationCandidatesLineResult lineResult)
    {
        _selectedHuCodes.Clear();
        _selectedQtyByHu.Clear();

        var remaining = _lineRemainingQty;
        foreach (var candidate in lineResult.Candidates.Where(candidate => candidate.AutoSelected))
        {
            var normalized = candidate.HuCode.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(normalized)
                || !_selectedHuCodes.Add(normalized)
                || candidate.Qty <= CustomerOrderHuPickerRules.QtyTolerance)
            {
                continue;
            }

            var allocated = Math.Min(remaining, candidate.Qty);
            if (allocated <= CustomerOrderHuPickerRules.QtyTolerance)
            {
                _selectedHuCodes.Remove(normalized);
                continue;
            }

            _selectedQtyByHu[normalized] = allocated;
            remaining -= allocated;
            if (remaining <= CustomerOrderHuPickerRules.QtyTolerance)
            {
                break;
            }
        }

        if (_selectedHuCodes.Count == 0)
        {
            foreach (var existing in _existingOnlyReservations.Values)
            {
                if (!_selectedHuCodes.Add(existing.HuCode))
                {
                    continue;
                }

                _selectedQtyByHu[existing.HuCode] = existing.Qty;
            }
        }
    }

    private void PruneSelectionToKnownCandidates()
    {
        foreach (var huCode in _selectedHuCodes.ToArray())
        {
            if (_candidateByHu.ContainsKey(huCode) || _existingOnlyReservations.ContainsKey(huCode))
            {
                continue;
            }

            _selectedHuCodes.Remove(huCode);
            _selectedQtyByHu.Remove(huCode);
        }
    }

    private void SyncSelectedQtyFromCandidates()
    {
        foreach (var huCode in _selectedHuCodes.ToArray())
        {
            _selectedQtyByHu[huCode] = ResolveQtyForHu(huCode);
        }
    }

    private double ResolveQtyForHu(string huCode)
    {
        if (_candidateByHu.TryGetValue(huCode, out var candidate))
        {
            return candidate.Qty;
        }

        if (_existingOnlyReservations.TryGetValue(huCode, out var existing))
        {
            return existing.Qty;
        }

        return 0;
    }

    private static string FormatQty(double qty) =>
        qty.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(AvailableHuDisplay));
        OnPropertyChanged(nameof(BoundHuDisplay));
        OnPropertyChanged(nameof(HuDisplayRows));
        OnPropertyChanged(nameof(RemainingHuDisplay));
        OnPropertyChanged(nameof(HuPickerLabel));
        OnPropertyChanged(nameof(HuPickerToolTip));
        OnPropertyChanged(nameof(ManualBindingCapacity));
        OnPropertyChanged(nameof(ManualBindableRemaining));
        OnPropertyChanged(nameof(IsHuPickerEnabled));
        OnPropertyChanged(nameof(IsSelectionOverRemaining));
        OnPropertyChanged(nameof(HuCoverageTone));
        OnPropertyChanged(nameof(HuCoverageToolTip));
    }

    private void Line_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _lineRemainingQty = CustomerOrderHuPickerRules.ComputeManualBindingCapacity(_line);
        RaiseAll();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed record ExistingReservationSnapshot(string HuCode, double Qty, string Source);
}

public sealed class CustomerOrderLinePresentation : INotifyPropertyChanged
{
    public CustomerOrderLinePresentation(CustomerOrderLineHuState state)
    {
        State = state;
        State.PropertyChanged += (_, _) => RaiseAll();
    }

    public CustomerOrderLineHuState State { get; }

    public string ClientLineKey => State.ClientLineKey;

    public OrderLineView Line => State.Line;

    public string ItemName => State.Line.ItemName;

    public string? Barcode => State.Line.Barcode;

    public string? Gtin => State.Line.Gtin;

    public double QtyOrdered => State.Line.QtyOrdered;

    public string ProductionHuCodes => State.Line.ProductionHuCodes;

    public double QtyShipped => State.Line.QtyShipped;

    public double QtyRemaining => State.Line.QtyRemaining;

    public double QtyAvailable => State.Line.QtyAvailable;

    public double CanShipNow => State.Line.CanShipNow;

    public double Shortage => State.Line.Shortage;

    public bool IsMixedPalletLine => State.Line.IsMixedPalletLine;

    public int MixedPalletGroupNumber
    {
        get => State.Line.MixedPalletGroupNumber;
        set => State.Line.MixedPalletGroupNumber = value;
    }

    public string AvailableHuDisplay => State.AvailableHuDisplay;

    public string BoundHuDisplay => State.BoundHuDisplay;

    public IReadOnlyList<CustomerOrderLineHuDisplayRow> HuDisplayRows => State.HuDisplayRows;

    public string RemainingHuDisplay => State.RemainingHuDisplay;

    public string HuPickerLabel => State.HuPickerLabel;

    public string? HuPickerToolTip => State.HuPickerToolTip;

    public bool IsHuPickerEnabled => State.IsHuPickerEnabled;

    public string HuCoverageTone => State.HuCoverageTone;

    public string HuCoverageToolTip => State.HuCoverageToolTip;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Line));
        OnPropertyChanged(nameof(ItemName));
        OnPropertyChanged(nameof(Barcode));
        OnPropertyChanged(nameof(Gtin));
        OnPropertyChanged(nameof(QtyOrdered));
        OnPropertyChanged(nameof(ProductionHuCodes));
        OnPropertyChanged(nameof(QtyShipped));
        OnPropertyChanged(nameof(QtyRemaining));
        OnPropertyChanged(nameof(QtyAvailable));
        OnPropertyChanged(nameof(CanShipNow));
        OnPropertyChanged(nameof(Shortage));
        OnPropertyChanged(nameof(IsMixedPalletLine));
        OnPropertyChanged(nameof(MixedPalletGroupNumber));
        OnPropertyChanged(nameof(AvailableHuDisplay));
        OnPropertyChanged(nameof(BoundHuDisplay));
        OnPropertyChanged(nameof(HuDisplayRows));
        OnPropertyChanged(nameof(RemainingHuDisplay));
        OnPropertyChanged(nameof(HuPickerLabel));
        OnPropertyChanged(nameof(HuPickerToolTip));
        OnPropertyChanged(nameof(IsHuPickerEnabled));
        OnPropertyChanged(nameof(HuCoverageTone));
        OnPropertyChanged(nameof(HuCoverageToolTip));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record CustomerOrderLineHuDisplayRow(
    string HuCode,
    string Label,
    double Qty,
    bool IsBold,
    int SortOrder)
{
    public string DisplayText => $"{HuCode} · {Label} · {Qty:0.###}";
}

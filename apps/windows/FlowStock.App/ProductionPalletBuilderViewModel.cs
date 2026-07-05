using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlowStock.App;

/// <summary>Outcome of a "reload preview from server" request.</summary>
public enum PalletBuilderReloadOutcome
{
    /// <summary>The user declined the unsaved-changes confirmation; no API call was made.</summary>
    Cancelled,

    /// <summary>The server preview was reloaded and the editable area replaced.</summary>
    Reloaded,

    /// <summary>The API call failed; the editable area is unchanged and an error is shown.</summary>
    Failed
}

/// <summary>
/// ViewModel of the pallet constructor. The editable area is the suggested delta only;
/// saved pallets (open plan + FILLED history) are read-only. Local validation mirrors
/// the server rules (exact allocation, equal-cap, over-capacity) purely as UX guard —
/// the server confirm remains the final authority.
///
/// The ViewModel is long-lived: it owns the "reload preview" and confirmation seams via
/// injected delegates so the reload / auto-split-reset / close-with-unsaved-changes flows
/// are testable without a live Window.
/// </summary>
public sealed class ProductionPalletBuilderViewModel : INotifyPropertyChanged
{
    private const double QtyTolerance = 0.000001d;

    public const string DirtyStatusText = "Есть несохранённые изменения";
    public const string ResetConfirmMessage = "Ручные изменения будут потеряны. Вернуть автоматическое распределение?";
    public const string ReloadConfirmMessage = "Несохранённая раскладка будет потеряна. Обновить данные с сервера?";
    public const string CloseConfirmMessage = "Есть несохранённые изменения. Закрыть без сохранения?";
    public const string OpenPlanEmptyText =
        "Сохранённых паллет пока нет. После сохранения они появятся здесь при повторном открытии конструктора.";
    public const string HistoryEmptyText = "Наполненных HU пока нет.";

    private readonly Func<CancellationToken, Task<WpfPalletPlanPreviewApiResult>>? _previewLoader;
    private readonly Func<string, bool>? _confirm;

    private WpfPalletPlanPreview _preview;
    private Dictionary<long, WpfPalletPlanLine> _linesById;
    private IReadOnlyList<string> _validationErrors = Array.Empty<string>();
    private IReadOnlySet<long> _highlightedOrderLineIds = new HashSet<long>();
    private string? _serverErrorMessage;
    private bool _needsPreviewRefresh;
    private bool _isDirty;
    private bool _isBusy;

    public event PropertyChangedEventHandler? PropertyChanged;

    private ProductionPalletBuilderViewModel(
        WpfPalletPlanPreview preview,
        Func<CancellationToken, Task<WpfPalletPlanPreviewApiResult>>? previewLoader,
        Func<string, bool>? confirm)
    {
        _preview = preview;
        _previewLoader = previewLoader;
        _confirm = confirm;
        _linesById = preview.Lines.ToDictionary(line => line.OrderLineId, line => line);
        OpenPlanPallets = new ObservableCollection<WpfSavedPallet>(preview.OpenPlanPallets);
        HistoricalPallets = new ObservableCollection<WpfSavedPallet>(preview.HistoricalPallets);
        SuggestedPallets = new ObservableCollection<BuilderPalletViewModel>();
        ResetToServerSuggestion();
    }

    public static ProductionPalletBuilderViewModel FromPreview(WpfPalletPlanPreview preview)
    {
        return new ProductionPalletBuilderViewModel(preview, previewLoader: null, confirm: null);
    }

    /// <summary>Long-lived overload used by the Window: reload + confirmation seams are injected.</summary>
    public static ProductionPalletBuilderViewModel FromPreview(
        WpfPalletPlanPreview preview,
        Func<CancellationToken, Task<WpfPalletPlanPreviewApiResult>> previewLoader,
        Func<string, bool> confirm)
    {
        return new ProductionPalletBuilderViewModel(preview, previewLoader, confirm);
    }

    public long OrderId => _preview.OrderId;
    public string OrderRef => _preview.OrderRef;
    public string HeaderText => $"Заказ {OrderRef}: план паллет";
    public string PreviewFingerprint => _preview.PreviewFingerprint;
    public bool ProductionRequired => _preview.ProductionRequired;
    public IReadOnlyList<WpfPalletPlanLine> Lines => _preview.Lines;
    public ObservableCollection<WpfSavedPallet> OpenPlanPallets { get; }
    public ObservableCollection<WpfSavedPallet> HistoricalPallets { get; }
    public ObservableCollection<BuilderPalletViewModel> SuggestedPallets { get; }

    public IReadOnlyList<string> ValidationErrors => _validationErrors;
    public IReadOnlySet<long> HighlightedOrderLineIds => _highlightedOrderLineIds;
    public string? ServerErrorMessage => _serverErrorMessage;

    /// <summary>True when the last server error requires reloading the preview (stale/no-need).</summary>
    public bool NeedsPreviewRefresh => _needsPreviewRefresh;

    /// <summary>
    /// After a refresh-requiring server error the "reload from server" action becomes the
    /// primary next step (used by the Window to emphasise that button).
    /// </summary>
    public bool RefreshIsPrimaryAction => _needsPreviewRefresh;

    /// <summary>True when the editable delta has unsaved manual changes since the last load/reset/reload.</summary>
    public bool IsDirty => _isDirty;

    /// <summary>
    /// True while an async operation (initial load, reload, save) is in flight. The editable area
    /// and closing are blocked so the layout cannot change after a save-request is built or while a
    /// fresh preview is awaited.
    /// </summary>
    public bool IsBusy => _isBusy;

    /// <summary>Toggles the busy gate around async operations (set by the Window).</summary>
    public void SetBusy(bool busy)
    {
        if (_isBusy == busy)
        {
            return;
        }

        _isBusy = busy;
        OnPropertyChanged(nameof(IsBusy));
    }

    /// <summary>True while "Вернуть авторазбиение" is meaningful (there are manual changes to discard).</summary>
    public bool CanResetToServerSuggestion => _isDirty;

    public bool HasOpenPlanPallets => OpenPlanPallets.Count > 0;
    public bool HasHistoricalPallets => HistoricalPallets.Count > 0;
    public string OpenPlanTabHeader => $"Текущий план ({OpenPlanPallets.Count})";
    public string HistoryTabHeader => $"История ({HistoricalPallets.Count})";

    /// <summary>
    /// Save is available only for a non-empty, locally valid suggested delta. After a
    /// stale / no-production-required / not-plannable server response saving stays blocked
    /// until the preview is successfully reloaded.
    /// </summary>
    public bool CanSave => ProductionRequired
                           && !_needsPreviewRefresh
                           && SuggestedPallets.Count > 0
                           && _validationErrors.Count == 0;

    /// <summary>Resets the editable area back to the server auto-split proposal (clears dirty).</summary>
    public void ResetToServerSuggestion()
    {
        SuggestedPallets.Clear();
        foreach (var pallet in _preview.SuggestedPallets)
        {
            var palletVm = new BuilderPalletViewModel(this, pallet.TempNo);
            foreach (var component in pallet.Components)
            {
                palletVm.AddOrSetComponent(component.OrderLineId, component.Qty);
            }

            SuggestedPallets.Add(palletVm);
        }

        RenumberAndRevalidate();
        SetDirty(false);
    }

    /// <summary>
    /// Gated "Вернуть авторазбиение": with unsaved changes it asks for confirmation first.
    /// Never saves and never loads a fresh preview — it only restores the current preview's
    /// server suggestion. Returns false if the user declined.
    /// </summary>
    public bool RequestResetToServerSuggestion()
    {
        if (_isBusy)
        {
            return false;
        }

        if (_isDirty && _confirm != null && !_confirm(ResetConfirmMessage))
        {
            return false;
        }

        ResetToServerSuggestion();
        return true;
    }

    public BuilderPalletViewModel AddPallet()
    {
        var pallet = new BuilderPalletViewModel(this, SuggestedPallets.Count + 1);
        SuggestedPallets.Add(pallet);
        RenumberAndRevalidate();
        SetDirty(true);
        return pallet;
    }

    public void RemovePallet(BuilderPalletViewModel pallet)
    {
        if (_isBusy)
        {
            return;
        }

        SuggestedPallets.Remove(pallet);
        RenumberAndRevalidate();
        SetDirty(true);
    }

    /// <summary>Moves quantity of one order line between two temp pallets.</summary>
    public bool TryMoveQty(
        BuilderPalletViewModel from,
        BuilderPalletViewModel to,
        long orderLineId,
        double qty,
        out string? error)
    {
        if (_isBusy)
        {
            error = "Операция выполняется, дождитесь завершения.";
            return false;
        }

        var source = from.FindComponent(orderLineId);
        if (source == null || qty <= QtyTolerance || qty > source.Qty + QtyTolerance)
        {
            error = "Недопустимое количество для переноса.";
            return false;
        }

        from.AddOrSetComponent(orderLineId, source.Qty - qty);
        var target = to.FindComponent(orderLineId);
        to.AddOrSetComponent(orderLineId, (target?.Qty ?? 0) + qty);
        RenumberAndRevalidate();
        SetDirty(true);
        error = null;
        return true;
    }

    /// <summary>Unallocated remainder of a line's shortfall across the current suggested delta.</summary>
    public double GetUnallocatedQty(long orderLineId)
    {
        var required = _linesById.TryGetValue(orderLineId, out var line) ? line.ShortfallQty : 0;
        var allocated = SuggestedPallets
            .SelectMany(pallet => pallet.Components)
            .Where(component => component.OrderLineId == orderLineId)
            .Sum(component => component.Qty);
        return required - allocated;
    }

    /// <summary>Order lines that still have an unallocated shortfall remainder, for the per-pallet selector.</summary>
    public IReadOnlyList<BuilderRemainderOption> GetAvailableRemainderOptions()
    {
        var options = new List<BuilderRemainderOption>();
        foreach (var line in _preview.Lines)
        {
            var remainder = GetUnallocatedQty(line.OrderLineId);
            if (remainder > QtyTolerance)
            {
                options.Add(new BuilderRemainderOption(line.OrderLineId, line.ItemName, remainder));
            }
        }

        return options;
    }

    /// <summary>Adds the line's unallocated shortfall remainder onto the target pallet.</summary>
    public bool TryAddRemainder(BuilderPalletViewModel target, long orderLineId, out string? error)
    {
        if (_isBusy)
        {
            error = "Операция выполняется, дождитесь завершения.";
            return false;
        }

        var remainder = GetUnallocatedQty(orderLineId);
        if (remainder <= QtyTolerance)
        {
            error = "По строке нет нераспределённого остатка.";
            return false;
        }

        var existing = target.FindComponent(orderLineId);
        target.AddOrSetComponent(orderLineId, (existing?.Qty ?? 0) + remainder);
        RenumberAndRevalidate();
        SetDirty(true);
        error = null;
        return true;
    }

    public IReadOnlyList<WpfExplicitPlanPallet> BuildConfirmRequestPallets()
    {
        return SuggestedPallets
            .Where(pallet => pallet.Components.Count > 0)
            .Select(pallet => new WpfExplicitPlanPallet(
                pallet.Components
                    .Select(component => new WpfExplicitPlanComponent(component.OrderLineId, component.Qty))
                    .ToArray()))
            .ToArray();
    }

    /// <summary>
    /// Reloads the plan preview from the server, replacing the editable layout. With unsaved
    /// changes the injected confirmation is asked first; declining makes no API call and leaves
    /// the layout untouched. A successful reload clears stale/error/dirty state.
    /// </summary>
    public async Task<PalletBuilderReloadOutcome> ReloadFromServerAsync(CancellationToken cancellationToken = default)
    {
        if (_previewLoader == null)
        {
            return PalletBuilderReloadOutcome.Failed;
        }

        if (_isDirty && _confirm != null && !_confirm(ReloadConfirmMessage))
        {
            return PalletBuilderReloadOutcome.Cancelled;
        }

        SetBusy(true);
        try
        {
            var result = await _previewLoader(cancellationToken).ConfigureAwait(true);
            if (!result.IsSuccess || result.Preview == null)
            {
                _serverErrorMessage = string.IsNullOrWhiteSpace(result.Message)
                    ? "Не удалось обновить план паллет."
                    : result.Message;
                OnPropertyChanged(nameof(ServerErrorMessage));
                return PalletBuilderReloadOutcome.Failed;
            }

            ApplyPreview(result.Preview);
            return PalletBuilderReloadOutcome.Reloaded;
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Whether the window may close now. With unsaved changes the injected confirmation decides;
    /// otherwise closing is always allowed.
    /// </summary>
    public bool RequestClose()
    {
        if (_isBusy)
        {
            return false;
        }

        if (!_isDirty)
        {
            return true;
        }

        return _confirm == null || _confirm(CloseConfirmMessage);
    }

    /// <summary>Marks the delta as saved (no unsaved changes) after a successful confirm.</summary>
    public void MarkSaved()
    {
        SetDirty(false);
    }

    private void ApplyPreview(WpfPalletPlanPreview preview)
    {
        _preview = preview;
        _linesById = preview.Lines.ToDictionary(line => line.OrderLineId, line => line);

        OpenPlanPallets.Clear();
        foreach (var pallet in preview.OpenPlanPallets)
        {
            OpenPlanPallets.Add(pallet);
        }

        HistoricalPallets.Clear();
        foreach (var pallet in preview.HistoricalPallets)
        {
            HistoricalPallets.Add(pallet);
        }

        _serverErrorMessage = null;
        _needsPreviewRefresh = false;
        _highlightedOrderLineIds = new HashSet<long>();

        ResetToServerSuggestion();

        OnPropertyChanged(nameof(OrderRef));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(PreviewFingerprint));
        OnPropertyChanged(nameof(ProductionRequired));
        OnPropertyChanged(nameof(Lines));
        OnPropertyChanged(nameof(ServerErrorMessage));
        OnPropertyChanged(nameof(NeedsPreviewRefresh));
        OnPropertyChanged(nameof(RefreshIsPrimaryAction));
        OnPropertyChanged(nameof(HighlightedOrderLineIds));
        OnPropertyChanged(nameof(HasOpenPlanPallets));
        OnPropertyChanged(nameof(HasHistoricalPallets));
        OnPropertyChanged(nameof(OpenPlanTabHeader));
        OnPropertyChanged(nameof(HistoryTabHeader));
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>
    /// Applies a structured server error: sets the display message, marks lines to highlight
    /// for allocation mismatches and flags stale/no-need responses for a preview reload.
    /// </summary>
    public void ApplyServerError(WpfPalletPlanServerError error)
    {
        _needsPreviewRefresh = error.ErrorCode
            is "PLAN_PREVIEW_STALE"
            or "NO_PRODUCTION_REQUIRED"
            or "ORDER_NOT_PLANNABLE"
            or "ORDER_LINE_NOT_FOUND"
            or "ORDER_LINE_CANCELLED";
        _highlightedOrderLineIds = error.AllocationLines
            .Select(line => line.OrderLineId)
            .ToHashSet();
        _serverErrorMessage = error.ErrorCode switch
        {
            "PLAN_PREVIEW_STALE" => "Данные заказа изменились. Обновите план и повторите сохранение.",
            "NO_PRODUCTION_REQUIRED" => "Производственная нехватка отсутствует. План не требуется.",
            "ORDER_LINE_NOT_FOUND" => "Строки заказа изменились. Обновите план паллет.",
            "ORDER_LINE_CANCELLED" => "Строка заказа отменена. Обновите план паллет.",
            "LINE_ALLOCATION_MISMATCH" => BuildAllocationMismatchMessage(error),
            "PALLET_OVER_CAPACITY" => error.Message,
            "PALLET_CAPACITY_MISMATCH" => error.Message,
            "ORDER_NOT_PLANNABLE" => "Заказ недоступен для планирования паллет.",
            _ => error.Message
        };
        OnPropertyChanged(nameof(ServerErrorMessage));
        OnPropertyChanged(nameof(NeedsPreviewRefresh));
        OnPropertyChanged(nameof(RefreshIsPrimaryAction));
        OnPropertyChanged(nameof(HighlightedOrderLineIds));
        OnPropertyChanged(nameof(CanSave));
    }

    private string BuildAllocationMismatchMessage(WpfPalletPlanServerError error)
    {
        if (error.AllocationLines.Count == 0)
        {
            return error.Message;
        }

        var details = error.AllocationLines
            .Select(line =>
            {
                var name = _linesById.TryGetValue(line.OrderLineId, out var known)
                    ? known.ItemName
                    : $"строка {line.OrderLineId}";
                return $"{name}: нужно {line.RequiredQty:0.###}, распределено {line.AllocatedQty:0.###}";
            });
        return $"{error.Message} {string.Join("; ", details)}.";
    }

    internal void RenumberAndRevalidate()
    {
        for (var index = 0; index < SuggestedPallets.Count; index++)
        {
            SuggestedPallets[index].TempNo = index + 1;
        }

        var errors = new List<string>();
        foreach (var pallet in SuggestedPallets)
        {
            pallet.Revalidate(_linesById, errors);
        }

        foreach (var line in _preview.Lines)
        {
            var unallocated = GetUnallocatedQty(line.OrderLineId);
            if (Math.Abs(unallocated) > QtyTolerance)
            {
                errors.Add(unallocated > 0
                    ? $"{line.ItemName}: не распределено {unallocated:0.###} из нехватки {line.ShortfallQty:0.###}."
                    : $"{line.ItemName}: распределено больше нехватки на {-unallocated:0.###}.");
            }
        }

        _validationErrors = errors;

        var options = GetAvailableRemainderOptions();
        foreach (var pallet in SuggestedPallets)
        {
            pallet.UpdateAvailableRemainders(options);
        }

        OnPropertyChanged(nameof(ValidationErrors));
        OnPropertyChanged(nameof(CanSave));
    }

    internal WpfPalletPlanLine? FindLine(long orderLineId)
    {
        return _linesById.TryGetValue(orderLineId, out var line) ? line : null;
    }

    internal void MarkDirty()
    {
        SetDirty(true);
    }

    private void SetDirty(bool value)
    {
        if (_isDirty == value)
        {
            return;
        }

        _isDirty = value;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanResetToServerSuggestion));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>One order line still carrying an unallocated shortfall remainder, for a pallet's selector.</summary>
public sealed record BuilderRemainderOption(long OrderLineId, string ItemName, double RemainderQty)
{
    public string DisplayText => $"{ItemName} — осталось {RemainderQty:0.###}";
}

/// <summary>One editable temp pallet of the suggested delta.</summary>
public sealed class BuilderPalletViewModel : INotifyPropertyChanged
{
    private const double QtyTolerance = 0.000001d;

    private readonly ProductionPalletBuilderViewModel _owner;
    private int _tempNo;
    private double? _capacityQty;
    private bool _isOverCapacity;
    private bool _hasCapacityMismatch;
    private BuilderRemainderOption? _selectedRemainder;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal BuilderPalletViewModel(ProductionPalletBuilderViewModel owner, int tempNo)
    {
        _owner = owner;
        _tempNo = tempNo;
        Components = new ObservableCollection<BuilderComponentViewModel>();
        AvailableRemainders = new ObservableCollection<BuilderRemainderOption>();
    }

    public ObservableCollection<BuilderComponentViewModel> Components { get; }

    /// <summary>Order lines with an unallocated remainder that can be added onto this pallet.</summary>
    public ObservableCollection<BuilderRemainderOption> AvailableRemainders { get; }

    /// <summary>The remainder option chosen in this pallet's selector (bound two-way from the ComboBox).</summary>
    public BuilderRemainderOption? SelectedRemainder
    {
        get => _selectedRemainder;
        set
        {
            if (ReferenceEquals(_selectedRemainder, value))
            {
                return;
            }

            _selectedRemainder = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanAddSelectedRemainder));
        }
    }

    public bool HasRemainders => AvailableRemainders.Count > 0;

    /// <summary>Add is enabled only when a specific line is chosen — never auto-picks the first line.</summary>
    public bool CanAddSelectedRemainder => _selectedRemainder != null;

    /// <summary>Shown in place of the selector when nothing remains to allocate.</summary>
    public string RemainderEmptyText => "Все позиции распределены";

    public int TempNo
    {
        get => _tempNo;
        internal set
        {
            if (_tempNo == value)
            {
                return;
            }

            _tempNo = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
        }
    }

    public bool IsMixed => Components.Count > 1;
    public double TotalQty => Components.Sum(component => component.Qty);
    public double? CapacityQty => _capacityQty;
    public bool IsOverCapacity => _isOverCapacity;
    public bool HasCapacityMismatch => _hasCapacityMismatch;

    public string Title => _capacityQty is { } capacity
        ? $"Паллета {TempNo} — {TotalQty:0.###} / {capacity:0.###}{(IsMixed ? " — МИКС" : string.Empty)}"
        : $"Паллета {TempNo} — {TotalQty:0.###}{(IsMixed ? " — МИКС" : string.Empty)}";

    public BuilderComponentViewModel? FindComponent(long orderLineId)
    {
        return Components.FirstOrDefault(component => component.OrderLineId == orderLineId);
    }

    /// <summary>
    /// Sets the component qty; zero (or below tolerance) removes the component. A no-op change
    /// (same value within tolerance) neither rebuilds the model nor marks the delta dirty.
    /// </summary>
    public void SetComponentQty(long orderLineId, double qty)
    {
        if (_owner.IsBusy)
        {
            return;
        }

        var current = FindComponent(orderLineId)?.Qty ?? 0;
        if (Math.Abs(current - qty) <= QtyTolerance)
        {
            return;
        }

        AddOrSetComponent(orderLineId, qty);
        _owner.RenumberAndRevalidate();
        _owner.MarkDirty();
    }

    /// <summary>
    /// Parses input text (comma or dot decimal, non-negative) and applies it to the component via
    /// the same path as an inline edit. Returns false for invalid text so the caller can restore the
    /// displayed value. Re-applying the same value is a no-op (safe against a following LostFocus).
    /// </summary>
    public bool TryApplyComponentQtyText(long orderLineId, string? text)
    {
        if (!TryParseComponentQty(text, out var qty))
        {
            return false;
        }

        SetComponentQty(orderLineId, qty);
        return true;
    }

    /// <summary>Shared qty-text parser used by both inline edits and the pending-edit commit.</summary>
    public static bool TryParseComponentQty(string? text, out double qty)
    {
        var normalized = (text ?? string.Empty).Trim().Replace(',', '.');
        return double.TryParse(
            normalized,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out qty) && qty >= 0;
    }

    public void RemoveComponent(long orderLineId)
    {
        SetComponentQty(orderLineId, 0);
    }

    /// <summary>Selects the remainder option for the given order line, if it is currently available.</summary>
    public bool TrySelectRemainder(long orderLineId)
    {
        var option = AvailableRemainders.FirstOrDefault(remainder => remainder.OrderLineId == orderLineId);
        if (option == null)
        {
            return false;
        }

        SelectedRemainder = option;
        return true;
    }

    /// <summary>
    /// Adds the explicitly selected remainder line onto this pallet. Requires a selection —
    /// there is no silent "first undistributed line" fallback.
    /// </summary>
    public bool AddSelectedRemainder(out string? error)
    {
        if (_selectedRemainder is not { } option)
        {
            error = "Выберите позицию для добавления.";
            return false;
        }

        return _owner.TryAddRemainder(this, option.OrderLineId, out error);
    }

    internal void UpdateAvailableRemainders(IReadOnlyList<BuilderRemainderOption> options)
    {
        var previousId = _selectedRemainder?.OrderLineId;

        AvailableRemainders.Clear();
        foreach (var option in options)
        {
            AvailableRemainders.Add(option);
        }

        _selectedRemainder = previousId is { } id
            ? AvailableRemainders.FirstOrDefault(option => option.OrderLineId == id)
            : null;

        OnPropertyChanged(nameof(HasRemainders));
        OnPropertyChanged(nameof(RemainderEmptyText));
        OnPropertyChanged(nameof(SelectedRemainder));
        OnPropertyChanged(nameof(CanAddSelectedRemainder));
    }

    internal void AddOrSetComponent(long orderLineId, double qty)
    {
        var existing = FindComponent(orderLineId);
        if (qty <= QtyTolerance)
        {
            if (existing != null)
            {
                Components.Remove(existing);
            }

            return;
        }

        if (existing != null)
        {
            existing.Qty = qty;
            return;
        }

        var line = _owner.FindLine(orderLineId);
        Components.Add(new BuilderComponentViewModel(
            orderLineId,
            line?.ItemId ?? 0,
            line?.ItemName ?? $"строка {orderLineId}",
            qty));
    }

    internal void Revalidate(IReadOnlyDictionary<long, WpfPalletPlanLine> linesById, List<string> errors)
    {
        var caps = Components
            .Select(component => linesById.TryGetValue(component.OrderLineId, out var line) ? line.MaxQtyPerHu : null)
            .ToArray();
        var capsValid = caps.Length > 0 && caps.All(cap => cap is > QtyTolerance);
        _hasCapacityMismatch = capsValid && caps.Select(cap => cap!.Value).Distinct().Count() > 1;
        _capacityQty = capsValid && !_hasCapacityMismatch ? caps[0] : null;
        _isOverCapacity = _capacityQty is { } capacity && TotalQty > capacity + QtyTolerance;

        if (Components.Count == 0)
        {
            errors.Add($"Паллета {TempNo} пуста.");
        }

        if (!capsValid && Components.Count > 0)
        {
            errors.Add($"Паллета {TempNo}: не задана вместимость (max_qty_per_hu) для товара.");
        }

        if (_hasCapacityMismatch)
        {
            errors.Add($"Паллета {TempNo}: разная вместимость (max_qty_per_hu) у товаров mixed-паллеты.");
        }

        if (_isOverCapacity)
        {
            errors.Add($"Паллета {TempNo}: сумма {TotalQty:0.###} превышает вместимость {_capacityQty:0.###}.");
        }

        OnPropertyChanged(nameof(IsMixed));
        OnPropertyChanged(nameof(TotalQty));
        OnPropertyChanged(nameof(CapacityQty));
        OnPropertyChanged(nameof(IsOverCapacity));
        OnPropertyChanged(nameof(HasCapacityMismatch));
        OnPropertyChanged(nameof(Title));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>One component (order line share) of an editable temp pallet.</summary>
public sealed class BuilderComponentViewModel : INotifyPropertyChanged
{
    private double _qty;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal BuilderComponentViewModel(long orderLineId, long itemId, string itemName, double qty)
    {
        OrderLineId = orderLineId;
        ItemId = itemId;
        ItemName = itemName;
        _qty = qty;
    }

    public long OrderLineId { get; }
    public long ItemId { get; }
    public string ItemName { get; }

    public double Qty
    {
        get => _qty;
        internal set
        {
            if (Math.Abs(_qty - value) < 0.000001d)
            {
                return;
            }

            _qty = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Qty)));
        }
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlowStock.App;

public interface IHuAssignmentManagementApiClient
{
    bool TryGetManageItems(string? search, int limit, out IReadOnlyList<WpfHuBindingManageItemRow> items);
    bool TryGetManageHus(long itemId, WpfHuBindingManageHuFilter filter, out WpfHuBindingManageHuPage page);
    bool TryGetManageTargets(long itemId, out IReadOnlyList<WpfHuBindingManageTargetLine> targets);
    bool TryApplyManageHuBindings(
        WpfHuBindingManageApplyRequest request,
        out WpfHuBindingManageApplyResult? result,
        out WpfHuBindingManageApplyError? error);
}

public sealed class WpfHuAssignmentManagementApiClient : IHuAssignmentManagementApiClient
{
    private readonly WpfReadApiService _readApi;

    public WpfHuAssignmentManagementApiClient(WpfReadApiService readApi)
    {
        _readApi = readApi;
    }

    public bool TryGetManageItems(string? search, int limit, out IReadOnlyList<WpfHuBindingManageItemRow> items) =>
        _readApi.TryGetManageItems(search, limit, out items);

    public bool TryGetManageHus(long itemId, WpfHuBindingManageHuFilter filter, out WpfHuBindingManageHuPage page) =>
        _readApi.TryGetManageHus(itemId, filter, out page);

    public bool TryGetManageTargets(long itemId, out IReadOnlyList<WpfHuBindingManageTargetLine> targets) =>
        _readApi.TryGetManageTargets(itemId, out targets);

    public bool TryApplyManageHuBindings(
        WpfHuBindingManageApplyRequest request,
        out WpfHuBindingManageApplyResult? result,
        out WpfHuBindingManageApplyError? error) =>
        _readApi.TryApplyManageHuBindings(request, out result, out error);
}

public sealed class HuAssignmentManagementController : INotifyPropertyChanged
{
    private const double QtyTolerance = 0.000001d;
    private static readonly HashSet<string> StaleErrorCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "HU_BINDING_STALE",
        "HU_OWNER_CHANGED",
        "HU_QTY_CHANGED",
        "HU_NOT_AVAILABLE",
        "HU_RESERVED_BY_OTHER_ORDER",
        "HU_BINDING_PLAN_CONFLICT",
        "HU_QTY_EXCEEDS_REMAINING"
    };

    private readonly IHuAssignmentManagementApiClient _api;
    private readonly Action<Exception>? _logException;
    private WpfHuBindingManageHuPage? _currentPage;
    private WpfHuBindingManageItemRow? _selectedItem;
    private HuAssignmentManagementHuItem? _selectedHu;
    private HuAssignmentManagementTargetLineItem? _selectedTargetLine;
    private bool _isBusy;
    private string _statusMessage = "Выберите товар.";
    private string? _itemSearch;
    private string? _huSearch;
    private string? _orderSearch;
    private string? _partnerSearch;
    private string _stateFilter = "ALL";
    private int _offset;

    public HuAssignmentManagementController(
        IHuAssignmentManagementApiClient api,
        Action<Exception>? logException = null)
    {
        _api = api;
        _logException = logException;
    }

    public ObservableCollection<WpfHuBindingManageItemRow> Items { get; } = new();
    public HuAssignmentManagementSession? Session { get; private set; }
    public WpfHuBindingManageItemRow? SelectedItem
    {
        get => _selectedItem;
        private set
        {
            _selectedItem = value;
            OnPropertyChanged();
        }
    }

    public HuAssignmentManagementHuItem? SelectedHu
    {
        get => _selectedHu;
        private set
        {
            _selectedHu = value;
            NotifyCommandState();
        }
    }

    public HuAssignmentManagementTargetLineItem? SelectedTargetLine
    {
        get => _selectedTargetLine;
        private set
        {
            _selectedTargetLine = value;
            NotifyCommandState();
        }
    }

    public string? ItemSearch
    {
        get => _itemSearch;
        set
        {
            _itemSearch = value;
            OnPropertyChanged();
        }
    }

    public string? HuSearch
    {
        get => _huSearch;
        set
        {
            _huSearch = value;
            OnPropertyChanged();
        }
    }

    public string? OrderSearch
    {
        get => _orderSearch;
        set
        {
            _orderSearch = value;
            OnPropertyChanged();
        }
    }

    public string? PartnerSearch
    {
        get => _partnerSearch;
        set
        {
            _partnerSearch = value;
            OnPropertyChanged();
        }
    }

    public string StateFilter
    {
        get => _stateFilter;
        set
        {
            _stateFilter = string.IsNullOrWhiteSpace(value) ? "ALL" : value.Trim().ToUpperInvariant();
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged();
            NotifyCommandState();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public int PageSize => 100;
    public int Total => _currentPage?.Total ?? 0;
    public int PageStart => Total == 0 ? 0 : _offset + 1;
    public int PageEnd => _currentPage == null ? 0 : Math.Min(Total, _offset + _currentPage.HuRows.Count);
    public string PageStatus => Total == 0 ? "Показано 0 из 0" : $"Показано {PageStart}-{PageEnd} из {Total}";
    public bool HasStagedChanges => Session?.HasStagedChanges == true;
    public bool CanPreviousPage => !IsBusy && _offset > 0;
    public bool CanNextPage => !IsBusy && _currentPage != null && _offset + _currentPage.HuRows.Count < Total;
    public bool CanSave => !IsBusy && HasStagedChanges;
    public bool CanBind => !IsBusy && CanAssignSelected(requireExistingFutureAssignment: false, requireDifferentTarget: false);
    public bool CanMove => !IsBusy && CanAssignSelected(requireExistingFutureAssignment: true, requireDifferentTarget: true);
    public bool CanDetach => !IsBusy && SelectedHu?.FutureOrderLineId.HasValue == true;
    public bool CanCancelSelectedChange => !IsBusy && SelectedHu?.IsChanged == true;
    public bool CanResetAllChanges => !IsBusy && HasStagedChanges;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool LoadInitial(out string message)
    {
        message = string.Empty;
        if (!LoadItems(out message))
        {
            return false;
        }

        if (Items.Count == 0)
        {
            StatusMessage = "Нет товаров с положительным HU-остатком.";
            return true;
        }

        return SelectItem(Items[0], discardStagedChanges: true, out message);
    }

    public bool LoadItems(out string message)
    {
        message = string.Empty;
        var localMessage = string.Empty;
        var ok = RunGuarded(() =>
        {
            if (!_api.TryGetManageItems(ItemSearch, PageSize, out var items))
            {
                StatusMessage = "Не удалось загрузить товары для управления HU.";
                localMessage = StatusMessage;
                return false;
            }

            Items.Clear();
            foreach (var item in items.OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase))
            {
                Items.Add(item);
            }

            StatusMessage = Items.Count == 0
                ? "Нет товаров с положительным HU-остатком."
                : $"Загружено товаров: {Items.Count}.";
            OnPropertyChanged(nameof(Items));
            return true;
        }, ref localMessage);
        message = localMessage;
        return ok;
    }

    public bool SelectItem(WpfHuBindingManageItemRow? item, bool discardStagedChanges, out string message)
    {
        message = string.Empty;
        if (item == null)
        {
            message = "Выберите товар.";
            StatusMessage = message;
            return false;
        }

        if (HasStagedChanges && !discardStagedChanges)
        {
            message = "Есть несохранённые изменения. Смена товара отменит их.";
            StatusMessage = message;
            return false;
        }

        SelectedItem = item;
        _offset = 0;
        return LoadCurrentData(discardStagedChanges: true, out message);
    }

    public bool SearchCurrent(out string message)
    {
        _offset = 0;
        return LoadCurrentData(discardStagedChanges: false, out message);
    }

    public bool RefreshCurrent(bool discardStagedChanges, out string message) =>
        LoadCurrentData(discardStagedChanges, out message);

    public bool NextPage(out string message)
    {
        message = string.Empty;
        if (HasStagedChanges)
        {
            message = "Сначала сохраните или отмените изменения перед переходом на другую страницу.";
            StatusMessage = message;
            return false;
        }

        if (!CanNextPage)
        {
            return false;
        }

        _offset += PageSize;
        return LoadCurrentData(discardStagedChanges: true, out message);
    }

    public bool PreviousPage(out string message)
    {
        message = string.Empty;
        if (HasStagedChanges)
        {
            message = "Сначала сохраните или отмените изменения перед переходом на другую страницу.";
            StatusMessage = message;
            return false;
        }

        if (!CanPreviousPage)
        {
            return false;
        }

        _offset = Math.Max(0, _offset - PageSize);
        return LoadCurrentData(discardStagedChanges: true, out message);
    }

    public void SelectHu(HuAssignmentManagementHuItem? hu)
    {
        SelectedHu = hu;
    }

    public void SelectTargetLine(HuAssignmentManagementTargetLineItem? line)
    {
        SelectedTargetLine = line;
    }

    public bool BindSelected(out string message) => StageSelected(assignExisting: false, out message);

    public bool MoveSelected(out string message) => StageSelected(assignExisting: true, out message);

    public bool DetachSelected(out string message)
    {
        message = string.Empty;
        if (Session == null)
        {
            message = "Сначала выберите товар.";
            return false;
        }

        if (!Session.StageDetach(SelectedHu, out message))
        {
            StatusMessage = message;
            NotifyCommandState();
            return false;
        }

        StatusMessage = "HU будет отвязан после сохранения.";
        NotifyAfterLocalChange();
        return true;
    }

    public bool CancelSelectedChange(out string message)
    {
        message = string.Empty;
        if (Session == null)
        {
            message = "Сначала выберите товар.";
            return false;
        }

        if (!Session.CancelChange(SelectedHu, out message))
        {
            StatusMessage = message;
            NotifyCommandState();
            return false;
        }

        StatusMessage = "Изменение HU отменено.";
        NotifyAfterLocalChange();
        return true;
    }

    public void ResetAllChanges()
    {
        if (Session == null)
        {
            return;
        }

        foreach (var hu in Session.HuRows.Where(hu => hu.IsChanged).ToArray())
        {
            Session.CancelChange(hu, out _);
        }

        StatusMessage = "Все локальные изменения отменены.";
        NotifyAfterLocalChange();
    }

    public HuAssignmentManagementSaveResult Save()
    {
        if (Session == null || !HasStagedChanges)
        {
            StatusMessage = "Нет изменений для сохранения.";
            return HuAssignmentManagementSaveResult.NoChanges(StatusMessage);
        }

        return RunGuardedSave();
    }

    private HuAssignmentManagementSaveResult RunGuardedSave()
    {
        try
        {
            IsBusy = true;
            var request = Session!.BuildApplyRequest();
            if (request.Lines.Count == 0)
            {
                StatusMessage = "Нет изменений для сохранения.";
                return HuAssignmentManagementSaveResult.NoChanges(StatusMessage);
            }

            if (!_api.TryApplyManageHuBindings(request, out _, out var error))
            {
                if (error != null && StaleErrorCodes.Contains(error.ErrorCode))
                {
                    var reloadMessage = string.Empty;
                    var reloaded = LoadCurrentData(discardStagedChanges: true, out reloadMessage);
                    StatusMessage = reloaded
                        ? "Данные изменились на сервере. Состояние обновлено."
                        : "Данные изменились на сервере, но обновить состояние не удалось.";
                    return HuAssignmentManagementSaveResult.StaleReloaded(StatusMessage, error);
                }

                if (error == null)
                {
                    StatusMessage = "Не удалось связаться с сервером. Локальные изменения сохранены на экране.";
                    return HuAssignmentManagementSaveResult.NetworkFailure(StatusMessage);
                }

                StatusMessage = BuildApplyErrorMessage(error);
                return HuAssignmentManagementSaveResult.Failed(StatusMessage, error);
            }

            Session.MarkSaveSuccess();
            var reloadOk = LoadCurrentData(discardStagedChanges: true, out _);
            StatusMessage = reloadOk ? "Сохранено." : "Сохранено. Не удалось обновить список после сохранения.";
            return HuAssignmentManagementSaveResult.Success(StatusMessage);
        }
        catch (Exception ex)
        {
            _logException?.Invoke(ex);
            StatusMessage = "Не удалось сохранить изменения HU.";
            return HuAssignmentManagementSaveResult.Unexpected(StatusMessage, ex);
        }
        finally
        {
            IsBusy = false;
            NotifyAllState();
        }
    }

    private bool LoadCurrentData(bool discardStagedChanges, out string message)
    {
        message = string.Empty;
        if (SelectedItem == null)
        {
            message = "Выберите товар.";
            StatusMessage = message;
            return false;
        }

        if (HasStagedChanges && !discardStagedChanges)
        {
            message = "Сначала сохраните или отмените изменения.";
            StatusMessage = message;
            return false;
        }

        var localMessage = string.Empty;
        var ok = RunGuarded(() =>
        {
            var filter = new WpfHuBindingManageHuFilter
            {
                HuSearch = HuSearch,
                OrderSearch = OrderSearch,
                PartnerSearch = PartnerSearch,
                State = StateFilter,
                Limit = PageSize,
                Offset = _offset
            };

            if (!_api.TryGetManageHus(SelectedItem.ItemId, filter, out var page))
            {
                StatusMessage = "Не удалось загрузить HU выбранного товара.";
                localMessage = StatusMessage;
                return false;
            }

            if (!_api.TryGetManageTargets(SelectedItem.ItemId, out var targets))
            {
                StatusMessage = "Не удалось загрузить строки заказов для привязки HU.";
                localMessage = StatusMessage;
                return false;
            }

            _currentPage = page;
            Session = new HuAssignmentManagementSession(page, targets);
            SelectedHu = null;
            SelectedTargetLine = null;
            StatusMessage = $"{Session.ItemName}: {Session.Summary}. {PageStatus}";
            NotifyAllState();
            return true;
        }, ref localMessage);
        message = localMessage;
        return ok;
    }

    private bool StageSelected(bool assignExisting, out string message)
    {
        message = string.Empty;
        if (Session == null)
        {
            message = "Сначала выберите товар.";
            return false;
        }

        if (assignExisting && SelectedHu?.FutureOrderLineId.HasValue != true)
        {
            message = "Выбранный HU ещё не назначен строке.";
            StatusMessage = message;
            NotifyCommandState();
            return false;
        }

        if (!assignExisting && SelectedHu?.FutureOrderLineId.HasValue == true)
        {
            message = "HU уже назначен. Используйте перенос.";
            StatusMessage = message;
            NotifyCommandState();
            return false;
        }

        if (!Session.StageBind(SelectedHu, SelectedTargetLine, out message))
        {
            StatusMessage = message;
            NotifyCommandState();
            return false;
        }

        StatusMessage = assignExisting
            ? "HU будет перенесён после сохранения."
            : "HU будет привязан после сохранения.";
        NotifyAfterLocalChange();
        return true;
    }

    private bool CanAssignSelected(bool requireExistingFutureAssignment, bool requireDifferentTarget)
    {
        if (SelectedHu == null || SelectedTargetLine == null || SelectedHu.IsMixed)
        {
            return false;
        }

        if (SelectedHu.ItemId != SelectedTargetLine.ItemId)
        {
            return false;
        }

        if (requireExistingFutureAssignment != SelectedHu.FutureOrderLineId.HasValue)
        {
            return false;
        }

        if (requireDifferentTarget && SelectedHu.FutureOrderLineId == SelectedTargetLine.OrderLineId)
        {
            return false;
        }

        if (!requireExistingFutureAssignment && SelectedHu.FutureOrderLineId.HasValue)
        {
            return false;
        }

        return SelectedHu.Qty <= SelectedTargetLine.RemainingFutureCapacity + QtyTolerance;
    }

    private bool RunGuarded(Func<bool> action, ref string message)
    {
        try
        {
            IsBusy = true;
            return action();
        }
        catch (Exception ex)
        {
            _logException?.Invoke(ex);
            message = "Не удалось обновить данные управления HU.";
            StatusMessage = message;
            return false;
        }
        finally
        {
            IsBusy = false;
            NotifyAllState();
        }
    }

    private static string BuildApplyErrorMessage(WpfHuBindingManageApplyError error)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(error.Message))
        {
            parts.Add(error.Message);
        }
        else if (!string.IsNullOrWhiteSpace(error.ErrorCode))
        {
            parts.Add(error.ErrorCode);
        }

        if (error.Problems.Count > 0)
        {
            parts.Add(string.Join(Environment.NewLine, error.Problems));
        }

        return parts.Count == 0 ? "Сервер отклонил изменения HU." : string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    private void NotifyAfterLocalChange()
    {
        NotifyAllState();
    }

    private void NotifyCommandState()
    {
        OnPropertyChanged(nameof(SelectedHu));
        OnPropertyChanged(nameof(SelectedTargetLine));
        OnPropertyChanged(nameof(CanBind));
        OnPropertyChanged(nameof(CanMove));
        OnPropertyChanged(nameof(CanDetach));
        OnPropertyChanged(nameof(CanCancelSelectedChange));
        OnPropertyChanged(nameof(CanResetAllChanges));
        OnPropertyChanged(nameof(CanSave));
    }

    private void NotifyAllState()
    {
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(PageStart));
        OnPropertyChanged(nameof(PageEnd));
        OnPropertyChanged(nameof(PageStatus));
        OnPropertyChanged(nameof(HasStagedChanges));
        OnPropertyChanged(nameof(CanPreviousPage));
        OnPropertyChanged(nameof(CanNextPage));
        NotifyCommandState();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class HuAssignmentManagementSaveResult
{
    private HuAssignmentManagementSaveResult(
        HuAssignmentManagementSaveOutcome outcome,
        string message,
        WpfHuBindingManageApplyError? error = null,
        Exception? exception = null)
    {
        Outcome = outcome;
        Message = message;
        Error = error;
        Exception = exception;
    }

    public HuAssignmentManagementSaveOutcome Outcome { get; }
    public string Message { get; }
    public WpfHuBindingManageApplyError? Error { get; }
    public Exception? Exception { get; }

    public static HuAssignmentManagementSaveResult NoChanges(string message) =>
        new(HuAssignmentManagementSaveOutcome.NoChanges, message);

    public static HuAssignmentManagementSaveResult Success(string message) =>
        new(HuAssignmentManagementSaveOutcome.Success, message);

    public static HuAssignmentManagementSaveResult StaleReloaded(string message, WpfHuBindingManageApplyError error) =>
        new(HuAssignmentManagementSaveOutcome.StaleReloaded, message, error);

    public static HuAssignmentManagementSaveResult NetworkFailure(string message) =>
        new(HuAssignmentManagementSaveOutcome.NetworkFailure, message);

    public static HuAssignmentManagementSaveResult Failed(string message, WpfHuBindingManageApplyError error) =>
        new(HuAssignmentManagementSaveOutcome.Failed, message, error);

    public static HuAssignmentManagementSaveResult Unexpected(string message, Exception exception) =>
        new(HuAssignmentManagementSaveOutcome.UnexpectedError, message, exception: exception);
}

public enum HuAssignmentManagementSaveOutcome
{
    NoChanges,
    Success,
    StaleReloaded,
    NetworkFailure,
    Failed,
    UnexpectedError
}

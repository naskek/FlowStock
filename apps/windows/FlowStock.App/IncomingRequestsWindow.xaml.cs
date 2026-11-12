using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using FlowStock.Core.Models;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlowStock.App;

public partial class IncomingRequestsWindow : Window
{
    private readonly AppServices _services = null!;
    private readonly ObservableCollection<IncomingRequestRow> _rows = new();
    private readonly ObservableCollection<BusinessNotificationRow> _notificationRows = new();
    private readonly Action? _onChanged;
    private bool _commandInProgress;
    private bool _liveRefreshPending;
    private readonly IDisposable? _liveRefreshSubscription;
    private bool _journalReady, _journalLoading, _windowClosed, _isPreview;
    private int _notificationOffset, _notificationTotal, _journalGeneration;
    private CancellationTokenSource? _journalCancellation;
    private DateTime? _journalUpdatedAt;
    private IReadOnlyList<BusinessNotification> _previewNotifications = Array.Empty<BusinessNotification>();
    private readonly IReadOnlyList<IncomingRequestTypeFilterOption> _filterOptions =
    [
        new("Все", IncomingRequestTypeFilter.All),
        new("Товары", IncomingRequestTypeFilter.Item),
        new("Заказы", IncomingRequestTypeFilter.Order)
    ];

    public IncomingRequestsWindow(AppServices services, Action? onChanged)
    {
        _services = services;
        _onChanged = onChanged;
        InitializeComponent();

        RequestsGrid.ItemsSource = _rows;
        NotificationsGrid.ItemsSource = _notificationRows;
        RequestsGrid.SelectionChanged += RequestsGrid_SelectionChanged;
        RequestTypeFilterCombo.ItemsSource = _filterOptions;
        RequestTypeFilterCombo.DisplayMemberPath = nameof(IncomingRequestTypeFilterOption.Label);
        RequestTypeFilterCombo.SelectedIndex = 0;
        _liveRefreshSubscription = _services.LiveRefresh.Register(
            () => IsVisible && IsActive && !_commandInProgress && !_journalLoading && !WpfLiveRefreshGuard.IsDataGridEditing(RequestsGrid),
            ApplyLiveRefresh,
            () => _liveRefreshPending = true);
        Activated += (_, _) => ApplyPendingLiveRefresh();
        Closed += (_, _) =>
        {
            _windowClosed = true;
            _journalCancellation?.Cancel();
            _liveRefreshSubscription?.Dispose();
        };
        _journalReady = true;
        LoadAll();
    }

    private void ApplyLiveRefresh()
    {
        _liveRefreshPending = false;
        LoadAll();
    }

    private void ApplyPendingLiveRefresh()
    {
        if (_liveRefreshPending && IsVisible && IsActive && !_commandInProgress)
        {
            ApplyLiveRefresh();
        }
    }

    private void LoadRequests()
    {
        if (_isPreview) return;
        _rows.Clear();
        var includeResolved = ShowResolvedCheck?.IsChecked == true;

        var itemRequests = _services.WpfIncomingRequestsApi.TryGetItemRequests(includeResolved, out var apiItemRequests)
            ? apiItemRequests
            : Array.Empty<ItemRequest>();

        var orderRequests = _services.WpfIncomingRequestsApi.TryGetOrderRequests(includeResolved, out var apiOrderRequests)
            ? apiOrderRequests
            : Array.Empty<OrderRequest>();

        var rows = IncomingRequestsRowsBuilder.Build(
            itemRequests,
            orderRequests,
            GetSelectedFilter(),
            DateTime.Now);

        foreach (var row in rows)
        {
            _rows.Add(row);
        }

        UpdateButtons();
    }

    private void LoadAll()
    {
        LoadRequests();
        LoadNotifications();
    }

    private void LoadNotifications() => _ = LoadNotificationsAsync();

    private async Task LoadNotificationsAsync()
    {
        if (!_journalReady || _windowClosed) return;
        if (NotificationFromDate.SelectedDate > NotificationToDate.SelectedDate)
        {
            ++_journalGeneration;
            _journalCancellation?.Cancel();
            _journalCancellation = null;
            _journalLoading = false;
            UpdateNotificationButtons();
            NotificationLoadStatus.Text = "Дата начала должна быть не позже даты окончания.";
            return;
        }
        var query = new BusinessNotificationQuery(
            ShowReadNotificationsCheck.IsChecked != true, 50, _notificationOffset,
            NotificationSearchBox.Text.Trim(), (NotificationCategoryCombo.SelectedItem as ComboBoxItem)?.Tag as string,
            NotificationFromDate.SelectedDate?.Date.ToUniversalTime(),
            NotificationToDate.SelectedDate?.Date.AddDays(1).ToUniversalTime());
        var generation = ++_journalGeneration;
        _journalCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _journalCancellation = cancellation;
        _journalLoading = true;
        NotificationLoadStatus.Text = "Обновление журнала…";
        UpdateNotificationButtons();
        try
        {
            var page = _isPreview ? PreviewPage(query)
                : await _services.WpfIncomingRequestsApi.GetBusinessNotificationPageAsync(query, cancellation.Token);
            if (_windowClosed || generation != _journalGeneration) return;
            if (page == null)
            {
                NotificationLoadStatus.Text = "Не удалось обновить журнал. Нажмите «Обновить», чтобы повторить."
                    + (_journalUpdatedAt.HasValue ? $" Показаны данные на {_journalUpdatedAt:HH:mm:ss}." : "");
                return;
            }
            if (_notificationOffset > 0 && _notificationOffset >= page.Total)
            {
                _notificationOffset = Math.Max(0, ((Math.Max(1, page.Total) - 1) / 50) * 50);
                LoadNotifications();
                return;
            }
            var selected = NotificationsGrid.SelectedItems.Cast<BusinessNotificationRow>().Select(row => row.Id).ToHashSet();
            var scroll = FindScrollViewer(NotificationsGrid);
            var verticalOffset = scroll?.VerticalOffset ?? 0;
            var previousById = _notificationRows.ToDictionary(row => row.Id);
            for (var i = 0; i < page.Items.Count; i++)
            {
                var item = page.Items[i];
                var row = previousById.TryGetValue(item.Id, out var previous) && previous.IsUnread == !item.IsRead
                    ? previous : new BusinessNotificationRow(item);
                if (i < _notificationRows.Count)
                {
                    if (!ReferenceEquals(_notificationRows[i], row)) _notificationRows[i] = row;
                }
                else _notificationRows.Add(row);
            }
            while (_notificationRows.Count > page.Items.Count) _notificationRows.RemoveAt(_notificationRows.Count - 1);
            foreach (var row in _notificationRows.Where(row => selected.Contains(row.Id)))
                if (!NotificationsGrid.SelectedItems.Contains(row)) NotificationsGrid.SelectedItems.Add(row);
            scroll?.ScrollToVerticalOffset(verticalOffset);
            _notificationTotal = page.Total;
            _journalUpdatedAt = DateTime.Now;
            EventJournalTab.Header = $"Журнал событий ({page.UnreadCount})";
            NotificationsPageText.Text = page.Total == 0 ? "0 событий"
                : $"{_notificationOffset + 1}–{_notificationOffset + page.Items.Count} из {page.Total}";
            NotificationLoadStatus.Text = page.Items.Count == 0
                ? (query.Search != "" || query.Category != null || query.FromUtc != null || query.ToUtc != null
                    ? "За выбранный период и по указанным условиям событий нет."
                    : query.UnreadOnly ? "Новых событий нет." : "Событий пока нет.")
                : $"Обновлено в {_journalUpdatedAt:HH:mm:ss}. Время событий — местное время этого рабочего места.";
            ActionRequiredTab.Header = $"Требуют действия ({_rows.Count(row => row.CanApprove || row.CanReject)})";
        }
        finally
        {
            cancellation.Dispose();
            if (generation == _journalGeneration)
            {
                _journalCancellation = null;
                _journalLoading = false;
                if (!_windowClosed) UpdateNotificationButtons();
            }
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }

    private void NotificationsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateNotificationButtons();

    private void UpdateNotificationButtons()
    {
        if (MarkNotificationReadButton == null) return;
        MarkNotificationReadButton.IsEnabled = !_isPreview && !_commandInProgress && !_journalLoading
            && NotificationsGrid.SelectedItems.Cast<BusinessNotificationRow>().Any(row => row.IsUnread);
        MarkAllNotificationsReadButton.IsEnabled = !_isPreview && !_commandInProgress && !_journalLoading;
        NotificationsPreviousButton.IsEnabled = !_journalLoading && _notificationOffset > 0;
        NotificationsNextButton.IsEnabled = !_journalLoading && _notificationOffset + 50 < _notificationTotal;
        var row = NotificationsGrid.SelectedItem as BusinessNotificationRow;
        NotificationDetailsHint.Visibility = row == null ? Visibility.Visible : Visibility.Collapsed;
        NotificationDetailsContent.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
        OpenNotificationOrderButton.IsEnabled = !_isPreview && row?.OrderId is > 0;
        OpenNotificationDocButton.IsEnabled = !_isPreview && row?.DocId is > 0;
    }

    private async void MarkNotificationRead_Click(object sender, RoutedEventArgs e)
    {
        if (_commandInProgress || _isPreview) return;
        var ids = NotificationsGrid.SelectedItems.Cast<BusinessNotificationRow>().Where(row => row.IsUnread).Select(row => row.Id).ToArray();
        _commandInProgress = true;
        UpdateNotificationButtons();
        try
        {
            foreach (var id in ids)
            {
                if (!await _services.WpfIncomingRequestsApi.TryMarkBusinessNotificationReadAsync(id))
                {
                    NotificationLoadStatus.Text = "Не удалось изменить статус просмотра. Обновите журнал для проверки результата.";
                    return;
                }
            }
            await LoadNotificationsAsync();
            _onChanged?.Invoke();
        }
        finally { _commandInProgress = false; UpdateNotificationButtons(); }
    }

    private async void MarkAllNotificationsRead_Click(object sender, RoutedEventArgs e)
    {
        if (_commandInProgress || _isPreview) return;
        _commandInProgress = true;
        UpdateNotificationButtons();
        try
        {
            if (!await _services.WpfIncomingRequestsApi.TryMarkAllBusinessNotificationsReadAsync())
            {
                NotificationLoadStatus.Text = "Не удалось изменить статус просмотра. Обновите журнал для проверки результата.";
                return;
            }
            await LoadNotificationsAsync();
            _onChanged?.Invoke();
        }
        finally { _commandInProgress = false; UpdateNotificationButtons(); }
    }

    private void OpenNotificationOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_isPreview || (NotificationsGrid.SelectedItem as BusinessNotificationRow)?.OrderId is not long id) return;
        try { new OrderDetailsWindow(_services, id) { Owner = this }.ShowDialog(); }
        catch (Exception) { MessageBox.Show("Не удалось открыть заказ. Возможно, он больше недоступен.", "Центр событий"); }
    }

    private void OpenNotificationDoc_Click(object sender, RoutedEventArgs e)
    {
        if (_isPreview || (NotificationsGrid.SelectedItem as BusinessNotificationRow)?.DocId is not long id) return;
        try { new OperationDetailsWindow(_services, id) { Owner = this }.ShowDialog(); }
        catch (Exception) { MessageBox.Show("Не удалось открыть отгрузку. Возможно, она больше недоступна.", "Центр событий"); }
    }

    private void RefreshNotifications_Click(object sender, RoutedEventArgs e) => LoadNotifications();
    private void ShowReadNotifications_Changed(object sender, RoutedEventArgs e) => ResetNotificationFilters();
    private void NotificationFilter_Changed(object sender, SelectionChangedEventArgs e) => ResetNotificationFilters();
    private void NotificationDate_Changed(object? sender, SelectionChangedEventArgs e) => ResetNotificationFilters();
    private void NotificationSearch_Click(object sender, RoutedEventArgs e) => ResetNotificationFilters();
    private void NotificationSearch_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ResetNotificationFilters(); }
    private void ResetNotificationFilters() { if (!_journalReady) return; _notificationOffset = 0; LoadNotifications(); }
    private void NotificationsPrevious_Click(object sender, RoutedEventArgs e) { _notificationOffset = Math.Max(0, _notificationOffset - 50); LoadNotifications(); }
    private void NotificationsNext_Click(object sender, RoutedEventArgs e) { _notificationOffset += 50; LoadNotifications(); }

    private void RequestsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var selected = RequestsGrid.SelectedItems.Cast<IncomingRequestRow>().ToList();
        ApproveButton.IsEnabled = selected.Any(row => row.CanApprove);
        RejectButton.IsEnabled = selected.Any(row => row.CanReject);
        DetailsButton.IsEnabled = selected.Count == 1 && selected[0].CanOpenDetails;
    }

    private List<IncomingRequestRow> GetSelectedForApprove()
    {
        return RequestsGrid.SelectedItems
            .Cast<IncomingRequestRow>()
            .Where(row => row.CanApprove)
            .ToList();
    }

    private List<IncomingRequestRow> GetSelectedForReject()
    {
        return RequestsGrid.SelectedItems
            .Cast<IncomingRequestRow>()
            .Where(row => row.CanReject && row.OrderRequest != null)
            .ToList();
    }

    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedForApprove();
        if (selected.Count == 0)
        {
            MessageBox.Show("Выберите необработанные запросы.", "Центр событий", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Подтвердить/обработать выбранные запросы ({selected.Count})?",
            "Центр событий",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _commandInProgress = true;
        var resolvedBy = Environment.UserName;
        var processedItems = 0;
        var approvedOrders = 0;
        var errors = new List<string>();

        foreach (var row in selected)
        {
            try
            {
                if (row.ItemRequest != null)
                {
                    if (!await _services.WpfIncomingRequestsApi
                            .TryResolveItemRequestAsync(row.ItemRequest.Id)
                            .ConfigureAwait(true))
                    {
                        throw new InvalidOperationException("Не удалось отметить запрос товара обработанным через сервер.");
                    }

                    processedItems++;
                    continue;
                }

                if (row.OrderRequest != null)
                {
                    if (_services.IncomingRequestOrderApprovals.CanHandle(row.OrderRequest.RequestType))
                    {
                        var outcome = await _services.IncomingRequestOrderApprovals
                            .ApproveAsync(row.OrderRequest, resolvedBy)
                            .ConfigureAwait(true);

                        if (!outcome.IsSuccess)
                        {
                            var requestId = row.OrderRequest.Id;
                            errors.Add($"#{requestId}: {outcome.Message}");
                            continue;
                        }

                        approvedOrders++;
                        continue;
                    }

                    errors.Add($"#{row.OrderRequest.Id}: Неподдерживаемый тип заявки: {row.OrderRequest.RequestType}");
                }
            }
            catch (Exception ex)
            {
                var requestId = row.ItemRequest?.Id ?? row.OrderRequest?.Id ?? 0;
                errors.Add($"#{requestId}: {ex.Message}");
            }
        }

        LoadRequests();
        _onChanged?.Invoke();
        _commandInProgress = false;
        ApplyPendingLiveRefresh();

        if (errors.Count > 0)
        {
            var text = "Часть запросов не удалось обработать:\n" + string.Join("\n", errors);
            MessageBox.Show(text, "Центр событий", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(
            $"Обработано запросов по товарам: {processedItems}\nПодтверждено заявок по заказам: {approvedOrders}",
            "Центр событий",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void Reject_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedForReject();
        if (selected.Count == 0)
        {
            MessageBox.Show("Выберите заявки по заказам в статусе ожидания.", "Центр событий", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Отклонить выбранные заявки по заказам ({selected.Count})?",
            "Центр событий",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _commandInProgress = true;
        try
        {
            var resolvedBy = Environment.UserName;
            foreach (var row in selected)
            {
                var orderRequest = row.OrderRequest!;
                var resolved = await _services.WpfIncomingRequestsApi
                    .TryRejectOrderRequestAsync(orderRequest.Id, resolvedBy)
                    .ConfigureAwait(true);

                if (!resolved)
                {
                    throw new InvalidOperationException($"Не удалось отклонить заявку #{orderRequest.Id} через сервер.");
                }
            }

            LoadRequests();
            _onChanged?.Invoke();
        }
        finally
        {
            _commandInProgress = false;
            ApplyPendingLiveRefresh();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadRequests();
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (RequestsGrid.SelectedItem is not IncomingRequestRow row)
        {
            MessageBox.Show("Выберите одну заявку.", "Центр событий", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (row.OrderRequest != null)
        {
            OpenOrderRequestDetails(row.OrderRequest);
            return;
        }

        MessageBox.Show("Для выбранного запроса подробности недоступны.", "Центр событий", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RequestsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RequestsGrid.SelectedItem is IncomingRequestRow row && row.OrderRequest != null)
        {
            OpenOrderRequestDetails(row.OrderRequest);
        }
    }

    private void OpenOrderRequestDetails(OrderRequest request)
    {
        var window = new OrderRequestDetailsWindow(_services, request)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void ShowResolvedCheck_Changed(object sender, RoutedEventArgs e)
    {
        LoadRequests();
    }

    private void RequestTypeFilterCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        LoadRequests();
    }

    private IncomingRequestTypeFilter GetSelectedFilter()
    {
        return RequestTypeFilterCombo?.SelectedItem is IncomingRequestTypeFilterOption option
            ? option.Filter
            : IncomingRequestTypeFilter.All;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private sealed record IncomingRequestTypeFilterOption(string Label, IncomingRequestTypeFilter Filter);
}

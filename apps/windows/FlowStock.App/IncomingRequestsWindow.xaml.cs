using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class IncomingRequestsWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<IncomingRequestRow> _rows = new();
    private readonly Action? _onChanged;
    private readonly IReadOnlyList<IncomingRequestTypeFilterOption> _filterOptions =
    [
        new("Все", IncomingRequestTypeFilter.All),
        new("Товары", IncomingRequestTypeFilter.Item),
        new("Заказы", IncomingRequestTypeFilter.Order),
        new("Готовые HU", IncomingRequestTypeFilter.ReadyHu)
    ];

    public IncomingRequestsWindow(AppServices services, Action? onChanged)
    {
        _services = services;
        _onChanged = onChanged;
        InitializeComponent();

        RequestsGrid.ItemsSource = _rows;
        RequestsGrid.SelectionChanged += RequestsGrid_SelectionChanged;
        RequestTypeFilterCombo.ItemsSource = _filterOptions;
        RequestTypeFilterCombo.DisplayMemberPath = nameof(IncomingRequestTypeFilterOption.Label);
        RequestTypeFilterCombo.SelectedIndex = 0;
        LoadRequests();
    }

    private void LoadRequests()
    {
        _rows.Clear();
        var includeResolved = ShowResolvedCheck?.IsChecked == true;

        var itemRequests = _services.WpfIncomingRequestsApi.TryGetItemRequests(includeResolved, out var apiItemRequests)
            ? apiItemRequests
            : Array.Empty<ItemRequest>();

        var orderRequests = _services.WpfIncomingRequestsApi.TryGetOrderRequests(includeResolved, out var apiOrderRequests)
            ? apiOrderRequests
            : Array.Empty<OrderRequest>();

        var readyHuBinding = _services.WpfReadApi.TryGetReadyHuBindingReadModel(out var apiReadyHuBinding)
            ? apiReadyHuBinding
            : null;

        var rows = IncomingRequestsRowsBuilder.Build(
            itemRequests,
            orderRequests,
            readyHuBinding,
            GetSelectedFilter(),
            DateTime.Now);

        foreach (var row in rows)
        {
            _rows.Add(row);
        }

        UpdateButtons();
    }

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
            MessageBox.Show("Выберите необработанные запросы.", "Входящие запросы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Подтвердить/обработать выбранные запросы ({selected.Count})?",
            "Входящие запросы",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

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

        if (errors.Count > 0)
        {
            var text = "Часть запросов не удалось обработать:\n" + string.Join("\n", errors);
            MessageBox.Show(text, "Входящие запросы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(
            $"Обработано запросов по товарам: {processedItems}\nПодтверждено заявок по заказам: {approvedOrders}",
            "Входящие запросы",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void Reject_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedForReject();
        if (selected.Count == 0)
        {
            MessageBox.Show("Выберите заявки по заказам в статусе ожидания.", "Входящие запросы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Отклонить выбранные заявки по заказам ({selected.Count})?",
            "Входящие запросы",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var resolvedBy = Environment.UserName;
        foreach (var row in selected)
        {
            var orderRequest = row.OrderRequest!;
            var resolved = await _services.WpfIncomingRequestsApi
                .TryResolveOrderRequestAsync(
                    orderRequest.Id,
                    OrderRequestStatus.Rejected,
                    resolvedBy,
                    "Отклонено оператором WPF.",
                    null)
                .ConfigureAwait(true);

            if (!resolved)
            {
                throw new InvalidOperationException($"Не удалось отклонить заявку #{orderRequest.Id} через сервер.");
            }
        }

        LoadRequests();
        _onChanged?.Invoke();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadRequests();
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (RequestsGrid.SelectedItem is not IncomingRequestRow row)
        {
            MessageBox.Show("Выберите одну заявку.", "Входящие запросы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (row.Kind == IncomingRequestRowKind.ReadyHu)
        {
            ShowReadyHuBindingInfo(row);
            return;
        }

        if (row.OrderRequest != null)
        {
            OpenOrderRequestDetails(row.OrderRequest);
            return;
        }

        MessageBox.Show("Для выбранного запроса подробности недоступны.", "Входящие запросы", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RequestsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RequestsGrid.SelectedItem is IncomingRequestRow { Kind: IncomingRequestRowKind.ReadyHu } readyHuRow)
        {
            ShowReadyHuBindingInfo(readyHuRow);
            return;
        }

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

    private static void ShowReadyHuBindingInfo(IncomingRequestRow row)
    {
        var preview = string.IsNullOrWhiteSpace(row.DetailsPreview)
            ? string.Empty
            : Environment.NewLine + Environment.NewLine + row.DetailsPreview;
        MessageBox.Show(
            "Глобальная привязка HU будет добавлена в Phase 4C. Сейчас используйте кнопку \"Привязка HU\" в карточке заказа." + preview,
            "Готовые HU",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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

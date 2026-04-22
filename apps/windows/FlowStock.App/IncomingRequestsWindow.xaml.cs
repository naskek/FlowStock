using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class IncomingRequestsWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<IncomingRequestRow> _rows = new();
    private readonly Action? _onChanged;

    public IncomingRequestsWindow(AppServices services, Action? onChanged)
    {
        _services = services;
        _onChanged = onChanged;
        InitializeComponent();

        RequestsGrid.ItemsSource = _rows;
        RequestsGrid.SelectionChanged += RequestsGrid_SelectionChanged;
        LoadRequests();
    }

    private void LoadRequests()
    {
        _rows.Clear();
        var includeResolved = ShowResolvedCheck?.IsChecked == true;

        var merged = new List<IncomingRequestRow>();
        var itemRequests = _services.WpfIncomingRequestsApi.TryGetItemRequests(includeResolved, out var apiItemRequests)
            ? apiItemRequests
            : Array.Empty<ItemRequest>();
        foreach (var itemRequest in itemRequests)
        {
            merged.Add(new IncomingRequestRow
            {
                ItemRequest = itemRequest,
                SourceDisplay = "Товары",
                TypeDisplay = "Запрос товара",
                Summary = BuildItemSummary(itemRequest),
                RequestedBy = BuildRequestedBy(itemRequest.Login, itemRequest.DeviceId),
                StatusDisplay = GetItemStatusDisplay(itemRequest.Status),
                CreatedAt = itemRequest.CreatedAt,
                ResolvedAt = itemRequest.ResolvedAt,
                ResolutionNote = itemRequest.Status.Equals("RESOLVED", StringComparison.OrdinalIgnoreCase)
                    ? "Отмечено обработанным."
                    : null,
                CanApprove = !itemRequest.Status.Equals("RESOLVED", StringComparison.OrdinalIgnoreCase),
                CanReject = false
            });
        }

        var orderRequests = _services.WpfIncomingRequestsApi.TryGetOrderRequests(includeResolved, out var apiOrderRequests)
            ? apiOrderRequests
            : Array.Empty<OrderRequest>();
        foreach (var orderRequest in orderRequests)
        {
            var isPending = string.Equals(orderRequest.Status, OrderRequestStatus.Pending, StringComparison.OrdinalIgnoreCase);
            merged.Add(new IncomingRequestRow
            {
                OrderRequest = orderRequest,
                SourceDisplay = "Заказы (веб)",
                TypeDisplay = GetOrderTypeDisplay(orderRequest.RequestType),
                Summary = BuildOrderSummary(orderRequest),
                RequestedBy = BuildRequestedBy(orderRequest.CreatedByLogin, orderRequest.CreatedByDeviceId),
                StatusDisplay = GetOrderStatusDisplay(orderRequest.Status),
                CreatedAt = orderRequest.CreatedAt,
                ResolvedAt = orderRequest.ResolvedAt,
                ResolutionNote = orderRequest.ResolutionNote,
                CanApprove = isPending,
                CanReject = isPending
            });
        }

        foreach (var row in merged.OrderByDescending(row => row.CreatedAt))
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
        DetailsButton.IsEnabled = selected.Count == 1 && selected[0].OrderRequest != null;
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
        if (RequestsGrid.SelectedItem is not IncomingRequestRow row || row.OrderRequest == null)
        {
            MessageBox.Show("Выберите одну заявку по заказу.", "Входящие запросы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenOrderRequestDetails(row.OrderRequest);
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

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static string BuildItemSummary(ItemRequest request)
    {
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? "-" : request.Comment.Trim();
        return $"ШК: {request.Barcode} · Комментарий: {comment}";
    }

    private static string BuildOrderSummary(OrderRequest request)
    {
        try
        {
            using var doc = JsonDocument.Parse(request.PayloadJson);
            var root = doc.RootElement;
            if (string.Equals(request.RequestType, OrderRequestType.CreateOrder, StringComparison.OrdinalIgnoreCase))
            {
                var orderRef = root.TryGetProperty("order_ref", out var refEl) ? refEl.GetString() : null;
                var partnerId = root.TryGetProperty("partner_id", out var partnerEl) ? partnerEl.GetInt64() : 0;
                var lineCount = root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array
                    ? linesEl.GetArrayLength()
                    : 0;
                return $"Создать заказ {orderRef ?? "-"} · контрагент ID={partnerId} · строк: {lineCount}";
            }

            if (string.Equals(request.RequestType, OrderRequestType.SetOrderStatus, StringComparison.OrdinalIgnoreCase))
            {
                var orderId = root.TryGetProperty("order_id", out var orderEl) ? orderEl.GetInt64() : 0;
                var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
                var displayStatus = OrderStatusMapper.StatusFromString(status) is { } parsed
                    ? OrderStatusMapper.StatusToDisplayName(parsed)
                    : status ?? "-";
                return $"Смена статуса · заказ ID={orderId} -> {displayStatus}";
            }
        }
        catch
        {
            // keep fallback summary
        }

        return request.PayloadJson;
    }

    private static string BuildRequestedBy(string? login, string? deviceId)
    {
        var normalizedLogin = login?.Trim();
        var normalizedDeviceId = deviceId?.Trim();

        if (!string.IsNullOrWhiteSpace(normalizedLogin) && !string.IsNullOrWhiteSpace(normalizedDeviceId))
        {
            return $"{normalizedLogin} ({normalizedDeviceId})";
        }

        if (!string.IsNullOrWhiteSpace(normalizedLogin))
        {
            return normalizedLogin;
        }

        if (!string.IsNullOrWhiteSpace(normalizedDeviceId))
        {
            return normalizedDeviceId;
        }

        return "-";
    }

    private static string GetItemStatusDisplay(string status)
    {
        return status.Equals("RESOLVED", StringComparison.OrdinalIgnoreCase)
            ? "Обработан"
            : "Новый";
    }

    private static string GetOrderTypeDisplay(string requestType)
    {
        if (string.Equals(requestType, OrderRequestType.CreateOrder, StringComparison.OrdinalIgnoreCase))
        {
            return "Создание заказа";
        }

        if (string.Equals(requestType, OrderRequestType.SetOrderStatus, StringComparison.OrdinalIgnoreCase))
        {
            return "Смена статуса заказа";
        }

        return requestType;
    }

    private static string GetOrderStatusDisplay(string status)
    {
        if (string.Equals(status, OrderRequestStatus.Pending, StringComparison.OrdinalIgnoreCase))
        {
            return "Ожидает";
        }

        if (string.Equals(status, OrderRequestStatus.Approved, StringComparison.OrdinalIgnoreCase))
        {
            return "Подтвержден";
        }

        if (string.Equals(status, OrderRequestStatus.Rejected, StringComparison.OrdinalIgnoreCase))
        {
            return "Отклонен";
        }

        return status;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record IncomingRequestRow
    {
        public ItemRequest? ItemRequest { get; init; }
        public OrderRequest? OrderRequest { get; init; }
        public required string SourceDisplay { get; init; }
        public required string TypeDisplay { get; init; }
        public required string Summary { get; init; }
        public required string RequestedBy { get; init; }
        public required string StatusDisplay { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? ResolvedAt { get; init; }
        public string? ResolutionNote { get; init; }
        public bool CanApprove { get; init; }
        public bool CanReject { get; init; }
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class OrderRequestDetailsWindow : Window
{
    private readonly AppServices _services;
    private readonly OrderRequest _request;
    private readonly ObservableCollection<OrderLineRow> _lines = new();
    private IReadOnlyList<Partner> _partners = Array.Empty<Partner>();
    private IReadOnlyList<Item> _items = Array.Empty<Item>();

    public OrderRequestDetailsWindow(AppServices services, OrderRequest request)
    {
        _services = services;
        _request = request;
        InitializeComponent();
        LinesGrid.ItemsSource = _lines;
        _partners = _services.WpfPartnerApi.TryGetPartners(out var apiPartners)
            ? apiPartners.Select(entry => entry.Partner).ToList()
            : Array.Empty<Partner>();
        _items = _services.WpfReadApi.TryGetItems(null, out var apiItems)
            ? apiItems
            : Array.Empty<Item>();
        LoadRequest();
    }

    private void LoadRequest()
    {
        RequestIdText.Text = _request.Id.ToString(CultureInfo.InvariantCulture);
        StatusText.Text = GetOrderRequestStatusDisplay(_request.Status);
        TypeText.Text = GetOrderRequestTypeDisplay(_request.RequestType);
        CreatedAtText.Text = _request.CreatedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);
        RequestedByText.Text = BuildRequestedBy(_request.CreatedByLogin, _request.CreatedByDeviceId);

        if (string.Equals(_request.RequestType, OrderRequestType.CreateOrder, StringComparison.OrdinalIgnoreCase))
        {
            LoadCreateOrderDetails();
        }
        else if (string.Equals(_request.RequestType, OrderRequestType.SetOrderStatus, StringComparison.OrdinalIgnoreCase))
        {
            LoadStatusChangeDetails();
        }
        else
        {
            OrderRefText.Text = "-";
            PartnerText.Text = "-";
            DueDateText.Text = "-";
            TargetStatusText.Text = "-";
            CommentText.Text = _request.PayloadJson;
            NoLinesText.Visibility = Visibility.Visible;
            LinesGrid.Visibility = Visibility.Collapsed;
        }
    }

    private void LoadCreateOrderDetails()
    {
        CreateOrderPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CreateOrderPayload>(_request.PayloadJson, JsonOptions);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload == null)
        {
            OrderRefText.Text = "-";
            PartnerText.Text = "-";
            DueDateText.Text = "-";
            TargetStatusText.Text = "-";
            CommentText.Text = "Некорректный payload заявки.";
            NoLinesText.Visibility = Visibility.Visible;
            LinesGrid.Visibility = Visibility.Collapsed;
            return;
        }

        OrderRefText.Text = string.IsNullOrWhiteSpace(payload.OrderRef) ? "-" : payload.OrderRef.Trim();
        var partner = payload.PartnerId > 0 ? _partners.FirstOrDefault(candidate => candidate.Id == payload.PartnerId) : null;
        PartnerText.Text = FormatPartner(partner, payload.PartnerId);
        DueDateText.Text = FormatDueDate(payload.DueDate);
        TargetStatusText.Text = OrderStatusMapper.StatusToDisplayName(OrderStatus.Accepted);
        CommentText.Text = string.IsNullOrWhiteSpace(payload.Comment) ? "-" : payload.Comment.Trim();

        _lines.Clear();
        var lines = payload.Lines ?? new List<CreateOrderLinePayload>();
        foreach (var line in lines)
        {
            var item = line.ItemId > 0 ? _items.FirstOrDefault(candidate => candidate.Id == line.ItemId) : null;
            _lines.Add(new OrderLineRow
            {
                ItemId = line.ItemId,
                ItemName = item?.Name ?? $"ID={line.ItemId}",
                Barcode = item?.Barcode ?? "-",
                Gtin = item?.Gtin ?? "-",
                QtyOrdered = line.QtyOrdered
            });
        }

        var hasLines = _lines.Count > 0;
        NoLinesText.Visibility = hasLines ? Visibility.Collapsed : Visibility.Visible;
        LinesGrid.Visibility = hasLines ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadStatusChangeDetails()
    {
        SetOrderStatusPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SetOrderStatusPayload>(_request.PayloadJson, JsonOptions);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload == null)
        {
            OrderRefText.Text = "-";
            PartnerText.Text = "-";
            DueDateText.Text = "-";
            TargetStatusText.Text = "-";
            CommentText.Text = "Некорректный payload заявки.";
            NoLinesText.Visibility = Visibility.Visible;
            LinesGrid.Visibility = Visibility.Collapsed;
            return;
        }

        var order = payload.OrderId > 0
            ? (_services.WpfReadApi.TryGetOrder(payload.OrderId, out var apiOrder) ? apiOrder : null)
            : null;
        OrderRefText.Text = order?.OrderRef ?? $"ID={payload.OrderId}";
        var partner = order?.PartnerId.HasValue == true
            ? _partners.FirstOrDefault(candidate => candidate.Id == order.PartnerId.Value)
            : null;
        PartnerText.Text = order != null ? FormatPartner(partner, order.PartnerId) : "-";
        DueDateText.Text = order?.DueDate.HasValue == true
            ? order.DueDate.Value.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture)
            : "-";

        var targetStatus = OrderStatusMapper.StatusFromString(payload.Status);
        TargetStatusText.Text = targetStatus.HasValue
            ? OrderStatusMapper.StatusToDisplayName(targetStatus.Value)
            : payload.Status ?? "-";

        CommentText.Text = order?.Comment ?? "-";

        _lines.Clear();
        NoLinesText.Visibility = Visibility.Visible;
        LinesGrid.Visibility = Visibility.Collapsed;
    }

    private static string FormatPartner(Partner? partner, long? partnerId)
    {
        if (partner == null)
        {
            return partnerId.HasValue && partnerId.Value > 0 ? $"ID={partnerId.Value}" : "-";
        }

        if (!string.IsNullOrWhiteSpace(partner.Code))
        {
            return $"{partner.Code} - {partner.Name}";
        }

        return partner.Name;
    }

    private static string FormatDueDate(string? dueDate)
    {
        if (string.IsNullOrWhiteSpace(dueDate))
        {
            return "-";
        }

        if (DateTime.TryParseExact(
                dueDate.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return parsed.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture);
        }

        return dueDate.Trim();
    }

    private static string BuildRequestedBy(string? login, string? deviceId)
    {
        var normalizedLogin = login?.Trim();
        var normalizedDevice = deviceId?.Trim();

        if (!string.IsNullOrWhiteSpace(normalizedLogin) && !string.IsNullOrWhiteSpace(normalizedDevice))
        {
            return $"{normalizedLogin} ({normalizedDevice})";
        }

        if (!string.IsNullOrWhiteSpace(normalizedLogin))
        {
            return normalizedLogin;
        }

        if (!string.IsNullOrWhiteSpace(normalizedDevice))
        {
            return normalizedDevice;
        }

        return "-";
    }

    private static string GetOrderRequestTypeDisplay(string requestType)
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

    private static string GetOrderRequestStatusDisplay(string status)
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

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record CreateOrderPayload
    {
        [JsonPropertyName("order_ref")]
        public string? OrderRef { get; init; }

        [JsonPropertyName("partner_id")]
        public long PartnerId { get; init; }

        [JsonPropertyName("due_date")]
        public string? DueDate { get; init; }

        [JsonPropertyName("comment")]
        public string? Comment { get; init; }

        [JsonPropertyName("lines")]
        public List<CreateOrderLinePayload>? Lines { get; init; }
    }

    private sealed record CreateOrderLinePayload
    {
        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("qty_ordered")]
        public double QtyOrdered { get; init; }
    }

    private sealed record SetOrderStatusPayload
    {
        [JsonPropertyName("order_id")]
        public long OrderId { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }
    }

    private sealed record OrderLineRow
    {
        public long ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public string Barcode { get; init; } = "-";
        public string Gtin { get; init; } = "-";
        public double QtyOrdered { get; init; }
    }
}

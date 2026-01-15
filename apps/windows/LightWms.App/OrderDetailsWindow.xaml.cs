using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using LightWms.Core.Models;

namespace LightWms.App;

public partial class OrderDetailsWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<Item> _items = new();
    private readonly ObservableCollection<Partner> _partners = new();
    private readonly ObservableCollection<OrderLineView> _lines = new();
    private readonly List<OrderStatusOption> _statusOptions = new()
    {
        new OrderStatusOption(OrderStatus.Accepted, OrderStatusMapper.StatusToDisplayName(OrderStatus.Accepted)),
        new OrderStatusOption(OrderStatus.InProgress, OrderStatusMapper.StatusToDisplayName(OrderStatus.InProgress))
    };
    private readonly List<OrderStatusOption> _statusOptionsAll = new()
    {
        new OrderStatusOption(OrderStatus.Accepted, OrderStatusMapper.StatusToDisplayName(OrderStatus.Accepted)),
        new OrderStatusOption(OrderStatus.InProgress, OrderStatusMapper.StatusToDisplayName(OrderStatus.InProgress)),
        new OrderStatusOption(OrderStatus.Shipped, OrderStatusMapper.StatusToDisplayName(OrderStatus.Shipped))
    };

    private Order? _order;
    private OrderLineView? _selectedLine;
    private long? _orderId;

    public OrderDetailsWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        InitializeData();
        LoadCatalog();
        PrepareNewOrder();
    }

    public OrderDetailsWindow(AppServices services, long orderId)
    {
        _services = services;
        _orderId = orderId;
        InitializeComponent();
        InitializeData();
        LoadCatalog();
        LoadOrder();
    }

    private void InitializeData()
    {
        OrderLinesGrid.ItemsSource = _lines;
        LineItemCombo.ItemsSource = _items;
        PartnerCombo.ItemsSource = _partners;
    }

    private void LoadCatalog()
    {
        _items.Clear();
        foreach (var item in _services.Catalog.GetItems(null))
        {
            _items.Add(item);
        }

        _partners.Clear();
        foreach (var partner in _services.Catalog.GetPartners())
        {
            _partners.Add(partner);
        }
    }

    private void PrepareNewOrder()
    {
        Title = "Новый заказ";
        _order = null;
        OrderRefBox.Text = string.Empty;
        PartnerCombo.SelectedItem = null;
        DueDatePicker.SelectedDate = null;
        CommentBox.Text = string.Empty;
        StatusCombo.ItemsSource = _statusOptions;
        StatusCombo.SelectedItem = _statusOptions[0];
        _lines.Clear();
        RefreshLineMetrics();
        SetEditingEnabled(true);
    }

    private void LoadOrder()
    {
        if (!_orderId.HasValue)
        {
            PrepareNewOrder();
            return;
        }

        _order = _services.Orders.GetOrder(_orderId.Value);
        if (_order == null)
        {
            MessageBox.Show("Заказ не найден.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
            return;
        }

        Title = $"Заказ: {_order.OrderRef}";
        OrderRefBox.Text = _order.OrderRef;
        PartnerCombo.SelectedItem = _partners.FirstOrDefault(p => p.Id == _order.PartnerId);
        DueDatePicker.SelectedDate = _order.DueDate;
        CommentBox.Text = _order.Comment ?? string.Empty;

        var isShipped = _order.Status == OrderStatus.Shipped;
        StatusCombo.ItemsSource = isShipped ? _statusOptionsAll : _statusOptions;
        StatusCombo.SelectedItem = (StatusCombo.ItemsSource as IEnumerable<OrderStatusOption>)?
            .FirstOrDefault(option => option.Status == _order.Status);

        _lines.Clear();
        foreach (var line in _services.Orders.GetOrderLineViews(_order.Id))
        {
            _lines.Add(line);
        }

        RefreshLineMetrics();
        SetEditingEnabled(!isShipped);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetHeaderValues(out var orderRef, out var partnerId, out var dueDate, out var status, out var comment))
        {
            return;
        }

        try
        {
            if (_orderId.HasValue)
            {
                _services.Orders.UpdateOrder(_orderId.Value, orderRef, partnerId, dueDate, status, comment, _lines);
            }
            else
            {
                _orderId = _services.Orders.CreateOrder(orderRef, partnerId, dueDate, status, comment, _lines);
            }

            LoadOrder();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Заказы", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureEditable())
        {
            return;
        }

        if (LineItemCombo.SelectedItem is not Item item)
        {
            MessageBox.Show("Выберите товар.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseQty(LineQtyBox.Text, out var qty))
        {
            MessageBox.Show("Количество должно быть больше 0.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var existing = _lines.FirstOrDefault(l => l.ItemId == item.Id);
        if (existing != null)
        {
            existing.QtyOrdered += qty;
        }
        else
        {
            _lines.Add(new OrderLineView
            {
                ItemId = item.Id,
                ItemName = item.Name,
                QtyOrdered = qty
            });
        }

        LineQtyBox.Text = string.Empty;
        RefreshLineMetrics();
    }

    private void DeleteLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureEditable())
        {
            return;
        }

        if (_selectedLine == null)
        {
            MessageBox.Show("Выберите строку.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _lines.Remove(_selectedLine);
        _selectedLine = null;
        RefreshLineMetrics();
    }

    private void OrderLinesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedLine = OrderLinesGrid.SelectedItem as OrderLineView;
        DeleteLineButton.IsEnabled = _selectedLine != null && EnsureEditable(false);
    }

    private void CreateOutbound_Click(object sender, RoutedEventArgs e)
    {
        if (!_orderId.HasValue)
        {
            MessageBox.Show("Сначала сохраните заказ.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!EnsureEditable())
        {
            return;
        }

        try
        {
            var docId = _services.Orders.CreateOutboundFromStock(_orderId.Value);
            var window = new OperationDetailsWindow(_services, docId)
            {
                Owner = this
            };
            window.ShowDialog();
            LoadOrder();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Заказы", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowShipments_Click(object sender, RoutedEventArgs e)
    {
        if (!_orderId.HasValue)
        {
            MessageBox.Show("Заказ не сохранен.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var docs = _services.Orders.GetOutboundDocs(_orderId.Value);
        if (docs.Count == 0)
        {
            MessageBox.Show("Отгрузок не найдено.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (docs.Count == 1)
        {
            var window = new OperationDetailsWindow(_services, docs[0].Id)
            {
                Owner = this
            };
            window.ShowDialog();
            LoadOrder();
            return;
        }

        var list = string.Join("\n", docs.Select(d => $"{d.DocRef} ({d.StatusDisplay})"));
        MessageBox.Show(list, "Отгрузки", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RefreshLineMetrics()
    {
        var availableByItem = _services.Orders.GetItemAvailability();
        var shippedByItem = _orderId.HasValue
            ? _services.Orders.GetShippedTotals(_orderId.Value)
            : new Dictionary<long, double>();

        foreach (var line in _lines)
        {
            var available = availableByItem.TryGetValue(line.ItemId, out var availableQty) ? availableQty : 0;
            var shipped = shippedByItem.TryGetValue(line.ItemId, out var shippedQty) ? shippedQty : 0;
            var remaining = Math.Max(0, line.QtyOrdered - shipped);
            var availableForShip = Math.Max(0, available);
            var canShip = Math.Min(remaining, availableForShip);
            var shortage = Math.Max(0, remaining - availableForShip);

            line.QtyAvailable = available;
            line.QtyShipped = shipped;
            line.QtyRemaining = remaining;
            line.CanShipNow = canShip;
            line.Shortage = shortage;
        }

        UpdateOutboundButtons();
        OrderLinesGrid.Items.Refresh();
    }

    private void UpdateOutboundButtons()
    {
        var canShip = _lines.Any(line => line.CanShipNow > 0);
        var orderSaved = _orderId.HasValue;
        var shipped = _order?.Status == OrderStatus.Shipped;
        CreateOutboundButton.IsEnabled = orderSaved && !shipped && canShip;
        ShowShipmentsButton.IsEnabled = orderSaved;
    }

    private bool EnsureEditable(bool showMessage = true)
    {
        if (_order != null && _order.Status == OrderStatus.Shipped)
        {
            if (showMessage)
            {
                MessageBox.Show("Отгруженный заказ нельзя редактировать.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return false;
        }

        return true;
    }

    private void SetEditingEnabled(bool enabled)
    {
        OrderRefBox.IsEnabled = enabled;
        PartnerCombo.IsEnabled = enabled;
        DueDatePicker.IsEnabled = enabled;
        StatusCombo.IsEnabled = enabled;
        CommentBox.IsEnabled = enabled;
        LineItemCombo.IsEnabled = enabled;
        LineQtyBox.IsEnabled = enabled;
        DeleteLineButton.IsEnabled = enabled && _selectedLine != null;
        SaveButton.IsEnabled = enabled;
        UpdateOutboundButtons();
    }

    private bool TryGetHeaderValues(out string orderRef, out long partnerId, out DateTime? dueDate, out OrderStatus status, out string? comment)
    {
        orderRef = OrderRefBox.Text ?? string.Empty;
        partnerId = 0;
        dueDate = DueDatePicker.SelectedDate;
        comment = CommentBox.Text;
        status = OrderStatus.Accepted;

        if (string.IsNullOrWhiteSpace(orderRef))
        {
            MessageBox.Show("Введите номер заказа.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (PartnerCombo.SelectedItem is not Partner partner)
        {
            MessageBox.Show("Выберите контрагента.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        partnerId = partner.Id;

        if (StatusCombo.SelectedItem is OrderStatusOption option)
        {
            status = option.Status;
        }

        if (status == OrderStatus.Shipped)
        {
            MessageBox.Show("Статус \"Отгружен\" ставится автоматически.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private static bool TryParseQty(string input, out double qty)
    {
        return double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out qty) && qty > 0;
    }

    private sealed class OrderStatusOption
    {
        public OrderStatusOption(OrderStatus status, string name)
        {
            Status = status;
            Name = name;
        }

        public OrderStatus Status { get; }
        public string Name { get; }
    }
}

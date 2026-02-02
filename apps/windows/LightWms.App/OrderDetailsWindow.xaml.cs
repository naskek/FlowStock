using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using LightWms.Core.Models;

namespace LightWms.App;

public partial class OrderDetailsWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<Partner> _partners = new();
    private readonly ObservableCollection<OrderLineView> _lines = new();
    private readonly List<OrderStatusOption> _statusOptions = new()
    {
        new OrderStatusOption(OrderStatus.Draft, OrderStatusMapper.StatusToDisplayName(OrderStatus.Draft)),
        new OrderStatusOption(OrderStatus.Accepted, OrderStatusMapper.StatusToDisplayName(OrderStatus.Accepted)),
        new OrderStatusOption(OrderStatus.InProgress, OrderStatusMapper.StatusToDisplayName(OrderStatus.InProgress))
    };
    private readonly List<OrderStatusOption> _statusOptionsAll = new()
    {
        new OrderStatusOption(OrderStatus.Draft, OrderStatusMapper.StatusToDisplayName(OrderStatus.Draft)),
        new OrderStatusOption(OrderStatus.Accepted, OrderStatusMapper.StatusToDisplayName(OrderStatus.Accepted)),
        new OrderStatusOption(OrderStatus.InProgress, OrderStatusMapper.StatusToDisplayName(OrderStatus.InProgress)),
        new OrderStatusOption(OrderStatus.Shipped, OrderStatusMapper.StatusToDisplayName(OrderStatus.Shipped))
    };

    private Order? _order;
    private OrderLineView? _selectedLine;
    private long? _orderId;
    private bool _isLoading;
    private bool _hasUnsavedChanges;
    private bool _allowCloseWithoutPrompt;

    public OrderDetailsWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        InitializeData();
        LoadPartners();
        PrepareNewOrder();
    }

    public OrderDetailsWindow(AppServices services, long orderId)
    {
        _services = services;
        _orderId = orderId;
        InitializeComponent();
        InitializeData();
        LoadPartners();
        LoadOrder();
    }

    private void InitializeData()
    {
        OrderLinesGrid.ItemsSource = _lines;
        PartnerCombo.ItemsSource = _partners;

        OrderRefBox.TextChanged += OrderHeaderChanged;
        PartnerCombo.SelectionChanged += OrderHeaderChanged;
        DueDatePicker.SelectedDateChanged += OrderHeaderChanged;
        StatusCombo.SelectionChanged += OrderHeaderChanged;
        CommentBox.TextChanged += OrderHeaderChanged;
    }

    private void LoadPartners()
    {
        _partners.Clear();
        foreach (var partner in _services.Catalog.GetPartners())
        {
            var status = _services.PartnerStatuses.GetStatus(partner.Id);
            if (status == PartnerStatus.Supplier)
            {
                continue;
            }

            _partners.Add(partner);
        }
    }

    private void PrepareNewOrder()
    {
        BeginLoad();
        Title = "Новый заказ";
        _order = null;
        _orderId = null;
        OrderRefBox.Text = GenerateNextOrderRef();
        PartnerCombo.SelectedItem = null;
        DueDatePicker.SelectedDate = null;
        CommentBox.Text = string.Empty;
        StatusCombo.ItemsSource = _statusOptions;
        StatusCombo.SelectedItem = _statusOptions[0];
        _lines.Clear();
        RefreshLineMetrics();
        SetEditingEnabled(true);
        SaveStatusText.Text = string.Empty;
        EndLoad();
    }

    private void LoadOrder()
    {
        if (!_orderId.HasValue)
        {
            PrepareNewOrder();
            return;
        }

        BeginLoad();
        _order = _services.Orders.GetOrder(_orderId.Value);
        if (_order == null)
        {
            MessageBox.Show("Заказ не найден.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
            EndLoad();
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

        SaveStatusText.Text = string.Empty;
        RefreshLineMetrics();
        SetEditingEnabled(!isShipped);
        EndLoad();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySaveOrder(showFeedback: false))
        {
            return;
        }

        _allowCloseWithoutPrompt = true;
        Close();
    }

    private bool TrySaveOrder(bool showFeedback)
    {
        if (!TryGetHeaderValues(out var orderRef, out var partnerId, out var dueDate, out var status, out var comment))
        {
            return false;
        }

        if (!TryValidateOrderRefUnique(orderRef))
        {
            return false;
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
            if (showFeedback)
            {
                SaveStatusText.Text = "Сохранено";
            }

            return true;
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Заказы", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void AddLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureEditable())
        {
            return;
        }

        var picker = new ItemPickerWindow(_services)
        {
            Owner = this
        };

        if (picker.ShowDialog() != true || picker.SelectedItem is not Item item)
        {
            return;
        }

        var packagings = _services.Packagings.GetPackagings(item.Id);
        var defaultUomCode = ResolveDefaultUomCode(item, packagings);
        var qtyDialog = new QuantityUomDialog(item.BaseUom, packagings, 1, defaultUomCode)
        {
            Owner = this
        };
        if (qtyDialog.ShowDialog() != true)
        {
            return;
        }

        var qtyBase = qtyDialog.QtyBase;
        var existing = _lines.FirstOrDefault(l => l.ItemId == item.Id);
        if (existing != null)
        {
            existing.QtyOrdered += qtyBase;
        }
        else
        {
            _lines.Add(new OrderLineView
            {
                ItemId = item.Id,
                ItemName = item.Name,
                QtyOrdered = qtyBase
            });
        }

        RefreshLineMetrics();
        MarkDirty();
    }

    private void EditLine_Click(object sender, RoutedEventArgs e)
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

        var item = _services.DataStore.FindItemById(_selectedLine.ItemId);
        if (item == null)
        {
            MessageBox.Show("Товар не найден.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var packagings = _services.Packagings.GetPackagings(item.Id);
        var defaultUomCode = ResolveDefaultUomCode(item, packagings);
        var qtyDialog = new QuantityUomDialog(item.BaseUom, packagings, _selectedLine.QtyOrdered, defaultUomCode)
        {
            Owner = this
        };
        if (qtyDialog.ShowDialog() != true)
        {
            return;
        }

        _selectedLine.QtyOrdered = qtyDialog.QtyBase;
        RefreshLineMetrics();
        MarkDirty();
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
        MarkDirty();
    }

    private void OrderLinesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedLine = OrderLinesGrid.SelectedItem as OrderLineView;
        DeleteLineButton.IsEnabled = _selectedLine != null && EnsureEditable(false);
        EditLineButton.IsEnabled = _selectedLine != null && EnsureEditable(false);
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

        UpdateEmptyState();
        OrderLinesGrid.Items.Refresh();
    }

    private void UpdateEmptyState()
    {
        OrderLinesEmptyText.Visibility = _lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        AddItemButton.IsEnabled = enabled;
        EditLineButton.IsEnabled = enabled && _selectedLine != null;
        DeleteLineButton.IsEnabled = enabled && _selectedLine != null;
        SaveButton.IsEnabled = enabled;
    }

    private void OrderHeaderChanged(object? sender, RoutedEventArgs e)
    {
        MarkDirty();
    }

    private void BeginLoad()
    {
        _isLoading = true;
    }

    private void EndLoad()
    {
        _isLoading = false;
        _hasUnsavedChanges = false;
    }

    private void MarkDirty()
    {
        if (_isLoading)
        {
            return;
        }

        _hasUnsavedChanges = true;
        SaveStatusText.Text = string.Empty;
    }

    private bool TryGetHeaderValues(out string orderRef, out long partnerId, out DateTime? dueDate, out OrderStatus status, out string? comment)
    {
        orderRef = OrderRefBox.Text ?? string.Empty;
        partnerId = 0;
        dueDate = DueDatePicker.SelectedDate;
        comment = CommentBox.Text;
        status = OrderStatus.Draft;

        if (string.IsNullOrWhiteSpace(orderRef))
        {
            MessageBox.Show("Введите номер заказа.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (PartnerCombo.SelectedItem is not Partner partner)
        {
            if (_order?.PartnerId is long existingPartnerId && IsSupplierPartner(existingPartnerId))
            {
                MessageBox.Show("В заказе нельзя выбрать контрагента со статусом \"Поставщик\".", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            MessageBox.Show("Выберите контрагента.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (IsSupplierPartner(partner.Id))
        {
            MessageBox.Show("В заказе нельзя выбрать контрагента со статусом \"Поставщик\".", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private bool IsSupplierPartner(long partnerId)
    {
        return _services.PartnerStatuses.GetStatus(partnerId) == PartnerStatus.Supplier;
    }

    private bool TryValidateOrderRefUnique(string orderRef)
    {
        var normalized = orderRef.Trim();
        var duplicate = _services.Orders.GetOrders()
            .FirstOrDefault(order => string.Equals(order.OrderRef, normalized, StringComparison.OrdinalIgnoreCase)
                                     && (!_orderId.HasValue || order.Id != _orderId.Value));
        if (duplicate == null)
        {
            return true;
        }

        MessageBox.Show($"Заказ с номером {normalized} уже существует. Продолжить нельзя.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!TryConfirmClose())
        {
            return;
        }

        _allowCloseWithoutPrompt = true;
        Close();
    }

    private void OrderDetailsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowCloseWithoutPrompt)
        {
            return;
        }

        if (!TryConfirmClose())
        {
            e.Cancel = true;
        }
    }

    private bool TryConfirmClose()
    {
        if (!_hasUnsavedChanges)
        {
            return true;
        }

        var result = MessageBox.Show("Сохранить изменения?", "Заказы", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (result == MessageBoxResult.No)
        {
            return true;
        }

        return TrySaveOrder(showFeedback: true);
    }

    private string GenerateNextOrderRef()
    {
        var max = 0L;
        foreach (var order in _services.Orders.GetOrders())
        {
            var orderRef = order.OrderRef?.Trim();
            if (string.IsNullOrWhiteSpace(orderRef) || !IsDigitsOnly(orderRef))
            {
                continue;
            }

            if (long.TryParse(orderRef, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > max)
            {
                max = value;
            }
        }

        return (max + 1).ToString("D3", CultureInfo.InvariantCulture);
    }

    private static bool IsDigitsOnly(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!char.IsDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static string ResolveDefaultUomCode(Item item, IReadOnlyList<ItemPackaging> packagings)
    {
        if (item.DefaultPackagingId.HasValue)
        {
            var packaging = packagings.FirstOrDefault(p => p.Id == item.DefaultPackagingId.Value);
            if (packaging != null)
            {
                return packaging.Code;
            }
        }

        return "BASE";
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

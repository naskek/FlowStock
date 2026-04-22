using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class OrderDetailsWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<Partner> _partners = new();
    private readonly List<Partner> _partnersAll = new();
    private readonly ObservableCollection<OrderLineView> _lines = new();
    private readonly List<OrderTypeOption> _typeOptions = new()
    {
        new OrderTypeOption(OrderType.Customer, "Клиентский заказ"),
        new OrderTypeOption(OrderType.Internal, "Внутренний заказ на выпуск")
    };

    private Order? _order;
    private OrderLineView? _selectedLine;
    private long? _orderId;
    private bool _isLoading;
    private bool _hasUnsavedChanges;
    private bool _allowCloseWithoutPrompt;
    private bool _suppressPartnerFilter;

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
        TypeCombo.ItemsSource = _typeOptions;

        OrderRefBox.TextChanged += OrderHeaderChanged;
        TypeCombo.SelectionChanged += TypeCombo_SelectionChanged;
        PartnerCombo.SelectionChanged += OrderHeaderChanged;
        PartnerCombo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler(PartnerCombo_TextChanged));
        DueDatePicker.SelectedDateChanged += OrderHeaderChanged;
        CommentBox.TextChanged += OrderHeaderChanged;
    }

    private void LoadPartners()
    {
        _partnersAll.Clear();
        _partners.Clear();
        if (_services.WpfPartnerApi.TryGetPartners(out var apiPartners))
        {
            foreach (var entry in apiPartners)
            {
                if (entry.Status == PartnerStatus.Supplier)
                {
                    continue;
                }

                _partnersAll.Add(entry.Partner);
            }
            ApplyPartnerFilter();
            return;
        }

        var partners = _services.WpfReadApi.TryGetPartners(out var readApiPartners)
            ? readApiPartners
            : Array.Empty<Partner>();
        foreach (var partner in partners)
        {
            _partnersAll.Add(partner);
        }

        ApplyPartnerFilter();
    }

    private void ApplyPartnerFilter(string? query = null, long? forceIncludePartnerId = null)
    {
        var normalized = NormalizePartnerSearch(query);
        var selectedPartnerId = forceIncludePartnerId ?? (PartnerCombo.SelectedItem as Partner)?.Id;

        _suppressPartnerFilter = true;
        try
        {
            _partners.Clear();
            foreach (var partner in _partnersAll)
            {
                if (selectedPartnerId.HasValue && partner.Id == selectedPartnerId.Value
                    || PartnerMatchesSearch(partner, normalized))
                {
                    _partners.Add(partner);
                }
            }
        }
        finally
        {
            _suppressPartnerFilter = false;
        }
    }

    private void PartnerCombo_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || _suppressPartnerFilter || !PartnerCombo.IsKeyboardFocusWithin)
        {
            return;
        }

        var text = PartnerCombo.Text ?? string.Empty;
        ApplyPartnerFilter(text);
        PartnerCombo.IsDropDownOpen = !string.IsNullOrWhiteSpace(text) && _partners.Count > 0;
        RestoreComboText(PartnerCombo, text);
    }

    private void PrepareNewOrder()
    {
        BeginLoad();
        Title = "Новый заказ";
        _order = null;
        _orderId = null;
        OrderRefBox.Text = _services.WpfReadApi.TryGenerateNextOrderRef(out var orderRef)
            ? orderRef
            : GenerateNextOrderRef();
        TypeCombo.SelectedItem = _typeOptions.First(option => option.Type == OrderType.Customer);
        PartnerCombo.SelectedItem = null;
        DueDatePicker.SelectedDate = null;
        CommentBox.Text = string.Empty;
        OrderStatusText.Text = OrderStatusMapper.StatusToDisplayName(OrderStatus.Draft, OrderType.Customer);
        _lines.Clear();
        UpdateTypeUi();
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
        _order = _services.WpfReadApi.TryGetOrder(_orderId.Value, out var apiOrder)
            ? apiOrder
            : null;
        if (_order == null)
        {
            MessageBox.Show("Заказ не найден.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
            EndLoad();
            Close();
            return;
        }

        Title = $"Заказ: {_order.OrderRef}";
        OrderRefBox.Text = _order.OrderRef;
        TypeCombo.SelectedItem = _typeOptions.FirstOrDefault(option => option.Type == _order.Type)
                                ?? _typeOptions.First();
        PartnerCombo.SelectedItem = _order.PartnerId.HasValue
            ? _partnersAll.FirstOrDefault(p => p.Id == _order.PartnerId.Value)
            : null;
        DueDatePicker.SelectedDate = _order.DueDate;
        CommentBox.Text = _order.Comment ?? string.Empty;

        var isShipped = _order.Status == OrderStatus.Shipped;
        OrderStatusText.Text = OrderStatusMapper.StatusToDisplayName(_order.Status, _order.Type);

        _lines.Clear();
        var lines = _services.WpfReadApi.TryGetOrderLines(_order.Id, out var apiLines)
            ? apiLines
            : Array.Empty<OrderLineView>();
        foreach (var line in lines)
        {
            _lines.Add(line);
        }

        SaveStatusText.Text = string.Empty;
        UpdateTypeUi();
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
        if (!TryGetHeaderValues(allowBlankOrderRef: false, out var orderRef, out var type, out var partnerId, out var dueDate, out var comment))
        {
            return false;
        }

        try
        {
            if (_orderId.HasValue)
            {
                return TryUpdateOrderViaServer(_orderId.Value, orderRef, type, partnerId, dueDate, comment, showFeedback);
            }

            return TryCreateOrderViaServer(orderRef, type, partnerId, dueDate, comment, showFeedback);
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

    private bool TryUpdateOrderViaServer(
        long orderId,
        string orderRef,
        OrderType type,
        long? partnerId,
        DateTime? dueDate,
        string? comment,
        bool showFeedback)
    {
        var result = _services.WpfUpdateOrders.UpdateOrderAsync(
                new WpfUpdateOrderContext(
                    orderId,
                    string.IsNullOrWhiteSpace(orderRef) ? null : orderRef,
                    type,
                    partnerId,
                    dueDate,
                    OrderStatus.InProgress,
                    comment,
                    _lines.ToList()))
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

        if (!result.IsSuccess)
        {
            var icon = result.Kind is WpfUpdateOrderResultKind.Timeout or WpfUpdateOrderResultKind.ServerUnavailable
                ? MessageBoxImage.Error
                : MessageBoxImage.Warning;
            MessageBox.Show(result.Message, "Заказы", MessageBoxButton.OK, icon);
            return false;
        }

        if (result.Response == null || result.Response.OrderId <= 0)
        {
            MessageBox.Show("Сервер вернул неполный ответ при обновлении заказа.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _orderId = result.Response.OrderId;
        LoadOrder();

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            MessageBox.Show(result.Message, "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        if (showFeedback)
        {
            SaveStatusText.Text = "Сохранено";
        }

        return true;
    }

    private bool TryCreateOrderViaServer(
        string orderRef,
        OrderType type,
        long? partnerId,
        DateTime? dueDate,
        string? comment,
        bool showFeedback)
    {
        var result = _services.WpfCreateOrders.CreateOrderAsync(
                new WpfCreateOrderContext(
                    string.IsNullOrWhiteSpace(orderRef) ? null : orderRef,
                    type,
                    partnerId,
                    dueDate,
                    OrderStatus.InProgress,
                    comment,
                    _lines.ToList()))
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

        if (!result.IsSuccess)
        {
            var icon = result.Kind is WpfCreateOrderResultKind.Timeout or WpfCreateOrderResultKind.ServerUnavailable
                ? MessageBoxImage.Error
                : MessageBoxImage.Warning;
            MessageBox.Show(result.Message, "Заказы", MessageBoxButton.OK, icon);
            return false;
        }

        if (result.Response == null || result.Response.OrderId <= 0)
        {
            MessageBox.Show("Сервер вернул неполный ответ при создании заказа.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _orderId = result.Response.OrderId;
        LoadOrder();

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            MessageBox.Show(result.Message, "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        if (showFeedback)
        {
            SaveStatusText.Text = "Сохранено";
        }

        return true;
    }

    private void AddLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureEditable())
        {
            return;
        }

        var picker = new ItemPickerWindow(_services)
        {
            Owner = this,
            KeepOpenOnSelect = true
        };
        picker.ItemPicked += (_, item) => AddOrderLine(item, picker);
        picker.ShowDialog();
    }

    private void AddOrderLine(Item item, Window owner)
    {
        var packagings = _services.WpfPackagingApi.TryGetPackagings(item.Id, includeInactive: false, out var apiPackagings)
            ? apiPackagings
            : Array.Empty<ItemPackaging>();
        var defaultUomCode = ResolveDefaultUomCode(item, packagings);
        var qtyDialog = new QuantityUomDialog(item.BaseUom, packagings, 1, defaultUomCode)
        {
            Owner = owner
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
                Barcode = item.Barcode,
                Gtin = item.Gtin,
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

        var item = (_services.WpfReadApi.TryGetItems(null, out var apiItems) ? apiItems : Array.Empty<Item>())
            .FirstOrDefault(candidate => candidate.Id == _selectedLine.ItemId);
        if (item == null)
        {
            MessageBox.Show("Товар не найден.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var packagings = _services.WpfPackagingApi.TryGetPackagings(item.Id, includeInactive: false, out var apiPackagings)
            ? apiPackagings
            : Array.Empty<ItemPackaging>();
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

    private void OrderLinesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_selectedLine == null || !EnsureEditable(false))
        {
            return;
        }

        EditLine_Click(sender, new RoutedEventArgs());
    }

    private void RefreshLineMetrics()
    {
        var type = GetSelectedOrderType();
        if (_orderId.HasValue && !_hasUnsavedChanges && TryRefreshPersistedOrderLineMetricsFromApi(type))
        {
            return;
        }

        var availableByItem = _services.WpfReadApi.TryGetItemAvailability(out var apiAvailability)
            ? apiAvailability
            : new Dictionary<long, double>();
        var processedByLine = new Dictionary<long, double>();

        if (_orderId.HasValue)
        {
            if (type == OrderType.Internal)
            {
                var receiptRemaining = _services.WpfReadApi.TryGetOrderReceiptRemaining(_orderId.Value, out var apiReceipt)
                    ? apiReceipt
                    : Array.Empty<OrderReceiptLine>();
                processedByLine = receiptRemaining.ToDictionary(line => line.OrderLineId, line => line.QtyReceived);
            }
            else
            {
                if (_services.WpfReadApi.TryGetOrderLines(_orderId.Value, out var apiLines))
                {
                    processedByLine = apiLines.ToDictionary(line => line.Id, line => line.QtyShipped);
                }
            }
        }

        foreach (var line in _lines)
        {
            var available = availableByItem.TryGetValue(line.ItemId, out var availableQty) ? availableQty : 0;
            var processed = processedByLine.TryGetValue(line.Id, out var processedQty) ? processedQty : 0;
            var remaining = Math.Max(0, line.QtyOrdered - processed);

            line.QtyAvailable = available;
            line.QtyProduced = type == OrderType.Internal ? processed : 0;
            line.QtyShipped = processed;
            line.QtyRemaining = remaining;

            if (type == OrderType.Internal)
            {
                line.CanShipNow = 0;
                line.Shortage = 0;
                continue;
            }

            var availableForShip = Math.Max(0, available);
            line.CanShipNow = Math.Min(remaining, availableForShip);
            line.Shortage = Math.Max(0, remaining - availableForShip);
        }

        UpdateEmptyState();
        OrderLinesGrid.Items.Refresh();
    }

    private bool TryRefreshPersistedOrderLineMetricsFromApi(OrderType type)
    {
        if (!_orderId.HasValue || !_services.WpfReadApi.TryGetOrderLines(_orderId.Value, out var apiLines))
        {
            return false;
        }

        var metricsByLine = apiLines.ToDictionary(line => line.Id, line => line);
        foreach (var line in _lines)
        {
            if (!metricsByLine.TryGetValue(line.Id, out var persisted))
            {
                return false;
            }

            line.QtyAvailable = persisted.QtyAvailable;
            line.QtyProduced = persisted.QtyProduced;
            line.QtyShipped = persisted.QtyShipped;
            line.QtyRemaining = persisted.QtyRemaining;
            line.CanShipNow = type == OrderType.Internal ? 0 : persisted.CanShipNow;
            line.Shortage = type == OrderType.Internal ? 0 : persisted.Shortage;
        }

        UpdateEmptyState();
        OrderLinesGrid.Items.Refresh();
        return true;
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
                MessageBox.Show($"{OrderStatusMapper.StatusToDisplayName(OrderStatus.Shipped, _order.Type)} заказ нельзя редактировать.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return false;
        }

        return true;
    }

    private void SetEditingEnabled(bool enabled)
    {
        OrderRefBox.IsEnabled = false;
        DueDatePicker.IsEnabled = enabled;
        CommentBox.IsEnabled = enabled;
        AddItemButton.IsEnabled = enabled;
        EditLineButton.IsEnabled = enabled && _selectedLine != null;
        DeleteLineButton.IsEnabled = enabled && _selectedLine != null;
        SaveButton.IsEnabled = enabled;
        UpdateTypeUi();
    }

    private void UpdateTypeUi()
    {
        var type = GetSelectedOrderType();
        var canEdit = _order?.Status != OrderStatus.Shipped;

        TypeCombo.IsEnabled = canEdit && !_orderId.HasValue;
        PartnerCombo.IsEnabled = canEdit && type == OrderType.Customer;

        if (type == OrderType.Internal)
        {
            if (PartnerCombo.SelectedItem != null)
            {
                PartnerCombo.SelectedItem = null;
            }
            if (!string.IsNullOrWhiteSpace(PartnerCombo.Text))
            {
                PartnerCombo.Text = string.Empty;
            }
        }

        OrderTypeHintText.Text = type == OrderType.Internal
            ? "Внутренний заказ на выпуск. Контрагент не нужен, закрывается по проведенным PRD."
            : "Клиентский заказ. Закрывается по проведенным отгрузкам OUT.";

        ProcessedQtyColumn.Header = type == OrderType.Internal ? "Выпущено" : "Отгружено";
        ProcessedQtyColumn.Binding = new System.Windows.Data.Binding(type == OrderType.Internal ? nameof(OrderLineView.QtyProduced) : nameof(OrderLineView.QtyShipped));
        AvailableQtyColumn.Header = type == OrderType.Internal ? "В наличии ГП" : "В наличии";
        CanShipNowColumn.Visibility = type == OrderType.Internal ? Visibility.Collapsed : Visibility.Visible;
        ShortageColumn.Visibility = type == OrderType.Internal ? Visibility.Collapsed : Visibility.Visible;
    }

    private OrderType GetSelectedOrderType()
    {
        return (TypeCombo.SelectedItem as OrderTypeOption)?.Type
               ?? _order?.Type
               ?? OrderType.Customer;
    }

    private void OrderHeaderChanged(object? sender, RoutedEventArgs e)
    {
        MarkDirty();
    }

    private void TypeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_order == null)
        {
            OrderStatusText.Text = OrderStatusMapper.StatusToDisplayName(OrderStatus.Draft, GetSelectedOrderType());
        }
        else
        {
            OrderStatusText.Text = OrderStatusMapper.StatusToDisplayName(_order.Status, GetSelectedOrderType());
        }
        UpdateTypeUi();
        RefreshLineMetrics();
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

    private bool TryGetHeaderValues(bool allowBlankOrderRef, out string orderRef, out OrderType type, out long? partnerId, out DateTime? dueDate, out string? comment)
    {
        orderRef = OrderRefBox.Text ?? string.Empty;
        type = GetSelectedOrderType();
        partnerId = null;
        dueDate = DueDatePicker.SelectedDate;
        comment = CommentBox.Text;

        if (!allowBlankOrderRef && string.IsNullOrWhiteSpace(orderRef))
        {
            MessageBox.Show("Введите номер заказа.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (type == OrderType.Customer)
        {
            var partner = FindPartnerByQuery(PartnerCombo.Text)
                          ?? (string.IsNullOrWhiteSpace(PartnerCombo.Text) ? PartnerCombo.SelectedItem as Partner : null);
            if (partner == null)
            {
                if (_order?.PartnerId is long existingPartnerId && IsSupplierPartner(existingPartnerId))
                {
                    MessageBox.Show("В заказе нельзя выбрать контрагента со статусом \"Поставщик\".", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                MessageBox.Show("Выберите контрагента.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            PartnerCombo.SelectedItem = partner;

            if (IsSupplierPartner(partner.Id))
            {
                MessageBox.Show("В заказе нельзя выбрать контрагента со статусом \"Поставщик\".", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            partnerId = partner.Id;
        }

        return true;
    }

    private Partner? FindPartnerByQuery(string? query)
    {
        var normalized = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var matches = _partners
            .Concat(_partnersAll)
            .GroupBy(partner => partner.Id)
            .Select(group => group.First())
            .Where(partner => string.Equals(partner.DisplayName, normalized, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(partner.Name, normalized, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(partner.Code, normalized, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string NormalizePartnerSearch(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    private static bool PartnerMatchesSearch(Partner partner, string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return true;
        }

        return ContainsPartnerText(partner.DisplayName, normalizedQuery)
               || ContainsPartnerText(partner.Name, normalizedQuery)
               || ContainsPartnerText(partner.Code, normalizedQuery);
    }

    private static bool ContainsPartnerText(string? value, string normalizedQuery)
    {
        return !string.IsNullOrWhiteSpace(value)
               && value.Trim().ToLowerInvariant().Contains(normalizedQuery, StringComparison.Ordinal);
    }

    private static void RestoreComboText(System.Windows.Controls.ComboBox comboBox, string text)
    {
        comboBox.Dispatcher.BeginInvoke(() =>
        {
            if (comboBox.Template.FindName("PART_EditableTextBox", comboBox) is System.Windows.Controls.TextBox textBox)
            {
                if (!string.Equals(textBox.Text, text, StringComparison.Ordinal))
                {
                    textBox.Text = text;
                }

                textBox.CaretIndex = textBox.Text.Length;
            }
        });
    }

    private bool IsSupplierPartner(long partnerId)
    {
        if (_services.WpfPartnerApi.TryGetPartners(out var apiPartners))
        {
            return apiPartners.FirstOrDefault(entry => entry.Partner.Id == partnerId)?.Status == PartnerStatus.Supplier;
        }

        return false;
    }

    private bool TryValidateOrderRefUnique(string orderRef)
    {
        var normalized = orderRef.Trim();
        var orders = _services.WpfReadApi.TryGetOrders(includeInternal: true, search: null, out var apiOrders)
            ? apiOrders
            : Array.Empty<Order>();
        var duplicate = orders
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
        var orders = _services.WpfReadApi.TryGetOrders(includeInternal: true, search: null, out var apiOrders)
            ? apiOrders
            : Array.Empty<Order>();
        foreach (var order in orders)
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

    private sealed class OrderTypeOption
    {
        public OrderTypeOption(OrderType type, string name)
        {
            Type = type;
            Name = name;
        }

        public OrderType Type { get; }
        public string Name { get; }
    }
}


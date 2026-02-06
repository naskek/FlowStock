using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class OperationDetailsWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<Location> _locations = new();
    private readonly ObservableCollection<Partner> _partners = new();
    private readonly List<Partner> _partnersAll = new();
    private readonly ObservableCollection<DocLineDisplay> _docLines = new();
    private readonly ObservableCollection<OrderOption> _orders = new();
    private readonly List<OrderOption> _ordersAll = new();
    private readonly ObservableCollection<HuOption> _huToOptions = new();
    private readonly ObservableCollection<HuOption> _huFromOptions = new();
    private readonly Dictionary<long, double> _orderedQtyByItem = new();
    private readonly long _docId;
    private Doc? _doc;
    private DocLineDisplay? _selectedDocLine;
    private bool _suppressOrderSync;
    private bool _suppressPartialSync;
    private bool _suppressDirtyTracking;
    private bool _isPartialShipment;
    private bool _hasUnsavedChanges;
    private bool _hasOutboundShortage;
    private int _outboundShortageCount;

    public OperationDetailsWindow(AppServices services, long docId)
    {
        _services = services;
        _docId = docId;
        InitializeComponent();

        DocLinesGrid.ItemsSource = _docLines;
        DocFromCombo.ItemsSource = _locations;
        DocToCombo.ItemsSource = _locations;
        DocPartnerCombo.ItemsSource = _partners;
        DocPartnerCombo.SelectionChanged += DocPartnerCombo_SelectionChanged;
        DocOrderCombo.ItemsSource = _orders;
        DocHuCombo.ItemsSource = _huToOptions;
        DocHuFromCombo.ItemsSource = _huFromOptions;
        DocFromCombo.SelectionChanged += DocFromCombo_SelectionChanged;
        DocToCombo.SelectionChanged += DocToCombo_SelectionChanged;

        LoadCatalog();
        LoadOrders();
        LoadDoc();
    }

    private void OperationDetailsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter)
        {
            e.Handled = true;
            TryCloseCurrentDoc();
        }
    }

    private void LoadCatalog()
    {
        _locations.Clear();
        foreach (var location in _services.Catalog.GetLocations())
        {
            _locations.Add(location);
        }

        _partnersAll.Clear();
        foreach (var partner in _services.Catalog.GetPartners())
        {
            _partnersAll.Add(partner);
        }
        ApplyPartnerFilter();
    }

    private void LoadOrders()
    {
        _ordersAll.Clear();
        foreach (var order in _services.Orders.GetOrders())
        {
            if (order.Status == OrderStatus.Shipped)
            {
                continue;
            }

            _ordersAll.Add(new OrderOption(order.Id, order.OrderRef, order.PartnerId, order.PartnerDisplay));
        }

        RefreshOrderList();
    }

    private void RefreshOrderList()
    {
        _orders.Clear();
        var partnerId = (DocPartnerCombo.SelectedItem as Partner)?.Id;
        foreach (var order in _ordersAll)
        {
            if (partnerId.HasValue && order.PartnerId != partnerId.Value)
            {
                continue;
            }

            _orders.Add(order);
        }
    }

    private void LoadDoc()
    {
        _doc = _services.Documents.GetDoc(_docId);
        if (_doc == null)
        {
            MessageBox.Show("Операция не найдена.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
            return;
        }

        Title = $"Операция: {_doc.DocRef} ({DocTypeMapper.ToDisplayName(_doc.Type)})";
        LoadDocLines();
        UpdateDocView();
    }

    private void LoadDocLines()
    {
        _docLines.Clear();
        var locationLookup = _locations
            .GroupBy(location => location.Code)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var packagingLookup = new Dictionary<long, IReadOnlyList<ItemPackaging>>();

        var lines = _services.Documents.GetDocLines(_docId);
        var isOutbound = _doc?.Type == DocType.Outbound;
        var isInventory = _doc?.Type == DocType.Inventory;
        var availableByItem = isOutbound
            ? _services.Orders.GetItemAvailability()
            : new Dictionary<long, double>();
        var requiredByItem = isOutbound
            ? lines.Where(line => line.Qty > 0)
                .GroupBy(line => line.ItemId)
                .ToDictionary(group => group.Key, group => group.Sum(line => line.Qty))
            : new Dictionary<long, double>();
        var shortageByItem = new Dictionary<long, double>();
        if (isOutbound)
        {
            foreach (var entry in requiredByItem)
            {
                var available = availableByItem.TryGetValue(entry.Key, out var qty) ? qty : 0;
                var shortage = entry.Value - available;
                if (shortage > 0)
                {
                    shortageByItem[entry.Key] = shortage;
                }
            }
        }

        foreach (var line in lines)
        {
            var baseUom = string.IsNullOrWhiteSpace(line.BaseUom) ? "шт" : line.BaseUom;
            var packagings = GetPackagings(line.ItemId, packagingLookup);
            var selectedPackaging = ResolvePackaging(packagings, line.UomCode);
            var inputQty = ResolveInputQty(line, selectedPackaging);
            var hasShortage = isOutbound && shortageByItem.ContainsKey(line.ItemId);
            var inventoryDbQty = (double?)null;
            var inventoryDiffQty = (double?)null;
            var hasInventoryDiff = false;
            if (isInventory)
            {
                var locationId = ResolveLocationId(line.ToLocation, locationLookup);
                if (locationId.HasValue)
                {
                    var huCode = NormalizeHuValue(line.ToHu);
                    inventoryDbQty = _services.DataStore.GetAvailableQty(line.ItemId, locationId.Value, huCode);
                    inventoryDiffQty = line.Qty - inventoryDbQty.Value;
                    hasInventoryDiff = Math.Abs(inventoryDiffQty.Value) > 0.000001;
                }
            }
            _docLines.Add(new DocLineDisplay
            {
                Id = line.Id,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                QtyBase = line.Qty,
                QtyInput = line.QtyInput,
                UomCode = line.UomCode,
                BaseUom = line.BaseUom,
                InputQtyDisplay = FormatQty(inputQty),
                InputUomDisplay = FormatInputUomDisplay(line.UomCode, baseUom, selectedPackaging),
                BaseQtyDisplay = FormatBaseQty(line),
                AvailableQty = isOutbound
                    ? (availableByItem.TryGetValue(line.ItemId, out var qty) ? qty : 0)
                    : null,
                HasShortage = hasShortage,
                ShortageQty = hasShortage ? shortageByItem[line.ItemId] : null,
                InventoryDbQty = inventoryDbQty,
                InventoryDiffQty = inventoryDiffQty,
                InventoryDbQtyDisplay = inventoryDbQty.HasValue ? FormatQty(inventoryDbQty.Value) : string.Empty,
                InventoryDiffQtyDisplay = inventoryDiffQty.HasValue ? FormatQty(inventoryDiffQty.Value) : string.Empty,
                HasInventoryDiff = hasInventoryDiff,
                FromLocation = FormatLocationDisplay(line.FromLocation, locationLookup),
                ToLocation = FormatLocationDisplay(line.ToLocation, locationLookup)
            });
        }

        _hasOutboundShortage = isOutbound && shortageByItem.Count > 0;
        _outboundShortageCount = isOutbound ? shortageByItem.Count : 0;
        _selectedDocLine = null;
        UpdateLineButtons();
        UpdateAvailabilityStatus();
        UpdateActionButtons();
    }

    private static string? FormatLocationDisplay(string? code, IReadOnlyDictionary<string, Location> lookup)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return lookup.TryGetValue(code, out var location) ? location.DisplayName : code;
    }

    private static long? ResolveLocationId(string? code, IReadOnlyDictionary<string, Location> lookup)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return lookup.TryGetValue(code, out var location) ? location.Id : null;
    }

    private void DocLines_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedDocLine = DocLinesGrid.SelectedItem as DocLineDisplay;
        UpdateLineButtons();
    }

    private void DocLinesGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            if (!IsDocEditable())
            {
                return;
            }
            if (HasOrderBinding())
            {
                return;
            }
            DocDeleteLine_Click(sender, new RoutedEventArgs());
        }
    }

    private void DocClose_Click(object sender, RoutedEventArgs e)
    {
        TryCloseCurrentDoc();
    }

    private void DocRecount_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null)
        {
            MessageBox.Show("Операция не выбрана.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_doc.Status != DocStatus.Draft)
        {
            MessageBox.Show("Операция уже закрыта.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_doc.Type != DocType.Inventory)
        {
            MessageBox.Show("На пересчет можно отправить только инвентаризацию.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            "Отправить инвентаризацию на пересчет? ТСД сможет дополнить или уточнить строки.",
            "Инвентаризация",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _services.Documents.MarkDocForRecount(_doc.Id);
            MessageBox.Show("Инвентаризация отправлена на пересчет.", "Инвентаризация", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Инвентаризация", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TryCloseCurrentDoc()
    {
        if (_doc == null)
        {
            MessageBox.Show("Операция не выбрана.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_doc.Status == DocStatus.Closed)
        {
            MessageBox.Show("Операция уже закрыта.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_doc.IsRecountRequested)
        {
            MessageBox.Show("Операция находится на перерасчете. Дождитесь данных от ТСД.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_hasUnsavedChanges && !TrySaveHeader())
        {
            return;
        }

        var doc = _doc;
        if (doc == null)
        {
            MessageBox.Show("Операция не выбрана.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_services.Documents.GetDocLines(doc.Id).Count == 0)
        {
            MessageBox.Show(
                "Добавьте хотя бы один товар в документ перед проведением.",
                "Операция",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (IsPartnerRequired() && doc.PartnerId == null)
        {
            MessageBox.Show("Выберите контрагента.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (doc.Type == DocType.Outbound && !TryValidateOutboundStock(doc.Id))
        {
            return;
        }

        CloseDocResult result;
        try
        {
            result = _services.Documents.TryCloseDoc(doc.Id, allowNegative: false);
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Doc close failed", ex);
            MessageBox.Show(
                "Не удалось провести операцию. Проверьте доступ к базе данных и повторите.",
                "Операция",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }
        if (result.Errors.Count > 0)
        {
            MessageBox.Show(string.Join("\n", result.Errors), "Проверка операции", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (doc.Type == DocType.Outbound && result.Warnings.Count > 0)
        {
            MessageBox.Show(string.Join("\n", result.Warnings), "Недостаточно товара", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (result.Warnings.Count > 0)
        {
            var warningText = "Остаток уйдет в минус:\n" + string.Join("\n", result.Warnings) + "\n\nЗакрыть операцию?";
            var confirm = MessageBox.Show(warningText, "Предупреждение", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                result = _services.Documents.TryCloseDoc(doc.Id, allowNegative: true);
            }
            catch (Exception ex)
            {
                _services.AppLogger.Error("Doc close failed (allow negative)", ex);
                MessageBox.Show(
                    "Не удалось провести операцию. Проверьте доступ к базе данных и повторите.",
                    "Операция",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
            if (!result.Success)
            {
                if (result.Errors.Count > 0)
                {
                    MessageBox.Show(string.Join("\n", result.Errors), "Проверка операции", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }
        }

        if (!result.Success)
        {
            return;
        }

        Close();
    }

    private void DocAddLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDraftDocSelected())
        {
            return;
        }

        if (_hasUnsavedChanges && !TrySaveHeader())
        {
            return;
        }

        if (HasOrderBinding())
        {
            MessageBox.Show("Нельзя добавлять строки вручную, когда выбран заказ.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IEnumerable<Item>? filteredItems = null;
        if (_doc?.Type == DocType.Move)
        {
            if (DocFromCombo.SelectedItem is not Location selectedFromLocation)
            {
                MessageBox.Show("Выберите место хранения (откуда).", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedFromHu = (DocHuFromCombo.SelectedItem as HuOption)?.Code;
            filteredItems = _services.DataStore.GetItemsByLocationAndHu(selectedFromLocation.Id, NormalizeHuValue(selectedFromHu));
            if (!filteredItems.Any())
            {
                MessageBox.Show("Нет доступных товаров для выбранного места хранения и HU.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        var picker = new ItemPickerWindow(_services, filteredItems)
        {
            Owner = this
        };
        if (picker.ShowDialog() != true || picker.SelectedItem is not Item item)
        {
            return;
        }

        var packagings = _services.Packagings.GetPackagings(item.Id);
        var defaultUomCode = ResolveDefaultUomCode(item, packagings);
        var (availableQty, showAvailableLabel) = GetAvailableQtyForDialog(item.Id);
        var qtyDialog = new QuantityUomDialog(item.BaseUom, packagings, 1, defaultUomCode, availableQty, showAvailableLabel)
        {
            Owner = this
        };
        if (qtyDialog.ShowDialog() != true)
        {
            return;
        }

        var qtyBase = qtyDialog.QtyBase;
        var qtyInput = qtyDialog.QtyInput;
        var uomCode = qtyDialog.UomCode;
        if (!TryGetLineLocations(out var fromLocation, out var toLocation, out var fromHu, out var toHu))
        {
            return;
        }

        try
        {
            var existing = _services.DataStore.GetDocLines(_doc!.Id)
                .FirstOrDefault(line => line.ItemId == item.Id
                                        && line.FromLocationId == fromLocation?.Id
                                        && line.ToLocationId == toLocation?.Id
                                        && string.Equals(NormalizeHuValue(line.FromHu), NormalizeHuValue(fromHu), StringComparison.OrdinalIgnoreCase)
                                        && string.Equals(NormalizeHuValue(line.ToHu), NormalizeHuValue(toHu), StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                var sameUom = IsSameUom(existing.UomCode, uomCode);
                var mergedInput = sameUom
                    ? (existing.QtyInput ?? 0) + qtyInput
                    : (double?)null;
                var mergedCode = sameUom ? uomCode : null;
                _services.Documents.UpdateDocLineQty(_doc!.Id, existing.Id, existing.Qty + qtyBase, mergedInput, mergedCode);
            }
            else
            {
                _services.Documents.AddDocLine(_doc!.Id, item!.Id, qtyBase, fromLocation?.Id, toLocation?.Id, qtyInput, uomCode, fromHu, toHu);
            }
            LoadDocLines();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DocDeleteLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDraftDocSelected())
        {
            return;
        }

        if (HasOrderBinding())
        {
            return;
        }

        if (_selectedDocLine == null)
        {
            MessageBox.Show("Выберите строку.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _services.Documents.DeleteDocLine(_doc!.Id, _selectedDocLine.Id);
            LoadDocLines();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DocEditLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDraftDocSelected())
        {
            return;
        }

        if (HasOrderBinding() && !_isPartialShipment)
        {
            return;
        }

        if (_selectedDocLine == null)
        {
            MessageBox.Show("Выберите строку.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var orderedQty = 0.0;
        if (HasOrderBinding() && !TryGetOrderedQty(_selectedDocLine.ItemId, out orderedQty))
        {
            MessageBox.Show("Не удалось найти количество из заказа.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var item = _services.DataStore.FindItemById(_selectedDocLine.ItemId);
        if (item == null)
        {
            MessageBox.Show("Товар не найден.", "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var packagings = _services.Packagings.GetPackagings(item.Id);
        var defaultQty = _selectedDocLine.QtyInput ?? _selectedDocLine.QtyBase;
        var defaultUom = string.IsNullOrWhiteSpace(_selectedDocLine.UomCode) ? "BASE" : _selectedDocLine.UomCode;
        var (availableQty, showAvailableLabel) = GetAvailableQtyForDialog(item.Id);
        var qtyDialog = new QuantityUomDialog(item.BaseUom, packagings, defaultQty, defaultUom, availableQty, showAvailableLabel)
        {
            Owner = this
        };
        if (qtyDialog.ShowDialog() != true)
        {
            return;
        }

        if (HasOrderBinding() && _isPartialShipment)
        {
            var newQty = qtyDialog.QtyBase;
            if (newQty < 1 || newQty > orderedQty)
            {
                MessageBox.Show(
                    $"Количество должно быть от 1 до {FormatQty(orderedQty)}.",
                    "Операция",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        try
        {
            _services.Documents.UpdateDocLineQty(_doc!.Id, _selectedDocLine.Id, qtyDialog.QtyBase, qtyDialog.QtyInput, qtyDialog.UomCode);
            LoadDocLines();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DocHeaderSave_Click(object sender, RoutedEventArgs e)
    {
        TrySaveHeader();
    }

    private void UpdateDocView()
    {
        if (_doc == null)
        {
            return;
        }

        DocInfoText.Text = FormatDocHeader(_doc);
        var isDraft = _doc.Status == DocStatus.Draft;
        var isEditable = isDraft && !_doc.IsRecountRequested;
        DocCloseButton.IsEnabled = isEditable;
        DocHeaderPanel.IsEnabled = isEditable;

        MarkHeaderSaved();
        _suppressDirtyTracking = true;
        ConfigureHeaderFields(_doc, isEditable);
        UpdatePartialUi();
        ApplyPartnerFilter();
        DocPartnerCombo.SelectedItem = _partners.FirstOrDefault(p => p.Id == _doc.PartnerId);
        SelectOrderFromDoc(_doc);
        ApplyHeaderLocationsFromLines();
        LoadHuOptions();
        SetHuSelection(_doc);
        _suppressDirtyTracking = false;
        UpdateLineButtons();
        UpdatePartnerLock();
        UpdateActionButtons();

        if (isEditable)
        {
            AddItemButton.Focus();
        }
    }

    private void SelectOrderFromDoc(Doc doc)
    {
        if (_suppressOrderSync)
        {
            return;
        }

        _suppressOrderSync = true;
        if (doc.OrderId.HasValue)
        {
            var selected = _ordersAll.FirstOrDefault(order => order.Id == doc.OrderId.Value);
            DocOrderCombo.SelectedItem = selected;
            DocOrderCombo.Text = selected?.DisplayName ?? doc.OrderRef ?? string.Empty;
        }
        else
        {
            DocOrderCombo.SelectedItem = null;
            DocOrderCombo.Text = doc.OrderRef ?? string.Empty;
        }
        _suppressOrderSync = false;
    }

    private void ApplyHeaderLocationsFromLines()
    {
        if (_doc == null)
        {
            return;
        }

        var lines = _services.DataStore.GetDocLines(_doc.Id);
        if (lines.Count == 0)
        {
            return;
        }

        if (_doc.Type == DocType.Inbound || _doc.Type == DocType.Inventory)
        {
            if (DocToCombo.SelectedItem == null)
            {
                var toLocation = ResolveUniqueLocation(lines, line => line.ToLocationId);
                if (toLocation != null)
                {
                    DocToCombo.SelectedItem = toLocation;
                }
            }
            return;
        }

        if (_doc.Type == DocType.Outbound || _doc.Type == DocType.WriteOff)
        {
            if (DocFromCombo.SelectedItem == null)
            {
                var fromLocation = ResolveUniqueLocation(lines, line => line.FromLocationId);
                if (fromLocation != null)
                {
                    DocFromCombo.SelectedItem = fromLocation;
                }
            }
            return;
        }

        if (_doc.Type == DocType.Move)
        {
            if (DocFromCombo.SelectedItem == null)
            {
                var fromLocation = ResolveUniqueLocation(lines, line => line.FromLocationId);
                if (fromLocation != null)
                {
                    DocFromCombo.SelectedItem = fromLocation;
                }
            }

            if (DocToCombo.SelectedItem == null)
            {
                var toLocation = ResolveUniqueLocation(lines, line => line.ToLocationId);
                if (toLocation != null)
                {
                    DocToCombo.SelectedItem = toLocation;
                }
            }
        }
    }

    private Location? ResolveUniqueLocation(IReadOnlyList<DocLine> lines, Func<DocLine, long?> selector)
    {
        var ids = lines
            .Select(selector)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (ids.Count != 1)
        {
            return null;
        }

        return _locations.FirstOrDefault(location => location.Id == ids[0]);
    }

    private void DocPartnerCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressOrderSync)
        {
            return;
        }

        RefreshOrderList();
        if (DocOrderCombo.SelectedItem is OrderOption selected && _orders.All(o => o.Id != selected.Id))
        {
            DocOrderCombo.SelectedItem = null;
            DocOrderCombo.Text = string.Empty;
        }

        if (!_suppressDirtyTracking && _doc?.Status == DocStatus.Draft && DocOrderCombo.SelectedItem == null)
        {
            MarkHeaderDirty();
        }
    }

    private void DocHuCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressDirtyTracking || _doc?.Status != DocStatus.Draft)
        {
            return;
        }

        MarkHeaderDirty();
    }

    private void DocHuCombo_KeyUp(object sender, KeyEventArgs e)
    {
        if (_suppressDirtyTracking || _doc?.Status != DocStatus.Draft)
        {
            return;
        }

        MarkHeaderDirty();
    }

    private void DocOrderCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressOrderSync)
        {
            return;
        }

        if (DocOrderCombo.SelectedItem is not OrderOption selected)
        {
            UpdatePartnerLock();
            return;
        }

        var partner = _partners.FirstOrDefault(p => p.Id == selected.PartnerId);
        if (partner == null)
        {
            return;
        }

        _suppressOrderSync = true;
        DocPartnerCombo.SelectedItem = partner;
        _suppressOrderSync = false;
        UpdatePartnerLock();
        ResetPartialMode();
        TryApplyOrderSelection(selected);
        UpdateLineButtons();
    }

    private void DocOrderCombo_KeyUp(object sender, KeyEventArgs e)
    {
        if (_suppressOrderSync)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(DocOrderCombo.Text))
        {
            DocOrderCombo.SelectedItem = null;
            ClearDocOrderBinding();
            ResetPartialMode();
            UpdatePartnerLock();
            UpdateLineButtons();
            UpdateActionButtons();
        }
        else if (!_suppressDirtyTracking && _doc?.Status == DocStatus.Draft && DocOrderCombo.SelectedItem == null)
        {
            MarkHeaderDirty();
        }
    }

    private void DocOrderClear_Click(object sender, RoutedEventArgs e)
    {
        _suppressOrderSync = true;
        DocOrderCombo.SelectedItem = null;
        DocOrderCombo.Text = string.Empty;
        _suppressOrderSync = false;
        ClearDocOrderBinding();
        ResetPartialMode();
        UpdatePartnerLock();
        UpdateLineButtons();
        UpdateActionButtons();
    }

    private void DocPartialCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressPartialSync)
        {
            return;
        }

        if (!HasOrderBinding())
        {
            ResetPartialMode();
            return;
        }

        _isPartialShipment = DocPartialCheck.IsChecked == true;
        if (!_isPartialShipment && _doc?.OrderId.HasValue == true)
        {
            TryApplyOrderSelection(new OrderOption(_doc.OrderId.Value, _doc.OrderRef ?? string.Empty, _doc.PartnerId ?? 0, string.Empty));
        }

        UpdateLineButtons();
        UpdateActionButtons();
    }

    private void DocMoveInternalCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_doc?.Type != DocType.Move)
        {
            return;
        }

        if (DocMoveInternalCheck.IsChecked == true)
        {
            if (DocFromCombo.SelectedItem is Location fromLocation)
            {
                DocToCombo.SelectedItem = fromLocation;
            }

            DocToCombo.IsEnabled = false;
        }
        else
        {
            DocToCombo.IsEnabled = true;
        }

        RefreshHuOptions();
    }

    private void DocFromCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_doc == null)
        {
            return;
        }

        if (_doc.Type == DocType.Move && DocMoveInternalCheck.IsChecked == true)
        {
            DocToCombo.SelectedItem = DocFromCombo.SelectedItem;
        }

        RefreshHuOptions();
    }

    private void DocToCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_doc == null)
        {
            return;
        }

        RefreshHuOptions();
    }

    private void UpdatePartnerLock()
    {
        DocPartnerCombo.IsEnabled = DocOrderCombo.SelectedItem == null;
    }

    private void ConfigureHeaderFields(Doc doc, bool isEditable)
    {
        var showPartner = false;
        var showOrder = false;
        var showFrom = false;
        var showTo = false;
        var showHu = true;
        var partnerLabel = "Контрагент";
        var fromLabel = "Откуда";
        var toLabel = "Куда";

        switch (doc.Type)
        {
            case DocType.Inbound:
                showPartner = true;
                partnerLabel = "Поставщик";
                showTo = true;
                toLabel = "Место хранения";
                break;
            case DocType.Outbound:
                showPartner = true;
                partnerLabel = "Покупатель";
                showOrder = true;
                break;
            case DocType.Move:
                showFrom = true;
                showTo = true;
                fromLabel = "Откуда";
                toLabel = "Куда";
                break;
            case DocType.WriteOff:
                showFrom = true;
                fromLabel = "Место хранения";
                break;
            case DocType.Inventory:
                showTo = true;
                toLabel = "Место хранения";
                break;
        }

        DocPartnerPanel.Visibility = showPartner ? Visibility.Visible : Visibility.Collapsed;
        DocOrderPanel.Visibility = showOrder ? Visibility.Visible : Visibility.Collapsed;
        DocFromPanel.Visibility = showFrom ? Visibility.Visible : Visibility.Collapsed;
        DocToPanel.Visibility = showTo ? Visibility.Visible : Visibility.Collapsed;
        DocHuPanel.Visibility = showHu ? Visibility.Visible : Visibility.Collapsed;
        DocHuFromPanel.Visibility = doc.Type == DocType.Move ? Visibility.Visible : Visibility.Collapsed;
        DocMoveInternalPanel.Visibility = doc.Type == DocType.Move ? Visibility.Visible : Visibility.Collapsed;

        DocPartnerLabel.Text = partnerLabel;
        DocFromLabel.Text = fromLabel;
        DocToLabel.Text = toLabel;
        DocHuLabel.Text = doc.Type == DocType.Move ? "HU (куда)" : "HU";
        DocPartialCheck.Visibility = showOrder ? Visibility.Visible : Visibility.Collapsed;
        DocHuCombo.IsEnabled = isEditable;
        DocHuFromCombo.IsEnabled = isEditable;
        DocHuCombo.IsEditable = doc.Type == DocType.Move;
        DocMoveInternalCheck.IsEnabled = isEditable;

        if (!showFrom)
        {
            DocFromCombo.SelectedItem = null;
        }

        if (!showTo)
        {
            DocToCombo.SelectedItem = null;
        }
        if (doc.Type != DocType.Move)
        {
            DocMoveInternalCheck.IsChecked = false;
        }

        DocHeaderSaveButton.Visibility = showPartner || showOrder || showHu
            ? Visibility.Visible
            : Visibility.Collapsed;
        DocHeaderSaveButton.IsEnabled = isEditable;

        DocRecountButton.Visibility = doc.Type == DocType.Inventory && IsTsdSource(doc)
            ? Visibility.Visible
            : Visibility.Collapsed;
        DocRecountButton.IsEnabled = isEditable;

        var showInventory = doc.Type == DocType.Inventory;
        DocInventoryDbColumn.Visibility = showInventory ? Visibility.Visible : Visibility.Collapsed;
        DocInventoryDiffColumn.Visibility = showInventory ? Visibility.Visible : Visibility.Collapsed;
        DocFromColumn.Visibility = showFrom ? Visibility.Visible : Visibility.Collapsed;
        DocToColumn.Visibility = showTo ? Visibility.Visible : Visibility.Collapsed;
        DocFromColumn.Header = fromLabel;
        DocToColumn.Header = toLabel;
    }

    private static bool IsTsdSource(Doc doc)
    {
        if (!string.IsNullOrWhiteSpace(doc.SourceDeviceId))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(doc.Comment)
               && doc.Comment.StartsWith("TSD", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsDocEditable()
    {
        return _doc?.Status == DocStatus.Draft && _doc?.IsRecountRequested != true;
    }

    private static string FormatDocHeader(Doc doc)
    {
        var createdAt = doc.CreatedAt.ToString("dd'/'MM'/'yyyy HH':'mm", CultureInfo.InvariantCulture);
        var closedAt = doc.ClosedAt.HasValue
            ? doc.ClosedAt.Value.ToString("dd'/'MM'/'yyyy HH':'mm", CultureInfo.InvariantCulture)
            : "-";
        return $"Номер: {doc.DocRef} | Тип: {DocTypeMapper.ToDisplayName(doc.Type)} | Статус: {doc.StatusDisplay} | Создан: {createdAt} | Проведена: {closedAt}";
    }

    private void UpdateLineButtons()
    {
        var isEditable = IsDocEditable();
        var hasOrder = HasOrderBinding();
        var allowPartialEdit = hasOrder && _isPartialShipment;
        AddItemButton.IsEnabled = isEditable && !hasOrder;
        EditLineButton.IsEnabled = isEditable && _selectedDocLine != null && (!hasOrder || allowPartialEdit);
        DeleteLineButton.IsEnabled = isEditable && _selectedDocLine != null && !hasOrder;
        DocPartialCheck.IsEnabled = isEditable && hasOrder;
    }

    private void UpdateActionButtons()
    {
        var isEditable = IsDocEditable();
        var hasId = _doc?.Id > 0;
        var hasPartner = !IsPartnerRequired() || _doc?.PartnerId != null || DocPartnerCombo.SelectedItem != null;
        var hasShortage = _doc?.Type == DocType.Outbound && _hasOutboundShortage;
        DocCloseButton.IsEnabled = isEditable && hasId && hasPartner && !hasShortage;
        DocHeaderSaveButton.IsEnabled = isEditable && _hasUnsavedChanges;
    }

    private bool IsPartnerRequired()
    {
        return _doc?.Type == DocType.Inbound || _doc?.Type == DocType.Outbound;
    }

    private void UpdateAvailabilityStatus()
    {
        if (_doc?.Type != DocType.Outbound || _outboundShortageCount == 0)
        {
            DocShortageText.Text = string.Empty;
            DocShortageText.Visibility = Visibility.Collapsed;
            return;
        }

        DocShortageText.Text = $"Не хватает по {_outboundShortageCount} позициям.";
        DocShortageText.Visibility = Visibility.Visible;
    }

    private bool HasOrderBinding()
    {
        return _doc?.OrderId.HasValue == true || DocOrderCombo.SelectedItem != null;
    }

    private void TryApplyOrderSelection(OrderOption selected)
    {
        if (_doc == null || _doc.Type != DocType.Outbound)
        {
            return;
        }

        try
        {
            var added = _services.Documents.ApplyOrderToDoc(_doc.Id, selected.Id);
            LoadOrderQuantities(selected.Id);
            LoadDoc();
            if (added == 0)
            {
                MessageBox.Show("По заказу нет остатка к отгрузке.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearDocOrderBinding()
    {
        if (_doc == null || _doc.Status != DocStatus.Draft)
        {
            return;
        }

        var partnerId = (DocPartnerCombo.SelectedItem as Partner)?.Id;
        try
        {
            _services.Documents.ClearDocOrder(_doc.Id, partnerId);
            _orderedQtyByItem.Clear();
            LoadDoc();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool TrySaveHeader()
    {
        if (_doc == null)
        {
            MessageBox.Show("Операция не выбрана.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (_doc.Status != DocStatus.Draft)
        {
            MessageBox.Show("Операция уже закрыта.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (_doc.IsRecountRequested)
        {
            MessageBox.Show("Операция находится на перерасчете. Изменения недоступны до ответа от ТСД.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var partnerId = (DocPartnerCombo.SelectedItem as Partner)?.Id;
        var huCode = GetSelectedHuCode(DocHuCombo);
        try
        {
            if (!TryResolveOrder(out var orderOption))
            {
                return false;
            }

            if (orderOption != null)
            {
                _services.Documents.UpdateDocHeader(_doc.Id, orderOption.PartnerId, orderOption.OrderRef, huCode);
                if (!_isPartialShipment || _doc.OrderId != orderOption.Id)
                {
                    var added = _services.Documents.ApplyOrderToDoc(_doc.Id, orderOption.Id);
                    LoadOrderQuantities(orderOption.Id);
                    ResetPartialMode();
                    if (added == 0)
                    {
                        MessageBox.Show("По заказу нет остатка к отгрузке.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            else
            {
                _services.Documents.ClearDocOrder(_doc.Id, partnerId);
                _services.Documents.UpdateDocHeader(_doc.Id, partnerId, null, huCode);
                ResetPartialMode();
            }

            LoadDoc();
            MarkHeaderSaved();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void ApplyPartnerFilter()
    {
        _partners.Clear();
        var docType = _doc?.Type;
        foreach (var partner in _partnersAll)
        {
            var status = _services.PartnerStatuses.GetStatus(partner.Id);
            if (docType == DocType.Inbound && status == PartnerStatus.Client)
            {
                continue;
            }

            if (docType == DocType.Outbound && status == PartnerStatus.Supplier)
            {
                continue;
            }

            _partners.Add(partner);
        }
    }

    private void LoadOrderQuantities(long orderId)
    {
        _orderedQtyByItem.Clear();
        var orderedByItem = new Dictionary<long, double>();
        foreach (var line in _services.DataStore.GetOrderLines(orderId))
        {
            if (line.QtyOrdered <= 0)
            {
                continue;
            }

            orderedByItem[line.ItemId] = orderedByItem.TryGetValue(line.ItemId, out var current)
                ? current + line.QtyOrdered
                : line.QtyOrdered;
        }

        var shippedByItem = _services.DataStore.GetShippedTotalsByOrder(orderId);
        foreach (var entry in orderedByItem)
        {
            var shipped = shippedByItem.TryGetValue(entry.Key, out var shippedQty) ? shippedQty : 0;
            var remaining = entry.Value - shipped;
            if (remaining <= 0)
            {
                continue;
            }

            _orderedQtyByItem[entry.Key] = remaining;
        }
    }

    private void UpdatePartialUi()
    {
        if (_doc?.OrderId.HasValue == true)
        {
            LoadOrderQuantities(_doc.OrderId.Value);
        }
        else
        {
            _orderedQtyByItem.Clear();
            _isPartialShipment = false;
        }

        _suppressPartialSync = true;
        DocPartialCheck.IsChecked = _isPartialShipment;
        _suppressPartialSync = false;
    }

    private void MarkHeaderDirty()
    {
        _hasUnsavedChanges = true;
        UpdateActionButtons();
    }

    private void MarkHeaderSaved()
    {
        _hasUnsavedChanges = false;
        UpdateActionButtons();
    }

    private void ResetPartialMode()
    {
        _isPartialShipment = false;
        _suppressPartialSync = true;
        DocPartialCheck.IsChecked = false;
        _suppressPartialSync = false;
    }

    private bool TryGetOrderedQty(long itemId, out double orderedQty)
    {
        return _orderedQtyByItem.TryGetValue(itemId, out orderedQty);
    }

    private (double? AvailableQty, bool ShowAvailableLabel) GetAvailableQtyForDialog(long itemId)
    {
        if (_doc?.Type != DocType.Outbound)
        {
            if (_doc?.Type == DocType.Move)
            {
                if (DocFromCombo.SelectedItem is Location fromLocation)
                {
                    var fromHu = (DocHuFromCombo.SelectedItem as HuOption)?.Code;
                    return (_services.DataStore.GetAvailableQty(itemId, fromLocation.Id, NormalizeHuValue(fromHu)), true);
                }

                return (null, true);
            }

            return (null, false);
        }

        var availableByItem = _services.Orders.GetItemAvailability();
        return (availableByItem.TryGetValue(itemId, out var qty) ? qty : 0, true);
    }

    private bool TryValidateOutboundStock(long docId)
    {
        var lines = _services.Documents.GetDocLines(docId);
        var requiredByItem = lines
            .Where(line => line.Qty > 0)
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Qty));
        if (requiredByItem.Count == 0)
        {
            return true;
        }

        var availableByItem = _services.Orders.GetItemAvailability();
        var namesByItem = lines
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.First().ItemName);
        var shortages = new List<string>();
        foreach (var entry in requiredByItem)
        {
            var available = availableByItem.TryGetValue(entry.Key, out var qty) ? qty : 0;
            available = Math.Max(0, available);
            if (available + 0.0001 < entry.Value)
            {
                var name = namesByItem.TryGetValue(entry.Key, out var itemName) ? itemName : $"ID {entry.Key}";
                shortages.Add($"- {name}: нужно {FormatQty(entry.Value)}, доступно {FormatQty(available)}");
            }
        }

        if (shortages.Count == 0)
        {
            return true;
        }

        MessageBox.Show(
            "Недостаточно остатков для проведения отгрузки:\n" + string.Join("\n", shortages),
            "Операция",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private IReadOnlyList<ItemPackaging> GetPackagings(long itemId, IDictionary<long, IReadOnlyList<ItemPackaging>> lookup)
    {
        if (lookup.TryGetValue(itemId, out var cached))
        {
            return cached;
        }

        var packagings = _services.Packagings.GetPackagings(itemId, includeInactive: true);
        lookup[itemId] = packagings;
        return packagings;
    }

    private static ItemPackaging? ResolvePackaging(IReadOnlyList<ItemPackaging> packagings, string? uomCode)
    {
        if (string.IsNullOrWhiteSpace(uomCode) || IsBaseUomCode(uomCode))
        {
            return null;
        }

        return packagings.FirstOrDefault(packaging => string.Equals(packaging.Code, uomCode, StringComparison.OrdinalIgnoreCase));
    }

    private static double ResolveInputQty(DocLineView line, ItemPackaging? packaging)
    {
        if (line.QtyInput.HasValue)
        {
            return line.QtyInput.Value;
        }

        if (packaging != null && packaging.FactorToBase > 0)
        {
            return line.Qty / packaging.FactorToBase;
        }

        return line.Qty;
    }

    private static string FormatInputUomDisplay(string? uomCode, string baseUom, ItemPackaging? packaging)
    {
        if (packaging != null && !IsBaseUomCode(uomCode))
        {
            var factor = FormatQty(packaging.FactorToBase);
            return $"{packaging.Code} — {packaging.Name} (×{factor})";
        }

        if (!string.IsNullOrWhiteSpace(uomCode) && !IsBaseUomCode(uomCode))
        {
            return uomCode.Trim();
        }

        return $"{baseUom} (база)";
    }

    private static string FormatBaseQty(DocLineView line)
    {
        var baseUom = string.IsNullOrWhiteSpace(line.BaseUom) ? "шт" : line.BaseUom;
        return $"{FormatQty(line.Qty)} {baseUom}";
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

    private static bool IsSameUom(string? left, string? right)
    {
        return string.Equals(NormalizeUomCode(left), NormalizeUomCode(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBaseUomCode(string? code)
    {
        return string.Equals(NormalizeUomCode(code), "BASE", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeUomCode(string? code)
    {
        return string.IsNullOrWhiteSpace(code) ? "BASE" : code.Trim();
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", CultureInfo.CurrentCulture);
    }

    private void LoadHuOptions()
    {
        RefreshHuOptions();
    }

    private void RefreshHuOptions()
    {
        var selectedToHu = GetSelectedHuCode(DocHuCombo);
        var selectedFromHu = (DocHuFromCombo.SelectedItem as HuOption)?.Code;

        _huToOptions.Clear();
        _huFromOptions.Clear();
        _huToOptions.Add(new HuOption(null, "-"));

        if (_doc == null)
        {
            return;
        }

        var totalsByHuAll = _services.DataStore.GetLedgerTotalsByHu();

        if (_doc.Type == DocType.Move)
        {
            var fromLocation = DocFromCombo.SelectedItem as Location;
            if (fromLocation != null)
            {
                var codes = _services.DataStore.GetHuCodesByLocation(fromLocation.Id);
                if (codes.Any(code => code == null))
                {
                    _huFromOptions.Add(new HuOption(null, "—"));
                }
                foreach (var code in codes.Where(code => !string.IsNullOrWhiteSpace(code)))
                {
                    _huFromOptions.Add(new HuOption(code, code!));
                }
            }
            else
            {
                _huFromOptions.Add(new HuOption(null, "-"));
            }

            var toLocation = DocToCombo.SelectedItem as Location;
            if (toLocation != null)
            {
                var totalsByLocation = GetHuTotalsByLocation(toLocation.Id);
                var freeCodes = GetFreeHuCodes(totalsByHuAll);
                AddHuOptions(BuildHuOptions(totalsByLocation, freeCodes), _huToOptions);
            }
        }
        else
        {
            var selectedLocation = _doc.Type == DocType.Inbound || _doc.Type == DocType.Inventory
                ? DocToCombo.SelectedItem as Location
                : DocFromCombo.SelectedItem as Location;
            if (selectedLocation != null)
            {
                var totalsByLocation = GetHuTotalsByLocation(selectedLocation.Id);
                var freeCodes = _doc.Type == DocType.Inbound || _doc.Type == DocType.Inventory
                    ? GetFreeHuCodes(totalsByHuAll)
                    : Enumerable.Empty<string>();
                AddHuOptions(BuildHuOptions(totalsByLocation, freeCodes), _huToOptions);
            }
            _huFromOptions.Add(new HuOption(null, "-"));
        }

        var previousSuppress = _suppressDirtyTracking;
        _suppressDirtyTracking = true;
        RestoreHuSelection(DocHuCombo, _huToOptions, selectedToHu);
        RestoreHuSelection(DocHuFromCombo, _huFromOptions, selectedFromHu);
        _suppressDirtyTracking = previousSuppress;
    }

    private IEnumerable<string> GetAvailableHuCodes()
    {
        try
        {
            var hus = _services.Hus.GetHus(null, 2000);
            return hus
                .Where(IsSelectableHu)
                .Select(hu => hu.Code)
                .Where(code => !string.IsNullOrWhiteSpace(code));
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }

    private IReadOnlyDictionary<string, double> GetHuTotalsByLocation(long locationId)
    {
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _services.DataStore.GetHuStockRows())
        {
            if (row.LocationId != locationId || string.IsNullOrWhiteSpace(row.HuCode))
            {
                continue;
            }

            totals[row.HuCode] = totals.TryGetValue(row.HuCode, out var current)
                ? current + row.Qty
                : row.Qty;
        }

        return totals;
    }

    private IEnumerable<string> GetFreeHuCodes(IReadOnlyDictionary<string, double> totalsByHu)
    {
        return GetAvailableHuCodes()
            .Where(code => !totalsByHu.TryGetValue(code, out var qty) || qty <= 0.000001);
    }

    private static bool IsSelectableHu(HuRecord record)
    {
        return !string.Equals(record.Status, "CLOSED", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(record.Status, "VOID", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> BuildHuOptions(
        IReadOnlyDictionary<string, double> totalsByHu,
        IEnumerable<string> issuedCodes)
    {
        var allCodes = totalsByHu.Keys
            .Concat(issuedCodes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var code in allCodes)
        {
            if (!totalsByHu.TryGetValue(code, out var qty) || qty <= 0.000001)
            {
                yield return $"{code} (свободен)";
            }
            else
            {
                yield return code;
            }
        }
    }

    private static void AddHuOptions(IEnumerable<string> codes, ICollection<HuOption> target, string? suffix = null)
    {
        foreach (var code in codes)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            var normalized = code;
            var suffixIndex = normalized.IndexOf(" (", StringComparison.Ordinal);
            if (suffixIndex > 0)
            {
                normalized = normalized.Substring(0, suffixIndex);
            }

            if (target.Any(option => string.Equals(option.Code, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var display = suffix == null ? code : $"{code}{suffix}";
            target.Add(new HuOption(normalized, display));
        }
    }

    private void SetHuSelection(Doc doc)
    {
        var normalized = NormalizeHuCode(doc.ShippingRef) ?? ResolveHeaderHuFromLines(doc);
        var currentFromHu = (DocHuFromCombo.SelectedItem as HuOption)?.Code;
        _suppressDirtyTracking = true;
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            if (_huToOptions.All(option => !string.Equals(option.Code, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                _huToOptions.Add(new HuOption(normalized, normalized));
            }
            DocHuCombo.SelectedItem = _huToOptions.FirstOrDefault(option =>
                string.Equals(option.Code, normalized, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            DocHuCombo.SelectedItem = _huToOptions.FirstOrDefault(option => option.Code == null)
                                      ?? _huToOptions.FirstOrDefault();
        }
        if (doc.Type == DocType.Move && !string.IsNullOrWhiteSpace(currentFromHu))
        {
            DocHuFromCombo.SelectedItem = _huFromOptions.FirstOrDefault(option =>
                string.Equals(option.Code, currentFromHu, StringComparison.OrdinalIgnoreCase))
                                          ?? _huFromOptions.FirstOrDefault(option => option.Code == null)
                                          ?? _huFromOptions.FirstOrDefault();
        }
        else
        {
            DocHuFromCombo.SelectedItem = _huFromOptions.FirstOrDefault(option => option.Code == null)
                                          ?? _huFromOptions.FirstOrDefault();
        }
        _suppressDirtyTracking = false;
    }

    private string? ResolveHeaderHuFromLines(Doc doc)
    {
        var lines = _services.DataStore.GetDocLines(doc.Id);
        if (lines.Count == 0)
        {
            return null;
        }

        IEnumerable<string?> values = doc.Type switch
        {
            DocType.Inbound or DocType.Inventory => lines.Select(line => line.ToHu),
            DocType.Outbound or DocType.WriteOff => lines.Select(line => line.FromHu),
            DocType.Move => lines.Select(line => line.ToHu),
            _ => Enumerable.Empty<string?>()
        };

        var distinct = values
            .Select(NormalizeHuCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return distinct.Count == 1 ? distinct[0] : null;
    }

    private static string? NormalizeHuCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("HU-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed.ToUpperInvariant();
    }

    private static string? GetSelectedHuCode(System.Windows.Controls.ComboBox combo)
    {
        if (combo.SelectedItem is HuOption option)
        {
            return option.Code;
        }

        var text = combo.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var suffixIndex = text.IndexOf(" (", StringComparison.Ordinal);
            if (suffixIndex > 0)
            {
                text = text.Substring(0, suffixIndex);
            }
        }
        return string.IsNullOrWhiteSpace(text) ? null : NormalizeHuCode(text);
    }

    private static void RestoreHuSelection(
        System.Windows.Controls.ComboBox combo,
        IEnumerable<HuOption> options,
        string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            combo.SelectedItem = options.FirstOrDefault(option => option.Code == null)
                                 ?? options.FirstOrDefault();
            return;
        }

        combo.SelectedItem = options.FirstOrDefault(option =>
                                 string.Equals(option.Code, code, StringComparison.OrdinalIgnoreCase))
                             ?? options.FirstOrDefault(option => option.Code == null)
                             ?? options.FirstOrDefault();
    }

    private sealed class HuOption
    {
        public HuOption(string? code, string displayName)
        {
            Code = code;
            DisplayName = displayName;
        }

        public string? Code { get; }
        public string DisplayName { get; }
    }

    private bool TryGetLineLocations(out Location? fromLocation, out Location? toLocation, out string? fromHu, out string? toHu)
    {
        fromLocation = DocFromCombo.SelectedItem as Location;
        toLocation = DocToCombo.SelectedItem as Location;
        fromHu = null;
        toHu = null;

        if (_doc == null)
        {
            return false;
        }

        if (_doc.Type == DocType.Inbound || _doc.Type == DocType.Inventory)
        {
            fromLocation = null;
        }
        else if (_doc.Type == DocType.WriteOff || _doc.Type == DocType.Outbound)
        {
            toLocation = null;
        }

        if (_doc.Type == DocType.Move)
        {
            fromHu = (DocHuFromCombo.SelectedItem as HuOption)?.Code;
            toHu = GetSelectedHuCode(DocHuCombo);
        }
        else
        {
            ApplyLineHu(_doc.Type, GetSelectedHuCode(DocHuCombo), ref fromHu, ref toHu);
        }
        return ValidateLineLocations(_doc, fromLocation, toLocation, fromHu, toHu);
    }

    private bool EnsureDraftDocSelected()
    {
        if (_doc == null)
        {
            MessageBox.Show("Операция не выбрана.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (_doc.Status != DocStatus.Draft)
        {
            MessageBox.Show("Операция уже закрыта.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (_doc.IsRecountRequested)
        {
            MessageBox.Show("Операция находится на перерасчете. Дождитесь данных от ТСД.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private bool TryResolveOrder(out OrderOption? orderOption)
    {
        orderOption = null;
        var text = DocOrderCombo.Text?.Trim();
        if (DocOrderCombo.SelectedItem is OrderOption selected)
        {
            orderOption = selected;
            return true;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var match = _ordersAll.FirstOrDefault(order => string.Equals(order.OrderRef, text, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            MessageBox.Show("Выберите заказ из списка.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        DocOrderCombo.SelectedItem = match;
        orderOption = match;
        return true;
    }

    private bool ValidateLineLocations(Doc doc, Location? fromLocation, Location? toLocation, string? fromHu, string? toHu)
    {
        switch (doc.Type)
        {
            case DocType.Inbound:
                if (toLocation == null)
                {
                    MessageBox.Show("Для приемки выберите место хранения получателя.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            case DocType.WriteOff:
                if (fromLocation == null)
                {
                    MessageBox.Show("Для списания выберите место хранения источника.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            case DocType.Outbound:
                if (fromLocation == null)
                {
                    MessageBox.Show("Для отгрузки выберите место хранения источника.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            case DocType.Move:
                if (fromLocation == null || toLocation == null)
                {
                    MessageBox.Show("Для перемещения выберите места хранения откуда/куда.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                if (DocMoveInternalCheck.IsChecked == true)
                {
                    if (fromLocation.Id != toLocation.Id)
                    {
                        MessageBox.Show("Для внутреннего перемещения выберите одинаковые места хранения.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(fromHu) && string.IsNullOrWhiteSpace(toHu))
                    {
                        MessageBox.Show("Для внутреннего перемещения укажите HU-источник или HU-назначение.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }

                    return true;
                }

                if (fromLocation.Id == toLocation.Id
                    && string.IsNullOrWhiteSpace(NormalizeHuValue(fromHu))
                    && string.IsNullOrWhiteSpace(NormalizeHuValue(toHu)))
                {
                    MessageBox.Show("Для перемещения места хранения должны быть разными. Если вы хотите упаковать в HU в том же месте - заполните HU.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            case DocType.Inventory:
                if (toLocation == null)
                {
                    MessageBox.Show("Для инвентаризации выберите место хранения.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;
            default:
                return true;
        }
    }

    private static void ApplyLineHu(DocType docType, string? selectedHu, ref string? fromHu, ref string? toHu)
    {
        var normalized = NormalizeHuValue(selectedHu);
        switch (docType)
        {
            case DocType.Inbound:
            case DocType.Inventory:
                toHu = normalized;
                fromHu = null;
                break;
            case DocType.Outbound:
            case DocType.WriteOff:
                fromHu = normalized;
                toHu = null;
                break;
            case DocType.Move:
                fromHu = null;
                toHu = normalized;
                break;
        }
    }

    private static string? NormalizeHuValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed class DocLineDisplay
    {
        public long Id { get; init; }
        public long ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public double QtyBase { get; init; }
        public double? QtyInput { get; init; }
        public string? UomCode { get; init; }
        public string BaseUom { get; init; } = "шт";
        public string InputQtyDisplay { get; init; } = string.Empty;
        public string InputUomDisplay { get; init; } = string.Empty;
        public string BaseQtyDisplay { get; init; } = string.Empty;
        public double? AvailableQty { get; init; }
        public bool HasShortage { get; init; }
        public double? ShortageQty { get; init; }
        public double? InventoryDbQty { get; init; }
        public double? InventoryDiffQty { get; init; }
        public string InventoryDbQtyDisplay { get; init; } = string.Empty;
        public string InventoryDiffQtyDisplay { get; init; } = string.Empty;
        public bool HasInventoryDiff { get; init; }
        public string? FromLocation { get; init; }
        public string? ToLocation { get; init; }
    }

    private sealed record OrderOption(long Id, string OrderRef, long PartnerId, string PartnerDisplay)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(PartnerDisplay)
            ? OrderRef
            : $"{OrderRef} - {PartnerDisplay}";
    }
}


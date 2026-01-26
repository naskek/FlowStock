using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using LightWms.Core.Models;

namespace LightWms.App;

public partial class OperationDetailsWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<Location> _locations = new();
    private readonly ObservableCollection<Partner> _partners = new();
    private readonly List<Partner> _partnersAll = new();
    private readonly ObservableCollection<DocLineDisplay> _docLines = new();
    private readonly ObservableCollection<OrderOption> _orders = new();
    private readonly List<OrderOption> _ordersAll = new();
    private readonly ObservableCollection<HuOption> _huOptions = new();
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
        DocHuCombo.ItemsSource = _huOptions;

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
            if (_doc?.Status != DocStatus.Draft)
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
        DocCloseButton.IsEnabled = isDraft;
        DocHeaderPanel.IsEnabled = isDraft;

        MarkHeaderSaved();
        _suppressDirtyTracking = true;
        ConfigureHeaderFields(_doc, isDraft);
        UpdatePartialUi();
        ApplyPartnerFilter();
        DocPartnerCombo.SelectedItem = _partners.FirstOrDefault(p => p.Id == _doc.PartnerId);
        SelectOrderFromDoc(_doc);
        LoadHuOptions();
        SetHuSelection(_doc);
        _suppressDirtyTracking = false;
        UpdateLineButtons();
        UpdatePartnerLock();
        UpdateActionButtons();

        if (_doc.Status == DocStatus.Draft)
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

    private void UpdatePartnerLock()
    {
        DocPartnerCombo.IsEnabled = DocOrderCombo.SelectedItem == null;
    }

    private void ConfigureHeaderFields(Doc doc, bool isDraft)
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

        DocPartnerLabel.Text = partnerLabel;
        DocFromLabel.Text = fromLabel;
        DocToLabel.Text = toLabel;
        DocPartialCheck.Visibility = showOrder ? Visibility.Visible : Visibility.Collapsed;
        DocHuCombo.IsEnabled = isDraft;

        if (!showFrom)
        {
            DocFromCombo.SelectedItem = null;
        }

        if (!showTo)
        {
            DocToCombo.SelectedItem = null;
        }

        DocHeaderSaveButton.Visibility = showPartner || showOrder || showHu
            ? Visibility.Visible
            : Visibility.Collapsed;
        DocHeaderSaveButton.IsEnabled = isDraft;

        DocFromColumn.Visibility = showFrom ? Visibility.Visible : Visibility.Collapsed;
        DocToColumn.Visibility = showTo ? Visibility.Visible : Visibility.Collapsed;
        DocFromColumn.Header = fromLabel;
        DocToColumn.Header = toLabel;
    }

    private static string FormatDocHeader(Doc doc)
    {
        var createdAt = doc.CreatedAt.ToString("dd'/'MM'/'yyyy HH':'mm", CultureInfo.InvariantCulture);
        var closedAt = doc.ClosedAt.HasValue
            ? doc.ClosedAt.Value.ToString("dd'/'MM'/'yyyy HH':'mm", CultureInfo.InvariantCulture)
            : "-";
        return $"Номер: {doc.DocRef} | Тип: {DocTypeMapper.ToDisplayName(doc.Type)} | Статус: {DocTypeMapper.StatusToDisplayName(doc.Status)} | Создан: {createdAt} | Проведена: {closedAt}";
    }

    private void UpdateLineButtons()
    {
        var isDraft = _doc?.Status == DocStatus.Draft;
        var hasOrder = HasOrderBinding();
        var allowPartialEdit = hasOrder && _isPartialShipment;
        AddItemButton.IsEnabled = isDraft && !hasOrder;
        EditLineButton.IsEnabled = isDraft && _selectedDocLine != null && (!hasOrder || allowPartialEdit);
        DeleteLineButton.IsEnabled = isDraft && _selectedDocLine != null && !hasOrder;
        DocPartialCheck.IsEnabled = isDraft && hasOrder;
    }

    private void UpdateActionButtons()
    {
        var isDraft = _doc?.Status == DocStatus.Draft;
        var hasId = _doc?.Id > 0;
        var hasPartner = !IsPartnerRequired() || _doc?.PartnerId != null || DocPartnerCombo.SelectedItem != null;
        var hasShortage = _doc?.Type == DocType.Outbound && _hasOutboundShortage;
        DocCloseButton.IsEnabled = isDraft && hasId && hasPartner && !hasShortage;
        DocHeaderSaveButton.IsEnabled = isDraft && _hasUnsavedChanges;
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

        var partnerId = (DocPartnerCombo.SelectedItem as Partner)?.Id;
        var huCode = (DocHuCombo.SelectedItem as HuOption)?.Code;
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
                    return (_services.DataStore.GetLedgerBalance(itemId, fromLocation.Id), true);
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
        _huOptions.Clear();
        _huOptions.Add(new HuOption(null, "—"));

        if (!_services.HuRegistry.TryGetItems(out var items, out var error))
        {
            MessageBox.Show(error ?? "Не удалось прочитать реестр HU.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        foreach (var item in items
                     .Where(entry => string.Equals(entry.State, HuRegistryStates.Issued, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(item.Code))
            {
                continue;
            }

            _huOptions.Add(new HuOption(item.Code, item.Code));
        }

        if (_doc == null)
        {
            return;
        }

        var current = NormalizeHuCode(_doc.ShippingRef);
        if (!string.IsNullOrWhiteSpace(current) && _huOptions.All(option => !string.Equals(option.Code, current, StringComparison.OrdinalIgnoreCase)))
        {
            _huOptions.Add(new HuOption(current, $"{current} (занят)"));
        }
    }

    private void SetHuSelection(Doc doc)
    {
        var normalized = NormalizeHuCode(doc.ShippingRef);
        _suppressDirtyTracking = true;
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            DocHuCombo.SelectedItem = _huOptions.FirstOrDefault(option =>
                string.Equals(option.Code, normalized, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            DocHuCombo.SelectedItem = _huOptions.FirstOrDefault(option => option.Code == null)
                                      ?? _huOptions.FirstOrDefault();
        }
        _suppressDirtyTracking = false;
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

        ApplyLineHu(_doc.Type, (DocHuCombo.SelectedItem as HuOption)?.Code, ref fromHu, ref toHu);
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
                return true;
            case DocType.Move:
                if (fromLocation == null || toLocation == null)
                {
                    MessageBox.Show("Для перемещения выберите места хранения откуда/куда.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                if (fromLocation.Id == toLocation.Id)
                {
                    if (string.Equals(NormalizeHuValue(fromHu), NormalizeHuValue(toHu), StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show("Для перемещения места хранения должны быть разными. Если вы хотите упаковать в HU в том же месте - заполните HU.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                    return true;
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

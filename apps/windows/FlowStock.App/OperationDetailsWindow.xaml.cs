using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class OperationDetailsWindow : Window
{
    private static bool KmUiEnabled => false;
    private readonly AppServices _services;
    private readonly ObservableCollection<Location> _locations = new();
    private readonly ObservableCollection<Partner> _partners = new();
    private readonly List<Partner> _partnersAll = new();
    private readonly Dictionary<long, PartnerStatus> _partnerStatusesById = new();
    private readonly ObservableCollection<DocLineDisplay> _docLines = new();
    private readonly ObservableCollection<OrderOption> _orders = new();
    private readonly List<OrderOption> _ordersAll = new();
    private readonly ObservableCollection<HuOption> _huToOptions = new();
    private readonly ObservableCollection<HuOption> _huFromOptions = new();
    private readonly ObservableCollection<WriteOffReason> _writeOffReasons = new();
    private readonly ObservableCollection<OutboundHuCandidate> _outboundHuCandidates = new();
    private readonly Dictionary<long, double> _orderedQtyByOrderLine = new();
    private readonly long _docId;
    private Doc? _doc;
    private DocLineDisplay? _selectedDocLine;
    private OutboundHuCandidate? _selectedOutboundHu;
    private bool _suppressOrderSync;
    private bool _suppressPartnerFilter;
    private bool _suppressPartialSync;
    private bool _suppressDirtyTracking;
    private bool _isPartialShipment;
    private bool _autoFillFromBoundOrderInProgress;
    private readonly HashSet<string> _autoFillAttempts = new(StringComparer.Ordinal);
    private bool _hasUnsavedChanges;
    private bool _hasOutboundShortage;
    private int _outboundShortageCount;
    private bool _isLoadingDocLines;
    private const double QtyTolerance = 0.000001;

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
        DocPartnerCombo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler(DocPartnerCombo_TextChanged));
        DocOrderCombo.ItemsSource = _orders;
        DocHuCombo.ItemsSource = _huToOptions;
        DocHuFromCombo.ItemsSource = _huFromOptions;
        DocReasonCombo.ItemsSource = _writeOffReasons;
        DocFromCombo.SelectionChanged += DocFromCombo_SelectionChanged;
        DocToCombo.SelectionChanged += DocToCombo_SelectionChanged;
        OutboundHuGrid.ItemsSource = _outboundHuCandidates;

        LoadWriteOffReasons();
        LoadCatalog();
        LoadOrders();
        LoadDoc();
    }

    public IEnumerable<Location> RowLocationOptions => _locations;

    private async void OperationDetailsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            if (DocLinesGrid.IsKeyboardFocusWithin
                && GetSelectedDocLines().Count > 0
                && IsDocEditable())
            {
                e.Handled = true;
                DocDeleteLine_Click(DocLinesGrid, new RoutedEventArgs());
            }

            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter)
        {
            e.Handled = true;
            await TryCloseCurrentDocAsync();
        }
    }

    private void LoadWriteOffReasons()
    {
        _writeOffReasons.Clear();
        var reasons = _services.WpfCatalogApi.TryGetWriteOffReasons(out var apiReasons)
            ? apiReasons
            : new[]
            {
                new WriteOffReason { Code = "DAMAGED", Name = "Повреждено" },
                new WriteOffReason { Code = "EXPIRED", Name = "Просрочено" },
                new WriteOffReason { Code = "DEFECT", Name = "Брак" },
                new WriteOffReason { Code = "SAMPLE", Name = "Проба" },
                new WriteOffReason { Code = "PRODUCTION", Name = "Производство" },
                new WriteOffReason { Code = "OTHER", Name = "Прочее" }
            };

        foreach (var reason in reasons.OrderBy(reason => reason.Name, StringComparer.OrdinalIgnoreCase))
        {
            _writeOffReasons.Add(reason);
        }
    }

    private void LoadCatalog()
    {
        _locations.Clear();
        var locations = _services.WpfReadApi.TryGetLocations(out var apiLocations)
            ? apiLocations
            : Array.Empty<Location>();
        foreach (var location in locations)
        {
            _locations.Add(location);
        }

        _partnersAll.Clear();
        _partnerStatusesById.Clear();
        var partners = _services.WpfReadApi.TryGetPartners(out var apiPartners)
            ? apiPartners
            : Array.Empty<Partner>();
        foreach (var partner in partners)
        {
            _partnersAll.Add(partner);
        }
        if (_services.WpfPartnerApi.TryGetPartners(out var partnerStatusEntries))
        {
            foreach (var entry in partnerStatusEntries)
            {
                _partnerStatusesById[entry.Partner.Id] = entry.Status;
                if (_partnersAll.All(partner => partner.Id != entry.Partner.Id))
                {
                    _partnersAll.Add(entry.Partner);
                }
            }
        }
        ApplyPartnerFilter();
    }

    private void LoadOrders()
    {
        _ordersAll.Clear();
        var isProductionReceipt = _doc?.Type == DocType.ProductionReceipt;
        var orders = _services.WpfReadApi.TryGetOrders(includeInternal: true, search: null, out var apiOrders)
            ? apiOrders
            : Array.Empty<Order>();
        foreach (var order in orders)
        {
            if (order.Status == OrderStatus.Shipped)
            {
                continue;
            }

            if (isProductionReceipt)
            {
                var hasReceiptRemaining = GetOrderReceiptRemaining(order.Id)
                    .Any(line => line.QtyRemaining > QtyTolerance);
                if (!hasReceiptRemaining)
                {
                    continue;
                }
            }

            _ordersAll.Add(new OrderOption(order.Id, order.OrderRef, order.Type, order.PartnerId, order.PartnerDisplay));
        }

        RefreshOrderList();
    }

    private void RefreshOrderList()
    {
        _orders.Clear();
        var partnerId = ResolveDocPartnerFromInput()?.Id;
        foreach (var order in _ordersAll)
        {
            if (_doc?.Type == DocType.Outbound && order.Type != OrderType.Customer)
            {
                continue;
            }

            if (partnerId.HasValue && order.PartnerId != partnerId.Value)
            {
                continue;
            }

            _orders.Add(order);
        }
    }

    private void LoadDoc()
    {
        _doc = _services.WpfReadApi.TryGetDoc(_docId, out var apiDoc)
            ? apiDoc
            : null;
        if (_doc == null)
        {
            MessageBox.Show("Операция не найдена.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
            return;
        }

        // Перезагружаем список заказов с учетом типа документа:
        // для PRD показываем только заказы с ненулевым receipt_remaining.
        LoadOrders();

        Title = $"Операция: {_doc.DocRef} ({DocTypeMapper.ToDisplayName(_doc.Type)})";
        LoadDocLines();
        UpdateDocView();
        TriggerAutoFillBoundOrderIfNeeded();
    }

    private void LoadDocLines()
    {
        _isLoadingDocLines = true;
        _docLines.Clear();
        var locationLookup = _locations
            .GroupBy(location => location.Code)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var packagingLookup = new Dictionary<long, IReadOnlyList<ItemPackaging>>();
        var itemsById = (_services.WpfReadApi.TryGetItems(null, out var apiItems)
                ? apiItems
                : Array.Empty<Item>())
            .ToDictionary(item => item.Id, item => item);

        var lines = _services.WpfReadApi.TryGetDocLines(_docId, out var apiLines)
            ? apiLines
            : Array.Empty<DocLineView>();
        var isOutbound = _doc?.Type == DocType.Outbound;
        var isInventory = _doc?.Type == DocType.Inventory;
        var isProductionReceipt = _doc?.Type == DocType.ProductionReceipt;
        var isEditable = IsDocEditable();
        var checkOutboundAvailability = isOutbound && isEditable;
        var receiptRemaining = new Dictionary<long, double>();
        if (isProductionReceipt && _doc?.OrderId.HasValue == true)
        {
            receiptRemaining = GetOrderReceiptRemaining(_doc.OrderId.Value)
                .ToDictionary(entry => entry.OrderLineId, entry => entry.QtyRemaining);
        }
        var availableByItem = checkOutboundAvailability
            ? GetItemAvailability()
            : new Dictionary<long, double>();
        var stockRows = isInventory && _services.WpfReadApi.TryGetStockRows(null, out var apiStockRows)
            ? apiStockRows
            : Array.Empty<StockRow>();
        var requiredByItem = checkOutboundAvailability
            ? lines.Where(line => line.Qty > 0)
                .GroupBy(line => line.ItemId)
                .ToDictionary(group => group.Key, group => group.Sum(line => line.Qty))
            : new Dictionary<long, double>();
        var shortageByItem = new Dictionary<long, double>();
        if (checkOutboundAvailability)
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
            var hasShortage = checkOutboundAvailability && shortageByItem.ContainsKey(line.ItemId);
            var huDisplay = ResolveLineHuDisplay(_doc?.Type ?? DocType.Inbound, line);
            var isMarked = KmUiEnabled && itemsById.TryGetValue(line.ItemId, out var item) && item.IsMarked;
            var kmDisplay = string.Empty;
            var kmEnabled = false;
            var inventoryDbQty = (double?)null;
            var inventoryDiffQty = (double?)null;
            var hasInventoryDiff = false;
            if (isInventory)
            {
                var locationId = ResolveLocationId(line.ToLocation, locationLookup);
                if (locationId.HasValue)
                {
                    var huCode = NormalizeHuValue(line.ToHu);
                    var locationCode = locationLookup.Values.FirstOrDefault(location => location.Id == locationId.Value)?.Code;
                    if (!string.IsNullOrWhiteSpace(locationCode) && stockRows.Count > 0)
                    {
                        inventoryDbQty = stockRows
                            .Where(row => row.ItemId == line.ItemId
                                          && string.Equals(row.LocationCode, locationCode, StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(NormalizeHuValue(row.Hu), huCode, StringComparison.OrdinalIgnoreCase))
                            .Sum(row => row.Qty);
                    }
                    else
                    {
                        inventoryDbQty = 0;
                    }
                    inventoryDiffQty = line.Qty - inventoryDbQty.Value;
                    hasInventoryDiff = Math.Abs(inventoryDiffQty.Value) > 0.000001;
                }
            }
            var orderLineDisplay = string.Empty;
            var orderLineHint = string.Empty;
            if (line.OrderLineId.HasValue)
            {
                var remaining = receiptRemaining.TryGetValue(line.OrderLineId.Value, out var value) ? value : 0;
                orderLineDisplay = FormatQty(remaining);
                orderLineHint = $"Осталось принять: {FormatQty(remaining)}";
            }

            _docLines.Add(new DocLineDisplay
            {
                Id = line.Id,
                ItemId = line.ItemId,
                OrderLineId = line.OrderLineId,
                ItemName = line.ItemName,
                QtyBase = line.Qty,
                QtyInput = line.QtyInput,
                UomCode = line.UomCode,
                BaseUom = line.BaseUom,
                InputQtyDisplay = FormatQty(inputQty),
                InputUomDisplay = FormatInputUomDisplay(line.UomCode, baseUom, selectedPackaging),
                BaseQtyDisplay = FormatBaseQty(line),
                AvailableQty = checkOutboundAvailability
                    ? (availableByItem.TryGetValue(line.ItemId, out var qty) ? qty : 0)
                    : null,
                HasShortage = hasShortage,
                ShortageQty = hasShortage ? shortageByItem[line.ItemId] : null,
                InventoryDbQty = inventoryDbQty,
                InventoryDiffQty = inventoryDiffQty,
                InventoryDbQtyDisplay = inventoryDbQty.HasValue ? FormatQty(inventoryDbQty.Value) : string.Empty,
                InventoryDiffQtyDisplay = inventoryDiffQty.HasValue ? FormatQty(inventoryDiffQty.Value) : string.Empty,
                HasInventoryDiff = hasInventoryDiff,
                IsMarked = isMarked,
                OrderLineDisplay = orderLineDisplay,
                OrderLineHint = orderLineHint,
                PackSingleHu = line.PackSingleHu,
                KmDisplay = kmDisplay,
                KmDistributeEnabled = kmEnabled,
                HuDisplay = huDisplay,
                FromLocationId = ResolveLocationId(line.FromLocation, locationLookup),
                ToLocationId = ResolveLocationId(line.ToLocation, locationLookup),
                FromHu = NormalizeHuValue(line.FromHu),
                ToHu = NormalizeHuValue(line.ToHu),
                FromLocation = FormatLocationDisplay(line.FromLocation, locationLookup),
                ToLocation = FormatLocationDisplay(line.ToLocation, locationLookup)
            });
        }

        _hasOutboundShortage = checkOutboundAvailability && shortageByItem.Count > 0;
        _outboundShortageCount = checkOutboundAvailability ? shortageByItem.Count : 0;
        _selectedDocLine = null;
        UpdateLineButtons();
        UpdateAvailabilityStatus();
        UpdateActionButtons();
        LoadOutboundHuCandidates();
        _isLoadingDocLines = false;
    }

    private static string? FormatLocationDisplay(string? code, IReadOnlyDictionary<string, Location> lookup)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return lookup.TryGetValue(code, out var location) ? location.DisplayName : code;
    }

    private static string? ResolveLineHuDisplay(DocType type, DocLineView line)
    {
        var fromHu = NormalizeHuValue(line.FromHu);
        var toHu = NormalizeHuValue(line.ToHu);

        return type switch
        {
            DocType.Inbound or DocType.Inventory or DocType.ProductionReceipt => toHu,
            DocType.Outbound or DocType.WriteOff => fromHu,
            DocType.Move => ResolveMoveHuDisplay(fromHu, toHu),
            _ => toHu ?? fromHu
        };
    }

    private static string? ResolveMoveHuDisplay(string? fromHu, string? toHu)
    {
        if (string.IsNullOrWhiteSpace(fromHu))
        {
            return toHu;
        }

        if (string.IsNullOrWhiteSpace(toHu))
        {
            return fromHu;
        }

        if (string.Equals(fromHu, toHu, StringComparison.OrdinalIgnoreCase))
        {
            return toHu;
        }

        return $"{fromHu} -> {toHu}";
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
        _selectedDocLine = GetSelectedDocLines().Count == 1
            ? DocLinesGrid.SelectedItem as DocLineDisplay
            : null;
        UpdateLineButtons();
        LoadOutboundHuCandidates();
    }

    private void OutboundHuGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedOutboundHu = OutboundHuGrid.SelectedItem as OutboundHuCandidate;
        UpdateOutboundHuButton();
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
            DocDeleteLine_Click(sender, new RoutedEventArgs());
        }
    }

    private void DocLinesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsDocEditable() || _selectedDocLine == null)
        {
            return;
        }

        DocEditLine_Click(sender, new RoutedEventArgs());
    }

    private async void DocClose_Click(object sender, RoutedEventArgs e)
    {
        await TryCloseCurrentDocAsync();
    }

    private async void DocRecount_Click(object sender, RoutedEventArgs e)
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

        var result = await _services.WpfDocumentRuntimeApi.TryMarkDocForRecountAsync(_doc.Id);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.Error ?? "Не удалось отправить инвентаризацию на пересчет.", "Инвентаризация", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        MessageBox.Show("Инвентаризация отправлена на пересчет.", "Инвентаризация", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private async Task TryCloseCurrentDocAsync()
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

        if (_hasUnsavedChanges && !await TrySaveHeaderAsync())
        {
            return;
        }

        var doc = _doc;
        if (doc == null)
        {
            MessageBox.Show("Операция не выбрана.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_docLines.Count == 0)
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

        if (!ConfirmCapacityOverrideIfNeeded(doc))
        {
            return;
        }

        await TryCloseCurrentDocViaServerAsync(doc);
    }

    private bool ConfirmCapacityOverrideIfNeeded(Doc doc)
    {
        if (doc.Type is not (DocType.Inbound or DocType.ProductionReceipt))
        {
            return true;
        }

        var warnings = BuildProjectedCapacityWarnings();
        if (warnings.Count == 0)
        {
            return true;
        }

        var message = "После проведения будет превышен лимит HU-мест в локациях:\n\n"
                      + string.Join("\n", warnings)
                      + "\n\nПродолжить проведение в режиме override?";
        var firstConfirm = MessageBox.Show(
            message,
            "Операция",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (firstConfirm != MessageBoxResult.Yes)
        {
            return false;
        }

        var secondConfirm = MessageBox.Show(
            "Подтвердите override еще раз: провести документ, даже если лимит HU-мест превышен?",
            "Операция",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return secondConfirm == MessageBoxResult.Yes;
    }

    private List<string> BuildProjectedCapacityWarnings()
    {
        var limitedLocations = _locations
            .Where(location => location.AutoHuDistributionEnabled && location.MaxHuSlots.HasValue && location.MaxHuSlots.Value > 0)
            .ToDictionary(location => location.Id, location => location);
        if (limitedLocations.Count == 0)
        {
            return new List<string>();
        }

        var locationIdByCode = _locations
            .Where(location => !string.IsNullOrWhiteSpace(location.Code))
            .ToDictionary(location => location.Code, location => location.Id, StringComparer.OrdinalIgnoreCase);

        var huBalances = new Dictionary<(long LocationId, string HuCode), double>();
        foreach (var row in GetStockRows())
        {
            if (row.Qty <= 0 || string.IsNullOrWhiteSpace(row.LocationCode))
            {
                continue;
            }

            if (!locationIdByCode.TryGetValue(row.LocationCode, out var locationId) || !limitedLocations.ContainsKey(locationId))
            {
                continue;
            }

            var huCode = NormalizeHuValue(row.Hu);
            if (string.IsNullOrWhiteSpace(huCode))
            {
                continue;
            }

            var key = (locationId, huCode);
            huBalances[key] = huBalances.TryGetValue(key, out var current)
                ? current + row.Qty
                : row.Qty;
        }

        if (_doc?.Type is DocType.Inbound or DocType.ProductionReceipt)
        {
            foreach (var line in _docLines)
            {
                if (!line.ToLocationId.HasValue || line.QtyBase <= QtyTolerance)
                {
                    continue;
                }

                if (!limitedLocations.ContainsKey(line.ToLocationId.Value))
                {
                    continue;
                }

                var huCode = NormalizeHuValue(line.ToHu);
                if (string.IsNullOrWhiteSpace(huCode))
                {
                    continue;
                }

                var key = (line.ToLocationId.Value, huCode);
                huBalances[key] = huBalances.TryGetValue(key, out var current)
                    ? current + line.QtyBase
                    : line.QtyBase;
            }
        }

        var occupiedCountByLocation = huBalances
            .Where(entry => entry.Value > QtyTolerance)
            .GroupBy(entry => entry.Key.LocationId)
            .ToDictionary(group => group.Key, group => group.Count());

        var warnings = new List<string>();
        foreach (var location in limitedLocations.Values.OrderBy(location => location.Code, StringComparer.OrdinalIgnoreCase))
        {
            var occupied = occupiedCountByLocation.TryGetValue(location.Id, out var count) ? count : 0;
            var limit = location.MaxHuSlots ?? 0;
            if (occupied <= limit)
            {
                continue;
            }

            warnings.Add($"{location.DisplayName}: занято {occupied}, лимит {limit}.");
        }

        return warnings;
    }

    private async Task TryCloseCurrentDocViaServerAsync(Doc doc)
    {
        var result = await _services.WpfCloseDocuments.CloseAsync(doc);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.Message, "Операция", MessageBoxButton.OK, ResolveServerCloseMessageImage(result.Kind));
            return;
        }

        LoadDoc();
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            MessageBox.Show(result.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        Close();
    }

    private static MessageBoxImage ResolveServerCloseMessageImage(WpfCloseDocumentResultKind kind)
    {
        return kind switch
        {
            WpfCloseDocumentResultKind.ValidationFailed => MessageBoxImage.Warning,
            WpfCloseDocumentResultKind.NotFound => MessageBoxImage.Warning,
            WpfCloseDocumentResultKind.EventConflict => MessageBoxImage.Warning,
            WpfCloseDocumentResultKind.ServerRejected => MessageBoxImage.Warning,
            _ => MessageBoxImage.Error
        };
    }

    private async void DocAddLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDraftDocSelected())
        {
            return;
        }

        if (_hasUnsavedChanges && !await TrySaveHeaderAsync())
        {
            return;
        }

        if (HasOrderBinding())
        {
            LogOutboundOrderBoundInfo("manual add blocked: order-bound outbound");
            MessageBox.Show(
                "Для отгрузки с привязанным заказом ручное добавление строк отключено.",
                "Операция",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        IEnumerable<Item>? filteredItems = null;
        IReadOnlyDictionary<long, double>? pickerAvailabilityByItem = null;
        if (_doc?.Type == DocType.Move)
        {
            if (DocFromCombo.SelectedItem is not Location selectedFromLocation)
            {
                MessageBox.Show("Выберите место хранения (откуда).", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedFromHu = (DocHuFromCombo.SelectedItem as HuOption)?.Code;
            filteredItems = GetAvailableItemsByLocationAndHu(selectedFromLocation.Id, selectedFromHu);
            if (!filteredItems.Any())
            {
                MessageBox.Show("Нет доступных товаров для выбранного места хранения и HU.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        else if (_doc?.Type == DocType.Outbound)
        {
            var selectedHu = GetSelectedHuCode(DocHuCombo);
            if (DocFromCombo.SelectedItem is Location selectedFromLocation)
            {
                filteredItems = GetAvailableItemsByLocationAndHu(selectedFromLocation.Id, selectedHu);
            }
            else
            {
                filteredItems = GetAvailableItemsForOutboundHu(selectedHu);
            }
            if (!filteredItems.Any())
            {
                MessageBox.Show("Нет доступных товаров для выбранного HU.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        else if (_doc?.Type == DocType.WriteOff)
        {
            pickerAvailabilityByItem = GetRemainingWriteOffAvailabilityByItem();
            filteredItems = GetAvailableItemsForWriteOff()
                .Where(item => pickerAvailabilityByItem.TryGetValue(item.Id, out var qty) && qty > QtyTolerance)
                .ToList();
            if (!filteredItems.Any())
            {
                MessageBox.Show("Нет доступных товаров для списания.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        var picker = new ItemPickerWindow(_services, filteredItems, pickerAvailabilityByItem)
        {
            Owner = this
        };
        if (picker.ShowDialog() != true || picker.SelectedItem is not Item item)
        {
            return;
        }

        var packagings = _services.WpfPackagingApi.TryGetPackagings(item.Id, includeInactive: false, out var apiPackagings)
            ? apiPackagings
            : Array.Empty<ItemPackaging>();
        var defaultUomCode = ResolveDefaultUomCode(item, packagings);
        Location? fromLocation;
        Location? toLocation;
        string? fromHu;
        string? toHu;
        double? availableQty;
        bool showAvailableLabel;

        if (_doc?.Type == DocType.WriteOff)
        {
            availableQty = GetRemainingWriteOffQty(item.Id, null, null);
            fromLocation = null;
            toLocation = null;
            fromHu = null;
            toHu = null;
            showAvailableLabel = true;
        }
        else
        {
            (availableQty, showAvailableLabel) = GetAvailableQtyForDialog(item.Id);
            if (!TryGetLineLocations(out fromLocation, out toLocation, out fromHu, out toHu))
            {
                return;
            }
        }

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

        if (_doc?.Type == DocType.WriteOff)
        {
            await TryAddWriteOffLinesByHuViaServerAsync(item, qtyBase, uomCode);
            return;
        }

        await TryAddLineViaServerAsync(item, qtyBase, qtyInput, uomCode, fromLocation, toLocation, fromHu, toHu);
    }

    private async Task TryAddWriteOffLinesByHuViaServerAsync(
        Item item,
        double qtyBase,
        string? uomCode)
    {
        if (_doc == null)
        {
            MessageBox.Show("Операция не выбрана.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sources = GetWriteOffSources(item.Id);
        if (sources.Count == 0)
        {
            MessageBox.Show("Нет доступного остатка для списания.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var available = sources.Sum(source => source.Qty);
        if (available + QtyTolerance < qtyBase)
        {
            MessageBox.Show(
                $"Недостаточно остатка: доступно {FormatQty(available)}.",
                "Операция",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var remaining = qtyBase;
        var contexts = new List<WpfAddDocLineContext>();
        foreach (var source in sources)
        {
            if (remaining <= QtyTolerance)
            {
                break;
            }

            var take = Math.Min(source.Qty, remaining);
            if (take <= QtyTolerance)
            {
                continue;
            }

            contexts.Add(new WpfAddDocLineContext(
                item.Id,
                item.Barcode,
                null,
                take,
                null,
                uomCode,
                source.LocationId,
                null,
                source.HuCode,
                null));
            remaining -= take;
        }

        if (remaining > QtyTolerance || contexts.Count == 0)
        {
            MessageBox.Show("Не удалось распределить количество по остаткам HU.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = await _services.WpfBatchAddDocLines.AddLinesBatchAsync(_doc, contexts);
        LoadDoc();
        if (!result.IsSuccess)
        {
            MessageBox.Show(
                BuildBatchFailureMessage(result),
                "Операция",
                MessageBoxButton.OK,
                ResolveServerAddLineMessageImage(result.Kind));
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            MessageBox.Show(result.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async Task TryAddLineViaServerAsync(
        Item item,
        double qtyBase,
        double? qtyInput,
        string? uomCode,
        Location? fromLocation,
        Location? toLocation,
        string? fromHu,
        string? toHu)
    {
        var doc = _doc;
        if (doc == null)
        {
            MessageBox.Show("Операция не выбрана.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = await _services.WpfAddDocLines.AddLineAsync(
            doc,
            new WpfAddDocLineContext(
                item.Id,
                item.Barcode,
                null,
                qtyBase,
                qtyInput,
                uomCode,
                fromLocation?.Id,
                toLocation?.Id,
                fromHu,
                toHu));

        if (result.ShouldRefresh)
        {
            LoadDoc();
        }

        if (result.IsSuccess)
        {
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                MessageBox.Show(result.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return;
        }

        MessageBox.Show(result.Message, "Операция", MessageBoxButton.OK, ResolveServerAddLineMessageImage(result.Kind));
    }

    private static MessageBoxImage ResolveServerAddLineMessageImage(WpfAddDocLineResultKind kind)
    {
        return kind switch
        {
            WpfAddDocLineResultKind.ValidationFailed => MessageBoxImage.Warning,
            WpfAddDocLineResultKind.NotFound => MessageBoxImage.Warning,
            WpfAddDocLineResultKind.EventConflict => MessageBoxImage.Warning,
            WpfAddDocLineResultKind.ServerRejected => MessageBoxImage.Warning,
            _ => MessageBoxImage.Error
        };
    }

    private async void DocDeleteLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDraftDocSelected())
        {
            return;
        }

        var selectedLineIds = GetSelectedDocLines()
            .Select(line => line.Id)
            .Distinct()
            .ToList();
        if (selectedLineIds.Count == 0)
        {
            MessageBox.Show("Выберите хотя бы одну строку.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var result = await _services.WpfDeleteDocLines.DeleteLinesAsync(_doc!, selectedLineIds);
            if (result.ShouldRefresh)
            {
                LoadDoc();
            }

            if (result.IsSuccess)
            {
                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    MessageBox.Show(result.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return;
            }

            MessageBox.Show(result.Message, "Операция", MessageBoxButton.OK, ResolveServerDeleteLineMessageImage(result.Kind));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void KmCodes_Click(object sender, RoutedEventArgs e)
    {
        if (!KmUiEnabled)
        {
            MessageBox.Show("Маркировка в WPF временно заморожена.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_doc == null || _selectedDocLine == null)
        {
            MessageBox.Show("Выберите строку документа.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_doc.Type != DocType.ProductionReceipt && _doc.Type != DocType.Outbound)
        {
            return;
        }

        var line = FindCurrentDocLine(_selectedDocLine.Id);
        if (line == null)
        {
            MessageBox.Show("Строка не найдена.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var item = FindItem(line.ItemId);
        if (item == null || !item.IsMarked)
        {
            MessageBox.Show("Товар не маркируемый.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_doc.Type == DocType.ProductionReceipt)
        {
            if (!line.ToLocationId.HasValue)
            {
                MessageBox.Show("Выберите локацию приемки.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(line.ToHu))
            {
                MessageBox.Show("Для выпуска продукции требуется HU.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var window = new KmAssignReceiptWindow(_services, _doc, line, item)
            {
                Owner = this
            };
            window.ShowDialog();
            LoadDocLines();
        }
        else if (_doc.Type == DocType.Outbound)
        {
            var window = new KmAssignShipmentWindow(_services, _doc, line, item)
            {
                Owner = this
            };
            window.ShowDialog();
            LoadDocLines();
        }
    }

    private void KmDistribute_Click(object sender, RoutedEventArgs e)
    {
        if (!KmUiEnabled)
        {
            MessageBox.Show("Маркировка в WPF временно заморожена.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_doc == null || (_doc.Type != DocType.ProductionReceipt && _doc.Type != DocType.Outbound))
        {
            return;
        }

        if (sender is not FrameworkElement element || element.DataContext is not DocLineDisplay lineDisplay)
        {
            return;
        }

        var line = FindCurrentDocLine(lineDisplay.Id);
        if (line == null)
        {
            MessageBox.Show("Строка не найдена.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var item = FindItem(line.ItemId);
        if (item == null || !item.IsMarked)
        {
            MessageBox.Show("Товар не маркируемый.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_doc.Type == DocType.ProductionReceipt)
        {
            if (!line.ToLocationId.HasValue)
            {
                MessageBox.Show("Выберите локацию приемки.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(line.ToHu))
            {
                MessageBox.Show("Для выпуска продукции требуется HU.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        try
        {
            if (_doc.Type == DocType.ProductionReceipt)
            {
                if (_doc.OrderId.HasValue)
                {
                    var assigned = _services.Km.AssignCodesToReceipt(_doc.Id, line, item, null, _doc.OrderId.Value);
                    MessageBox.Show($"Распределено кодов: {assigned}.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    var window = new KmAssignReceiptWindow(_services, _doc, line, item)
                    {
                        Owner = this
                    };
                    window.ShowDialog();
                }
            }
            else
            {
                if (_doc.OrderId.HasValue)
                {
                    var assigned = _services.Km.AssignCodesToShipment(_doc.Id, line, item, _doc.OrderId);
                    MessageBox.Show($"Распределено кодов: {assigned}.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    var window = new KmAssignShipmentWindow(_services, _doc, line, item)
                    {
                        Owner = this
                    };
                    window.ShowDialog();
                }
            }

            LoadDocLines();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Маркировка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DocEditLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDraftDocSelected())
        {
            return;
        }

        if (HasOrderBinding() && !_isPartialShipment)
        {
            LogOutboundOrderBoundInfo("line update blocked: order-bound outbound allows qty edit only in partial shipment mode");
            MessageBox.Show(
                "Изменение количества доступно только в режиме 'Частичная отгрузка'.",
                "Операция",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_selectedDocLine == null)
        {
            MessageBox.Show("Выберите строку.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var orderedQty = 0.0;
        if (HasOrderBinding())
        {
            if (!_selectedDocLine.OrderLineId.HasValue
                || !TryGetOrderedQty(_selectedDocLine.OrderLineId.Value, out orderedQty))
            {
                MessageBox.Show("Не удалось найти количество из заказа.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var item = FindItem(_selectedDocLine.ItemId);
        if (item == null)
        {
            MessageBox.Show("Товар не найден.", "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var packagings = _services.WpfPackagingApi.TryGetPackagings(item.Id, includeInactive: false, out var apiPackagings)
            ? apiPackagings
            : Array.Empty<ItemPackaging>();
        var defaultQty = _selectedDocLine.QtyInput ?? _selectedDocLine.QtyBase;
        var defaultUom = string.IsNullOrWhiteSpace(_selectedDocLine.UomCode) ? "BASE" : _selectedDocLine.UomCode;
        var (availableQty, showAvailableLabel) = GetAvailableQtyForDialog(item.Id, _selectedDocLine);
        var qtyDialog = new QuantityUomDialog(item.BaseUom, packagings, defaultQty, defaultUom, availableQty, showAvailableLabel)
        {
            Owner = this
        };
        if (qtyDialog.ShowDialog() != true)
        {
            return;
        }

        if (!TryValidateReceiptQty(_selectedDocLine, qtyDialog.QtyBase))
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

        var doc = _doc;
        if (doc == null)
        {
            MessageBox.Show("Операция не выбрана.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var currentLine = FindCurrentDocLine(_selectedDocLine.Id);
        if (currentLine == null)
        {
            MessageBox.Show("Строка не найдена.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = await _services.WpfUpdateDocLines.UpdateLineAsync(
            doc,
            new WpfUpdateDocLineContext(
                currentLine.Id,
                qtyDialog.QtyBase,
                qtyDialog.UomCode,
                currentLine.FromLocationId,
                currentLine.ToLocationId,
                currentLine.FromHu,
                currentLine.ToHu));

        if (result.ShouldRefresh)
        {
            LoadDoc();
        }

        if (result.IsSuccess)
        {
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                MessageBox.Show(result.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return;
        }

        MessageBox.Show(
            result.Message,
            "Операция",
            MessageBoxButton.OK,
            ResolveServerUpdateLineMessageImage(result.Kind));
    }

    private static MessageBoxImage ResolveServerUpdateLineMessageImage(WpfUpdateDocLineResultKind kind)
    {
        return kind switch
        {
            WpfUpdateDocLineResultKind.ValidationFailed => MessageBoxImage.Warning,
            WpfUpdateDocLineResultKind.NotFound => MessageBoxImage.Warning,
            WpfUpdateDocLineResultKind.EventConflict => MessageBoxImage.Warning,
            WpfUpdateDocLineResultKind.ServerRejected => MessageBoxImage.Warning,
            _ => MessageBoxImage.Error
        };
    }

    private static MessageBoxImage ResolveServerDeleteLineMessageImage(WpfDeleteDocLineResultKind kind)
    {
        return kind switch
        {
            WpfDeleteDocLineResultKind.ValidationFailed => MessageBoxImage.Warning,
            WpfDeleteDocLineResultKind.NotFound => MessageBoxImage.Warning,
            WpfDeleteDocLineResultKind.EventConflict => MessageBoxImage.Warning,
            WpfDeleteDocLineResultKind.ServerRejected => MessageBoxImage.Warning,
            _ => MessageBoxImage.Error
        };
    }

    private async void AssignHuButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDraftDocSelected())
        {
            return;
        }

        if (!SupportsLineHuAssignment())
        {
            return;
        }

        if (_doc == null)
        {
            MessageBox.Show("Документ не выбран.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedDisplays = GetSelectedDocLines();
        if (_doc.Type == DocType.Inbound)
        {
            if (selectedDisplays.Count > 0)
            {
                var selectedHuCode = GetSelectedHuCode(DocHuCombo);
                var selectedInboundLines = selectedDisplays
                    .Select(display => FindCurrentDocLine(display.Id))
                    .Where(line => line != null)
                    .Cast<DocLine>()
                    .ToList();
                if (selectedInboundLines.Count == 0)
                {
                    MessageBox.Show("Строки не найдены.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(selectedHuCode))
                {
                    if (!TryEnsureHuExists(selectedHuCode))
                    {
                        return;
                    }

                    if (selectedInboundLines.Count == 1)
                    {
                        await TryAssignHuToSingleLineAsync(selectedInboundLines[0], selectedHuCode);
                    }
                    else
                    {
                        await TryAssignHuToMultipleLinesAsync(selectedInboundLines, selectedHuCode);
                    }
                    return;
                }

                await TryAutoAssignInboundHuToLinesAsync(selectedInboundLines);
                return;
            }

            var inboundTargets = _docLines.ToList();
            if (inboundTargets.Count == 0)
            {
                MessageBox.Show("В документе нет строк для назначения HU.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var inboundLines = inboundTargets
                .Select(display => FindCurrentDocLine(display.Id))
                .Where(line => line != null)
                .Cast<DocLine>()
                .ToList();
            if (inboundLines.Count == 0)
            {
                MessageBox.Show("Строки не найдены.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await TryAutoAssignInboundHuToLinesAsync(inboundLines);
            return;
        }

        if (selectedDisplays.Count == 0)
        {
            MessageBox.Show("Выберите хотя бы одну строку.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedHu = GetSelectedHuCode(DocHuCombo);
        if (string.IsNullOrWhiteSpace(selectedHu))
        {
            MessageBox.Show("Выберите HU в шапке документа.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_doc.Type == DocType.ProductionReceipt && !TryEnsureHuExists(selectedHu))
        {
            return;
        }

        var selectedLines = selectedDisplays
            .Select(display => FindCurrentDocLine(display.Id))
            .Where(line => line != null)
            .Cast<DocLine>()
            .ToList();
        if (selectedLines.Count == 0)
        {
            MessageBox.Show("Строки не найдены.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (selectedLines.Count > 1)
        {
            await TryAssignHuToMultipleLinesAsync(selectedLines, selectedHu);
            return;
        }

        var line = selectedLines[0];
        var item = FindItem(line.ItemId);
        if (_doc.Type == DocType.ProductionReceipt
            && item?.MaxQtyPerHu is double maxQtyPerHu
            && maxQtyPerHu > 0)
        {
            try
            {
                if (await TryAssignProductionLineByCapacityAsync(line, selectedHu, maxQtyPerHu))
                {
                    LoadDoc();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        await TryAssignHuToSingleLineAsync(line, selectedHu);
    }

    private async Task TryAutoAssignInboundHuToLinesAsync(IReadOnlyList<DocLine> lines)
    {
        if (_doc == null || _doc.Type != DocType.Inbound || lines.Count == 0)
        {
            return;
        }

        var totalsByHu = GetHuTotals();
        var targetLineIds = lines.Select(line => line.Id).ToHashSet();
        var reservedInDoc = _docLines
            .Where(line => !targetLineIds.Contains(line.Id))
            .Select(line => NormalizeHuValue(line.ToHu))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var freeCodes = GetFreeHuCodes(totalsByHu)
            .Where(code => !reservedInDoc.Contains(code))
            .ToList();
        if (freeCodes.Count == 0)
        {
            MessageBox.Show("Нет свободных HU для назначения.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var sharedLines = lines
            .Where(line => line.PackSingleHu)
            .OrderBy(line => line.Id)
            .ToList();
        var separateLines = lines
            .Where(line => !line.PackSingleHu)
            .OrderBy(line => line.Id)
            .ToList();

        var requiredHuCount = separateLines.Count + (sharedLines.Count > 0 ? 1 : 0);
        if (freeCodes.Count < requiredHuCount)
        {
            MessageBox.Show(
                $"Недостаточно свободных HU: нужно {requiredHuCount}, доступно {freeCodes.Count}.",
                "Операция",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var allocatedCodes = new Queue<string>(freeCodes);
        var plannedByLineId = new Dictionary<long, string>();
        if (sharedLines.Count > 0)
        {
            var sharedHu = allocatedCodes.Dequeue();
            foreach (var line in sharedLines)
            {
                plannedByLineId[line.Id] = sharedHu;
            }
        }

        foreach (var line in separateLines)
        {
            plannedByLineId[line.Id] = allocatedCodes.Dequeue();
        }

        var assigned = 0;
        var failed = 0;
        foreach (var line in lines.OrderBy(line => line.Id))
        {
            if (!plannedByLineId.TryGetValue(line.Id, out var targetHu) || string.IsNullOrWhiteSpace(targetHu))
            {
                break;
            }

            if (!TryEnsureHuExists(targetHu))
            {
                failed += 1;
                continue;
            }

            var result = await _services.WpfDocumentRuntimeApi.TryAssignDocLineHuAsync(
                _doc.Id,
                line.Id,
                line.Qty,
                null,
                targetHu);
            if (!result.IsSuccess)
            {
                failed += 1;
                continue;
            }

            assigned += 1;
        }

        LoadDoc();
        if (assigned <= 0)
        {
            MessageBox.Show("Не удалось назначить HU выбранным строкам.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (assigned < lines.Count || failed > 0)
        {
            MessageBox.Show(
                $"HU назначены частично: {assigned} из {lines.Count}.",
                "Операция",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(
            $"Свободные HU назначены: {assigned} строк.",
            "Операция",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async Task TryAssignHuToSingleLineAsync(DocLine line, string selectedHu)
    {
        if (_doc == null)
        {
            return;
        }

        var targetFromHu = line.FromHu;
        var targetToHu = line.ToHu;
        ApplyLineHu(_doc.Type, selectedHu, ref targetFromHu, ref targetToHu);

        var sameHu = string.Equals(NormalizeHuValue(line.FromHu), NormalizeHuValue(targetFromHu), StringComparison.OrdinalIgnoreCase)
                     && string.Equals(NormalizeHuValue(line.ToHu), NormalizeHuValue(targetToHu), StringComparison.OrdinalIgnoreCase);

        var qtyDialog = new QuantityDialog(line.Qty)
        {
            Owner = this
        };
        if (qtyDialog.ShowDialog() != true)
        {
            return;
        }

        var qty = qtyDialog.Qty;
        if (qty <= 0 || qty > line.Qty + 0.000001)
        {
            MessageBox.Show($"Количество должно быть от 1 до {FormatQty(line.Qty)}.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (sameHu && qty < line.Qty - 0.000001)
        {
            MessageBox.Show("Выбранный HU уже назначен строке. Выберите другой HU.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_doc.Type == DocType.Outbound && IsFullServerLineLifecycleEnabled())
        {
            await TryApplyOutboundHuMutationViaServerAsync(
                line,
                qty,
                line.FromLocationId,
                targetFromHu,
                targetToHu,
                "Операция",
                "assign-hu");
            return;
        }

        var assignResult = await _services.WpfDocumentRuntimeApi.TryAssignDocLineHuAsync(_doc.Id, line.Id, qty, targetFromHu, targetToHu);
        if (!assignResult.IsSuccess)
        {
            MessageBox.Show(assignResult.Error ?? "Не удалось назначить HU через сервер.", "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadDoc();
    }

    private async Task TryAssignHuToMultipleLinesAsync(IReadOnlyList<DocLine> lines, string selectedHu)
    {
        if (_doc == null || lines.Count == 0)
        {
            return;
        }

        var failures = new List<string>();
        foreach (var line in lines)
        {
            var targetFromHu = line.FromHu;
            var targetToHu = line.ToHu;
            ApplyLineHu(_doc.Type, selectedHu, ref targetFromHu, ref targetToHu);

            if (_doc.Type == DocType.Outbound && IsFullServerLineLifecycleEnabled())
            {
                var ok = await TryApplyOutboundHuMutationViaServerAsync(
                    line,
                    line.Qty,
                    line.FromLocationId,
                    targetFromHu,
                    targetToHu,
                    "Операция",
                    "assign-hu-bulk");
                if (!ok)
                {
                    failures.Add(line.ItemId.ToString(CultureInfo.InvariantCulture));
                }
                continue;
            }

            var assignResult = await _services.WpfDocumentRuntimeApi.TryAssignDocLineHuAsync(
                _doc.Id,
                line.Id,
                line.Qty,
                targetFromHu,
                targetToHu);
            if (!assignResult.IsSuccess)
            {
                failures.Add(line.ItemId.ToString(CultureInfo.InvariantCulture));
            }
        }

        LoadDoc();
        if (failures.Count > 0)
        {
            MessageBox.Show(
                "HU назначен не для всех выбранных строк. Обновите документ и проверьте строки с ошибкой.",
                "Операция",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void AutoHuButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDraftDocSelected())
        {
            return;
        }

        if (_doc?.Type != DocType.ProductionReceipt)
        {
            return;
        }

        if (_hasUnsavedChanges && !await TrySaveHeaderAsync())
        {
            return;
        }

        var selectedLineIds = GetSelectedDocLines()
            .Select(line => line.Id)
            .Distinct()
            .ToList();

        var result = await _services.WpfDocumentRuntimeApi.TryAutoDistributeProductionReceiptHusAsync(
            _doc.Id,
            selectedLineIds.Count > 0 ? selectedLineIds : null);
        if (!result.IsSuccess)
        {
            var message = result.Error ?? "Не удалось выполнить автораспределение HU через сервер.";
            message += Environment.NewLine + Environment.NewLine
                       + "Можно продолжить вручную: назначить HU по строкам и при необходимости выбрать локацию приёмки в строке.";
            MessageBox.Show(message, "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LoadDoc();
        MessageBox.Show(
            $"Автораспределение по HU выполнено. Назначено HU: {result.UsedHuCount}.",
            "Операция",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async Task<bool> TryAssignProductionLineByCapacityAsync(DocLine line, string selectedHu, double maxQtyPerHu)
    {
        if (_doc == null)
        {
            return false;
        }

        var requiredHuCount = line.PackSingleHu
            ? 1
            : (int)Math.Ceiling(line.Qty / maxQtyPerHu);
        var huCodes = new List<string> { selectedHu };
        if (requiredHuCount > 1)
        {
            var totalsByHu = GetHuTotals();
            foreach (var freeCode in GetFreeHuCodes(totalsByHu))
            {
                if (string.Equals(freeCode, selectedHu, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                huCodes.Add(freeCode);
                if (huCodes.Count >= requiredHuCount)
                {
                    break;
                }
            }
        }

        if (huCodes.Count < requiredHuCount)
        {
            MessageBox.Show(
                $"Недостаточно свободных HU. Нужно: {requiredHuCount}, доступно: {huCodes.Count}.",
                "Операция",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var result = await _services.WpfDocumentRuntimeApi.TryDistributeProductionLineByHuCapacityAsync(_doc.Id, line.Id, maxQtyPerHu, huCodes);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.Error ?? "Не удалось распределить строку по HU через сервер.", "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        if (requiredHuCount > 1)
        {
            MessageBox.Show(
                $"Строка распределена по {requiredHuCount} HU ({BuildHuDistributionSummary(line.Qty, maxQtyPerHu)}).",
                "Операция",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        return true;
    }

    private static string BuildHuDistributionSummary(double qty, double maxQtyPerHu)
    {
        var fullCount = (int)Math.Floor(qty / maxQtyPerHu);
        var remainder = qty - fullCount * maxQtyPerHu;
        if (remainder <= 0.000001)
        {
            return $"{fullCount} x {FormatQty(maxQtyPerHu)}";
        }

        if (fullCount <= 0)
        {
            return $"1 x {FormatQty(remainder)}";
        }

        return $"{fullCount} x {FormatQty(maxQtyPerHu)} + 1 x {FormatQty(remainder)}";
    }

    private bool TryValidateReceiptQty(DocLineDisplay lineDisplay, double newQty)
    {
        if (_doc?.Type != DocType.ProductionReceipt || !lineDisplay.OrderLineId.HasValue)
        {
            return true;
        }

        if (!_doc.OrderId.HasValue)
        {
            MessageBox.Show("Для строки заказа требуется указать заказ в документе.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var remaining = GetOrderReceiptRemaining(_doc.OrderId.Value)
            .ToDictionary(entry => entry.OrderLineId, entry => entry.QtyRemaining);
        if (!remaining.TryGetValue(lineDisplay.OrderLineId.Value, out var limit))
        {
            MessageBox.Show("Строка заказа не найдена.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var total = _docLines
            .Where(line => line.OrderLineId == lineDisplay.OrderLineId)
            .Sum(line => line.Id == lineDisplay.Id ? newQty : line.QtyBase);
        if (total > limit + 0.000001)
        {
            MessageBox.Show($"Количество превышает остаток по заказу: доступно {FormatQty(limit)}.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private async void DocHeaderSave_Click(object sender, RoutedEventArgs e)
    {
        await TrySaveHeaderAsync();
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
        DocBatchBox.Text = _doc.ProductionBatchNo ?? string.Empty;
        DocCommentBox.Text = _doc.Comment ?? string.Empty;
        UpdatePartialUi();
        ApplyPartnerFilter(forceIncludePartnerId: _doc.PartnerId);
        DocPartnerCombo.SelectedItem = _partners.FirstOrDefault(p => p.Id == _doc.PartnerId);
        SelectOrderFromDoc(_doc);
        ApplyHeaderLocationsFromLines();
        LoadHuOptions();
        SetHuSelection(_doc);
        ApplyReasonSelection(_doc);
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

        var lines = _docLines
            .Select(line => new DocLine
            {
                Id = line.Id,
                DocId = _doc.Id,
                FromLocationId = line.FromLocationId,
                ToLocationId = line.ToLocationId
            })
            .ToList();
        if (lines.Count == 0)
        {
            return;
        }

        if (_doc.Type == DocType.Inbound || _doc.Type == DocType.ProductionReceipt)
        {
            return;
        }

        if (_doc.Type == DocType.Inventory)
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

    private async void PackSingleHuCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _doc.Type is not (DocType.ProductionReceipt or DocType.Inbound))
        {
            return;
        }

        if (!IsDocEditable())
        {
            LoadDocLines();
            return;
        }

        if (sender is not System.Windows.Controls.CheckBox checkBox || checkBox.DataContext is not DocLineDisplay lineDisplay)
        {
            return;
        }

        var result = await _services.WpfDocumentRuntimeApi.TrySetProductionLinePackSingleHuAsync(_doc.Id, lineDisplay.Id, checkBox.IsChecked == true);
        if (!result.IsSuccess)
        {
            LoadDocLines();
            MessageBox.Show(result.Error ?? "Не удалось сохранить настройку \"Общий HU\" через сервер.", "Операция", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadDocLines();
    }

    private void ApplyReasonSelection(Doc doc)
    {
        if (doc.Type != DocType.WriteOff)
        {
            DocReasonCombo.SelectedItem = null;
            return;
        }

        var selected = _writeOffReasons.FirstOrDefault(reason =>
            string.Equals(reason.Code, doc.ReasonCode, StringComparison.OrdinalIgnoreCase));
        if (selected == null && !string.IsNullOrWhiteSpace(doc.ReasonCode))
        {
            selected = new WriteOffReason
            {
                Code = doc.ReasonCode.Trim(),
                Name = doc.ReasonCode.Trim()
            };
            if (_writeOffReasons.All(reason => !string.Equals(reason.Code, selected.Code, StringComparison.OrdinalIgnoreCase)))
            {
                _writeOffReasons.Insert(0, selected);
            }
        }
        DocReasonCombo.SelectedItem = selected;
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
        if (_suppressOrderSync || _suppressPartnerFilter)
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

    private void DocBatchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressDirtyTracking || _doc?.Status != DocStatus.Draft)
        {
            return;
        }

        MarkHeaderDirty();
    }

    private void DocCommentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressDirtyTracking || _doc?.Status != DocStatus.Draft)
        {
            return;
        }

        MarkHeaderDirty();
    }

    private void DocReasonCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressDirtyTracking || _doc?.Status != DocStatus.Draft)
        {
            return;
        }

        MarkHeaderDirty();
    }

    private async void DocOrderCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
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

        if (_doc?.Type == DocType.ProductionReceipt)
        {
            await TryApplyReceiptOrderSelectionAsync(selected);
            UpdateLineButtons();
            return;
        }

        if (!selected.PartnerId.HasValue)
        {
            return;
        }

        var partner = _partners.FirstOrDefault(p => p.Id == selected.PartnerId.Value);
        if (partner == null)
        {
            return;
        }

        _suppressOrderSync = true;
        DocPartnerCombo.SelectedItem = partner;
        _suppressOrderSync = false;
        UpdatePartnerLock();
        ResetPartialMode();
        if (_doc?.Type == DocType.Outbound)
        {
            MarkHeaderDirty();
            LogOutboundOrderBoundInfo($"auto fill triggered: order selection changed; order_id={selected.Id}; mode=server");
            await TryApplyOrderSelectionAsync(selected);
            UpdateLineButtons();
            UpdateActionButtons();
            return;
        }

        await TryApplyOrderSelectionAsync(selected);
        UpdateLineButtons();
    }

    private async void DocOrderCombo_KeyUp(object sender, KeyEventArgs e)
    {
        if (_suppressOrderSync)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(DocOrderCombo.Text))
        {
            DocOrderCombo.SelectedItem = null;
            await ClearDocOrderBindingAsync();
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

    private async void DocOrderClear_Click(object sender, RoutedEventArgs e)
    {
        _suppressOrderSync = true;
        DocOrderCombo.SelectedItem = null;
        DocOrderCombo.Text = string.Empty;
        _suppressOrderSync = false;
        await ClearDocOrderBindingAsync();
        ResetPartialMode();
        UpdatePartnerLock();
        UpdateLineButtons();
        UpdateActionButtons();
    }

    private async void DocPartialCheck_Changed(object sender, RoutedEventArgs e)
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
            LogOutboundOrderBoundInfo($"auto fill triggered: partial shipment changed; order_id={_doc.OrderId.Value}; partial={_isPartialShipment}");
            if (TryResolveOrder(out var orderOption) && orderOption != null)
            {
                await TryApplyOrderSelectionAsync(orderOption);
            }
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

    private async void DocToLocationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingDocLines || _doc == null || !IsDocEditable())
        {
            return;
        }

        if (_doc.Type is not (DocType.Inbound or DocType.ProductionReceipt))
        {
            return;
        }

        if (sender is not System.Windows.Controls.ComboBox combo || combo.DataContext is not DocLineDisplay lineDisplay)
        {
            return;
        }

        if (!combo.IsDropDownOpen && !combo.IsKeyboardFocusWithin)
        {
            return;
        }

        if (combo.SelectedItem is not Location targetLocation)
        {
            return;
        }

        if (lineDisplay.ToLocationId == targetLocation.Id)
        {
            return;
        }

        var currentLine = FindCurrentDocLine(lineDisplay.Id);
        if (currentLine == null)
        {
            return;
        }

        var result = await _services.WpfUpdateDocLines.UpdateLineAsync(
            _doc,
            new WpfUpdateDocLineContext(
                currentLine.Id,
                currentLine.Qty,
                currentLine.UomCode,
                currentLine.FromLocationId,
                targetLocation.Id,
                currentLine.FromHu,
                currentLine.ToHu));

        if (!result.IsSuccess)
        {
            if (result.ShouldRefresh)
            {
                LoadDoc();
            }

            MessageBox.Show(result.Message, "Операция", MessageBoxButton.OK, ResolveServerUpdateLineMessageImage(result.Kind));
            return;
        }

        LoadDoc();
    }

    private void UpdatePartnerLock()
    {
        DocPartnerCombo.IsEnabled = DocOrderCombo.SelectedItem == null;
    }

    private void ConfigureHeaderFields(Doc doc, bool isEditable)
    {
        var showPartner = false;
        var showOrder = false;
        var showFromHeader = false;
        var showFromColumn = false;
        var showTo = false;
        var showHuHeader = true;
        var showReason = false;
        var showBatch = false;
        var showComment = false;
        var partnerLabel = "Контрагент";
        var fromLabel = "Откуда";
        var toLabel = "Куда";

        switch (doc.Type)
        {
            case DocType.Inbound:
                showPartner = true;
                partnerLabel = "Поставщик";
                showTo = false;
                showHuHeader = false;
                toLabel = "Место хранения";
                break;
            case DocType.Outbound:
                showPartner = true;
                partnerLabel = "Покупатель";
                showOrder = true;
                break;
            case DocType.Move:
                showFromHeader = true;
                showFromColumn = true;
                showTo = true;
                fromLabel = "Откуда";
                toLabel = "Куда";
                break;
            case DocType.WriteOff:
                showFromColumn = true;
                showReason = true;
                showHuHeader = false;
                fromLabel = "Место хранения";
                break;
            case DocType.Inventory:
                showTo = true;
                toLabel = "Место хранения";
                break;
            case DocType.ProductionReceipt:
                showTo = false;
                showOrder = true;
                showBatch = false;
                showComment = true;
                showHuHeader = false;
                toLabel = "Локация приёмки";
                break;
        }

        DocPartnerPanel.Visibility = showPartner ? Visibility.Visible : Visibility.Collapsed;
        DocOrderPanel.Visibility = showOrder ? Visibility.Visible : Visibility.Collapsed;
        DocFromPanel.Visibility = showFromHeader ? Visibility.Visible : Visibility.Collapsed;
        DocReasonPanel.Visibility = showReason ? Visibility.Visible : Visibility.Collapsed;
        DocToPanel.Visibility = showTo ? Visibility.Visible : Visibility.Collapsed;
        DocBatchPanel.Visibility = showBatch ? Visibility.Visible : Visibility.Collapsed;
        DocCommentPanel.Visibility = showComment ? Visibility.Visible : Visibility.Collapsed;
        DocHuPanel.Visibility = showHuHeader ? Visibility.Visible : Visibility.Collapsed;
        DocHuFromPanel.Visibility = doc.Type == DocType.Move ? Visibility.Visible : Visibility.Collapsed;
        DocMoveInternalPanel.Visibility = doc.Type == DocType.Move ? Visibility.Visible : Visibility.Collapsed;

        DocPartnerLabel.Text = partnerLabel;
        DocFromLabel.Text = fromLabel;
        DocToLabel.Text = toLabel;
        DocHuLabel.Text = doc.Type == DocType.Move
            ? "HU (куда)"
            : doc.Type == DocType.ProductionReceipt
                ? "HU (палета)"
                : doc.Type == DocType.Outbound ? "HU (источник)" : "HU";
        DocPartialCheck.Visibility = doc.Type == DocType.Outbound ? Visibility.Visible : Visibility.Collapsed;
        DocHuCombo.IsEnabled = isEditable;
        DocHuFromCombo.IsEnabled = isEditable;
        DocHuCombo.IsEditable = doc.Type == DocType.Move;
        DocMoveInternalCheck.IsEnabled = isEditable;
        DocReasonCombo.IsEnabled = isEditable;

        if (!showFromHeader)
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

        DocHeaderSaveButton.Visibility = showPartner || showOrder || showHuHeader || showReason || showBatch || showComment
            ? Visibility.Visible
            : Visibility.Collapsed;
        DocHeaderSaveButton.IsEnabled = isEditable;

        DocRecountButton.Visibility = doc.Type == DocType.Inventory && IsTsdSource(doc)
            ? Visibility.Visible
            : Visibility.Collapsed;
        DocRecountButton.IsEnabled = isEditable;

        var showInventory = doc.Type == DocType.Inventory;
        DocLinesGrid.Tag = isEditable;
        DocInventoryDbColumn.Visibility = showInventory ? Visibility.Visible : Visibility.Collapsed;
        DocInventoryDiffColumn.Visibility = showInventory ? Visibility.Visible : Visibility.Collapsed;
        DocHuColumn.Visibility = Visibility.Collapsed;
        DocKmColumn.Visibility = KmUiEnabled && (doc.Type is DocType.ProductionReceipt or DocType.Outbound)
            ? Visibility.Visible
            : Visibility.Collapsed;
        DocOrderLineColumn.Visibility = doc.Type == DocType.ProductionReceipt ? Visibility.Visible : Visibility.Collapsed;
        DocPackSingleHuColumn.Visibility = doc.Type is DocType.ProductionReceipt or DocType.Inbound ? Visibility.Visible : Visibility.Collapsed;
        DocFromColumn.Visibility = showFromColumn ? Visibility.Visible : Visibility.Collapsed;
        DocToEditColumn.Visibility = doc.Type is DocType.Inbound or DocType.ProductionReceipt
            ? Visibility.Visible
            : Visibility.Collapsed;
        DocToColumn.Visibility = showTo && doc.Type is not (DocType.Inbound or DocType.ProductionReceipt)
            ? Visibility.Visible
            : Visibility.Collapsed;
        KmCodesButton.Visibility = KmUiEnabled && (doc.Type is DocType.ProductionReceipt or DocType.Outbound)
            ? Visibility.Visible
            : Visibility.Collapsed;
        AutoHuButton.Visibility = doc.Type == DocType.ProductionReceipt ? Visibility.Visible : Visibility.Collapsed;
        AssignHuButton.Visibility = doc.Type is DocType.Inbound or DocType.Inventory or DocType.ProductionReceipt or DocType.Outbound
            ? Visibility.Visible
            : Visibility.Collapsed;
        DocFromColumn.Header = fromLabel;
        DocToColumn.Header = toLabel;
        DocToEditColumn.Header = toLabel;

        FillFromOrderButton.Visibility = Visibility.Collapsed;
        FillFromOrderButton.IsEnabled = false;

        if (doc.Type != DocType.Outbound)
        {
            OutboundHuPanel.Visibility = Visibility.Collapsed;
        }
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
        var selectedLineCount = GetSelectedDocLines().Count;
        var hasSelection = selectedLineCount > 0;
        var hasSingleSelection = selectedLineCount == 1 && _selectedDocLine != null;
        AddItemButton.IsEnabled = isEditable;
        AutoHuButton.IsEnabled = isEditable && _doc?.Type == DocType.ProductionReceipt && _docLines.Count > 0;
        EditLineButton.IsEnabled = isEditable && hasSingleSelection;
        AssignHuButton.IsEnabled = isEditable
                                  && SupportsLineHuAssignment()
                                  && (_doc?.Type == DocType.Inbound
                                      ? _docLines.Count > 0
                                      : hasSelection);
        DeleteLineButton.IsEnabled = isEditable && hasSelection;
        DocPartialCheck.IsEnabled = isEditable && _doc?.Type == DocType.Outbound && hasOrder;
        KmCodesButton.IsEnabled = KmUiEnabled
                                  && isEditable
                                  && hasSingleSelection
                                  && _selectedDocLine?.IsMarked == true
                                  && (_doc?.Type == DocType.ProductionReceipt || _doc?.Type == DocType.Outbound);
        FillFromOrderButton.IsEnabled = false;
        UpdateOutboundHuButton();
    }

    private void UpdateOutboundHuButton()
    {
        OutboundHuApplyButton.IsEnabled = IsDocEditable()
                                          && _doc?.Type == DocType.Outbound
                                          && _selectedDocLine != null
                                          && _selectedOutboundHu != null;
    }

    private void LoadOutboundHuCandidates()
    {
        _outboundHuCandidates.Clear();
        _selectedOutboundHu = null;
        UpdateOutboundHuButton();

        if (_doc?.Type != DocType.Outbound || !IsDocEditable() || _selectedDocLine == null)
        {
            OutboundHuPanel.Visibility = Visibility.Collapsed;
            return;
        }

        OutboundHuPanel.Visibility = Visibility.Visible;

        var locationsById = _locations.ToDictionary(location => location.Id, location => location);
        var rows = GetStockRows()
            .Where(row => row.ItemId == _selectedDocLine.ItemId
                          && row.Qty > 0
                          && !string.IsNullOrWhiteSpace(row.Hu))
            .Select(row => new
            {
                HuCode = NormalizeHuValue(row.Hu)!,
                LocationId = _locations.FirstOrDefault(location =>
                    string.Equals(location.Code, row.LocationCode, StringComparison.OrdinalIgnoreCase))?.Id ?? 0,
                row.Qty
            })
            .Where(row => row.LocationId > 0)
            .OrderBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.LocationId)
            .ToList();

        foreach (var row in rows)
        {
            var locationLabel = locationsById.TryGetValue(row.LocationId, out var location)
                ? location.DisplayName
                : row.LocationId.ToString(CultureInfo.InvariantCulture);
            _outboundHuCandidates.Add(new OutboundHuCandidate
            {
                HuCode = row.HuCode,
                LocationId = row.LocationId,
                LocationDisplay = locationLabel,
                Qty = row.Qty
            });
        }
    }

    private async void OutboundHuApply_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _doc.Type != DocType.Outbound)
        {
            return;
        }

        if (!EnsureDraftDocSelected())
        {
            return;
        }

        if (_selectedDocLine == null || _selectedOutboundHu == null)
        {
            MessageBox.Show("Выберите строку и HU.", _doc.Type == DocType.WriteOff ? "Списание" : "Отгрузка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var line = FindCurrentDocLine(_selectedDocLine.Id);
        if (line == null)
        {
            MessageBox.Show("Строка не найдена.", _doc.Type == DocType.WriteOff ? "Списание" : "Отгрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var maxQty = Math.Min(line.Qty, _selectedOutboundHu.Qty);
        if (maxQty <= 0)
        {
            MessageBox.Show("Недостаточно остатка на выбранном HU.", _doc.Type == DocType.WriteOff ? "Списание" : "Отгрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var qtyDialog = new QuantityDialog(maxQty)
        {
            Owner = this
        };
        if (qtyDialog.ShowDialog() != true)
        {
            return;
        }

        var qty = qtyDialog.Qty;
        if (qty <= 0 || qty > maxQty + 0.000001)
        {
            MessageBox.Show($"Количество должно быть от 1 до {FormatQty(maxQty)}.", _doc.Type == DocType.WriteOff ? "Списание" : "Отгрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await TryApplyOutboundHuMutationViaServerAsync(
            line,
            qty,
            _selectedOutboundHu.LocationId,
            _selectedOutboundHu.HuCode,
            null,
            _doc.Type == DocType.WriteOff ? "Списание" : "Отгрузка",
            _doc.Type == DocType.WriteOff ? "writeoff-hu-apply" : "outbound-hu-apply");
    }

    private void UpdateActionButtons()
    {
        var isEditable = IsDocEditable();
        var hasId = _doc?.Id > 0;
        var hasPartner = !IsPartnerRequired() || _doc?.PartnerId != null || ResolveDocPartnerFromInput() != null;
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
        return _doc?.Type == DocType.Outbound && (_doc?.OrderId.HasValue == true || DocOrderCombo.SelectedItem != null);
    }

    private async Task<bool> TryApplyOrderSelectionAsync(OrderOption selected)
    {
        if (_doc == null || _doc.Type != DocType.Outbound)
        {
            return false;
        }

        LogOutboundOrderBoundInfo($"explicit fill invoked: order_id={selected.Id}; mode=server; partial={_isPartialShipment}");
        return await TryApplyOrderSelectionViaServerAsync(selected);
    }

    private bool IsFullServerLineLifecycleEnabled()
    {
        return true;
    }

    private async Task<bool> TryApplyOutboundHuMutationViaServerAsync(
        DocLine line,
        double allocatedQty,
        long? targetFromLocationId,
        string? targetFromHu,
        string? targetToHu,
        string caption,
        string flowName)
    {
        if (_doc == null)
        {
            return false;
        }

        if (!targetFromLocationId.HasValue)
        {
            LogHuServerFlowWarn($"{flowName} aborted before server call: missing target from_location_id for source_line_id={line.Id}");
            MessageBox.Show(
                "Для серверного HU-переназначения требуется локация-источник.",
                caption,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var remainingQty = line.Qty - allocatedQty;
        LogHuServerFlowInfo(
            $"{flowName} server sequence prepared: source_line_id={line.Id}; allocated_qty={FormatQty(allocatedQty)}; remaining_qty={FormatQty(Math.Max(remainingQty, 0))}; target_from_location_id={targetFromLocationId.Value}; target_from_hu={NormalizeHuValue(targetFromHu) ?? "-"}; target_to_hu={NormalizeHuValue(targetToHu) ?? "-"}");

        var sourceMutated = false;
        if (remainingQty <= 0.000001)
        {
            LogHuServerFlowInfo($"{flowName} server delete phase started: source_line_id={line.Id}");
            var deleteResult = await _services.WpfDeleteDocLines.DeleteLinesAsync(_doc, new[] { line.Id });
            if (!deleteResult.IsSuccess)
            {
                if (deleteResult.ShouldRefresh)
                {
                    LoadDoc();
                }

                LogHuServerFlowWarn($"{flowName} server delete phase failed: source_line_id={line.Id}; message={deleteResult.Message}");
                MessageBox.Show(
                    deleteResult.Message,
                    caption,
                    MessageBoxButton.OK,
                    ResolveServerDeleteLineMessageImage(deleteResult.Kind));
                return false;
            }

            sourceMutated = true;
            LogHuServerFlowInfo($"{flowName} server delete phase completed: source_line_id={line.Id}");
        }
        else
        {
            LogHuServerFlowInfo($"{flowName} server update phase started: source_line_id={line.Id}; remaining_qty={FormatQty(remainingQty)}");
            var updateResult = await _services.WpfUpdateDocLines.UpdateLineAsync(
                _doc,
                new WpfUpdateDocLineContext(
                    line.Id,
                    remainingQty,
                    line.UomCode,
                    line.FromLocationId,
                    line.ToLocationId,
                    line.FromHu,
                    line.ToHu));
            if (!updateResult.IsSuccess)
            {
                if (updateResult.ShouldRefresh)
                {
                    LoadDoc();
                }

                LogHuServerFlowWarn($"{flowName} server update phase failed: source_line_id={line.Id}; message={updateResult.Message}");
                MessageBox.Show(
                    updateResult.Message,
                    caption,
                    MessageBoxButton.OK,
                    ResolveServerUpdateLineMessageImage(updateResult.Kind));
                return false;
            }

            sourceMutated = true;
            LogHuServerFlowInfo($"{flowName} server update phase completed: source_line_id={line.Id}; remaining_qty={FormatQty(remainingQty)}");
        }

        LogHuServerFlowInfo($"{flowName} server add phase started: source_line_id={line.Id}; allocated_qty={FormatQty(allocatedQty)}");
        var addResult = await _services.WpfAddDocLines.AddLineAsync(
            _doc,
            new WpfAddDocLineContext(
                line.ItemId,
                null,
                line.OrderLineId,
                allocatedQty,
                null,
                line.UomCode,
                targetFromLocationId,
                null,
                targetFromHu,
                targetToHu));
        if (!addResult.IsSuccess)
        {
            if (sourceMutated || addResult.ShouldRefresh)
            {
                LoadDoc();
            }

            LogHuServerFlowWarn($"{flowName} server add phase failed: source_line_id={line.Id}; message={addResult.Message}");
            MessageBox.Show(
                BuildHuServerPartialFailureMessage(addResult.Message),
                caption,
                MessageBoxButton.OK,
                ResolveServerAddLineMessageImage(addResult.Kind));
            return false;
        }

        LoadDoc();
        LogHuServerFlowInfo($"{flowName} server sequence completed: source_line_id={line.Id}; allocated_qty={FormatQty(allocatedQty)}");
        return true;
    }

    private static string BuildHuServerPartialFailureMessage(string message)
    {
        return $"Часть HU-операции уже применена сервером. Обновите документ и проверьте фактическое состояние строк перед повтором.{Environment.NewLine}{Environment.NewLine}{message}";
    }

    private async Task<bool> TryApplyOrderSelectionViaServerAsync(OrderOption selected)
    {
        if (_doc == null)
        {
            return false;
        }

        var contexts = BuildOutboundOrderBatchContexts(selected.Id, null);
        LogOutboundOrderBoundInfo($"fill from order server branch prepared: order_id={selected.Id}; contexts={contexts.Count}; from_location_id=-; from_hu=-");

        var headerSaved = await TryPersistHeaderViaServerAsync(
            selected.PartnerId,
            selected.Id,
            null,
            null,
            null,
            null,
            "Операция");
        if (!headerSaved)
        {
            return false;
        }

        var existingLineIds = _docLines
            .Select(line => line.Id)
            .ToList();
        LogOutboundOrderBoundInfo($"fill from order server branch delete phase prepared: order_id={selected.Id}; active_line_count={existingLineIds.Count}");
        if (!await TryDeleteLinesForServerRebuildAsync(
                existingLineIds,
                "Операция",
                logInfo: message => LogOutboundOrderBoundInfo(message),
                logWarn: message => LogOutboundOrderBoundWarn(message)))
        {
            return false;
        }

        var result = await _services.WpfBatchAddDocLines.AddLinesBatchAsync(_doc, contexts);
        LoadDoc();

        if (!result.IsSuccess)
        {
            MessageBox.Show(
                BuildBatchFailureMessage(result),
                "Операция",
                MessageBoxButton.OK,
                ResolveServerAddLineMessageImage(result.Kind));
            return false;
        }

        LoadOrderQuantities(selected.Id);
        ResetPartialMode();
        if (result.AddedCount == 0)
        {
            MessageBox.Show("По заказу нет остатка к отгрузке.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (!string.IsNullOrWhiteSpace(result.Message))
        {
            MessageBox.Show(result.Message, "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        return true;
    }

    private async Task TryApplyReceiptOrderSelectionAsync(OrderOption selected)
    {
        if (_doc == null || _doc.Type != DocType.ProductionReceipt)
        {
            return;
        }

        if (!await TryPersistHeaderViaServerAsync(
                ResolveDocPartnerFromInput()?.Id,
                selected.Id,
                null,
                null,
                DocBatchBox.Text,
                DocCommentBox.Text,
                "Операция"))
        {
            return;
        }

        var replaceLines = false;
        var existingLines = _docLines;
        if (existingLines.Count > 0)
        {
            var confirm = MessageBox.Show(
                "Заменить текущие строки данными из заказа?",
                "Выпуск продукции",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);
            if (confirm != MessageBoxResult.Yes)
            {
                LoadDoc();
                return;
            }

            replaceLines = true;
        }

        var toLocation = EnsureProductionReceiptLocationSelected();
        if (toLocation == null)
        {
            MessageBox.Show(
                "Для выпуска продукции выберите локацию приёмки.",
                "Выпуск продукции",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        await FillProductionReceiptFromOrderAsync(selected.Id, replaceLines, showEmptyMessage: false);
    }

    private Location? EnsureProductionReceiptLocationSelected()
    {
        return ResolveDefaultReceiptLocation(preferAutoEnabled: true);
    }

    private Location? ResolveDefaultReceiptLocation(bool preferAutoEnabled)
    {
        if (_locations.Count == 0)
        {
            return null;
        }

        if (preferAutoEnabled)
        {
            var auto = _locations
                .Where(location => location.AutoHuDistributionEnabled)
                .OrderBy(location => location.Code, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (auto != null)
            {
                return auto;
            }
        }

        return _locations
            .OrderBy(location => location.Code, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private async void DocFillFromOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || (_doc.Type != DocType.ProductionReceipt && _doc.Type != DocType.Outbound))
        {
            return;
        }

        if (!EnsureDraftDocSelected())
        {
            return;
        }

        if (!TryResolveOrder(out var orderOption) || orderOption == null)
        {
            MessageBox.Show("Выберите заказ.", "Операция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var selected = orderOption;

        if (_doc.Type == DocType.Outbound)
        {
            LogOutboundOrderBoundInfo($"explicit fill-from-order button clicked: order_id={selected.Id}");
            await TryApplyOrderSelectionAsync(selected);
            return;
        }

        var existingLines = _docLines;
        var replaceLines = false;
        if (existingLines.Count > 0)
        {
            var confirm = MessageBox.Show(
                "Заменить текущие строки данными из заказа?",
                "Выпуск продукции",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            replaceLines = true;
        }

        await FillProductionReceiptFromOrderAsync(selected.Id, replaceLines, showEmptyMessage: true);
    }

    private async Task FillProductionReceiptFromOrderAsync(long orderId, bool replaceLines, bool showEmptyMessage)
    {
        if (_doc == null)
        {
            return;
        }

        if (!TryGetLineLocations(out _, out var toLocation, out _, out var toHu))
        {
            return;
        }

        if (IsServerBatchRebuildEnabled())
        {
            var contexts = BuildProductionReceiptBatchContexts(orderId, toLocation?.Id, toHu);
            if (!await TryPersistHeaderViaServerAsync(
                    ResolveDocPartnerFromInput()?.Id,
                    orderId,
                    null,
                    null,
                    DocBatchBox.Text,
                    DocCommentBox.Text,
                    "Операция"))
            {
                return;
            }

            if (replaceLines)
            {
                var existingLineIds = _docLines
                    .Select(line => line.Id)
                    .ToList();
                _services.AppLogger.Info($"wpf_batch_rebuild doc_id={_doc.Id} doc_type=ProductionReceipt delete_phase mode=server active_line_count={existingLineIds.Count}");
                if (!await TryDeleteLinesForServerRebuildAsync(existingLineIds, "Выпуск продукции"))
                {
                    return;
                }
            }

            var result = await _services.WpfBatchAddDocLines.AddLinesBatchAsync(_doc, contexts);
            LoadDoc();

            if (!result.IsSuccess)
            {
                MessageBox.Show(
                    BuildBatchFailureMessage(result),
                    "Операция",
                    MessageBoxButton.OK,
                    ResolveServerAddLineMessageImage(result.Kind));
                return;
            }

            if (showEmptyMessage && result.AddedCount == 0)
            {
                MessageBox.Show("Нет позиций для приёмки по выбранному заказу.", "Выпуск продукции", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (!string.IsNullOrWhiteSpace(result.Message))
            {
                MessageBox.Show(result.Message, "Выпуск продукции", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return;
        }
    }

    private void TriggerAutoFillBoundOrderIfNeeded()
    {
        if (_autoFillFromBoundOrderInProgress || _doc == null || _doc.Status != DocStatus.Draft)
        {
            return;
        }

        if (_docLines.Count > 0 || !_doc.OrderId.HasValue)
        {
            return;
        }

        if (_doc.Type != DocType.Outbound && _doc.Type != DocType.ProductionReceipt)
        {
            return;
        }

        var key = $"{_doc.Id}:{_doc.Type}:{_doc.OrderId.Value}";
        if (_autoFillAttempts.Contains(key))
        {
            return;
        }

        var orderOption = _ordersAll.FirstOrDefault(order => order.Id == _doc.OrderId.Value);
        if (orderOption == null)
        {
            return;
        }

        _autoFillAttempts.Add(key);
        _ = AutoFillBoundOrderAsync(orderOption);
    }

    private async Task AutoFillBoundOrderAsync(OrderOption selected)
    {
        if (_doc == null)
        {
            return;
        }

        try
        {
            _autoFillFromBoundOrderInProgress = true;
            if (_doc.Type == DocType.Outbound)
            {
                await TryApplyOrderSelectionAsync(selected);
            }
            else if (_doc.Type == DocType.ProductionReceipt)
            {
                await TryApplyReceiptOrderSelectionAsync(selected);
            }
        }
        finally
        {
            _autoFillFromBoundOrderInProgress = false;
        }
    }

    private IReadOnlyList<WpfAddDocLineContext> BuildOutboundOrderBatchContexts(long orderId, long? fromLocationId)
    {
        var requestedHu = NormalizeHuValue(GetSelectedHuCode(DocHuCombo));
        var orderBoundHuByItem = GetOrderBoundHuByItem(orderId);
        var locationsByCode = _locations
            .Where(location => !string.IsNullOrWhiteSpace(location.Code))
            .ToDictionary(location => location.Code, location => location.Id, StringComparer.OrdinalIgnoreCase);

        var sourcePools = new Dictionary<(long ItemId, long LocationId, string HuKey), OutboundSourcePool>();
        foreach (var row in GetStockRows().Where(row => row.Qty > 0))
        {
            if (string.IsNullOrWhiteSpace(row.LocationCode)
                || !locationsByCode.TryGetValue(row.LocationCode, out var locationId))
            {
                continue;
            }

            var location = _locations.FirstOrDefault(candidate => candidate.Id == locationId);
            if (location == null)
            {
                continue;
            }

            if (fromLocationId.HasValue && locationId != fromLocationId.Value)
            {
                continue;
            }

            if (!fromLocationId.HasValue && !location.AutoHuDistributionEnabled)
            {
                continue;
            }

            var hu = NormalizeHuValue(row.Hu);
            if (!string.IsNullOrWhiteSpace(requestedHu)
                && !string.Equals(hu, requestedHu, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Для отгрузки по заказу учитываем только HU, выпущенные под этот заказ.
            if (!orderBoundHuByItem.TryGetValue(row.ItemId, out var allowedHuCodes)
                || allowedHuCodes.Count == 0
                || string.IsNullOrWhiteSpace(hu)
                || !allowedHuCodes.Contains(hu))
            {
                continue;
            }

            var huKey = hu ?? string.Empty;
            var key = (row.ItemId, locationId, huKey);
            if (!sourcePools.TryGetValue(key, out var pool))
            {
                pool = new OutboundSourcePool
                {
                    ItemId = row.ItemId,
                    LocationId = locationId,
                    HuCode = hu,
                    RemainingQty = 0
                };
                sourcePools[key] = pool;
            }

            pool.RemainingQty += row.Qty;
        }

        var stockPoolsByItem = sourcePools.Values
            .GroupBy(pool => pool.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(pool => pool.LocationId)
                    .ThenBy(pool => pool.HuCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList());

        var contexts = new List<WpfAddDocLineContext>();
        foreach (var line in GetOrderShipmentRemaining(orderId).Where(line => line.QtyRemaining > 0))
        {
            var qtyRemaining = line.QtyRemaining;
            if (stockPoolsByItem.TryGetValue(line.ItemId, out var pools))
            {
                foreach (var pool in pools)
                {
                    if (qtyRemaining <= 0.000001)
                    {
                        break;
                    }

                    if (pool.RemainingQty <= 0.000001)
                    {
                        continue;
                    }

                    var allocatedQty = Math.Min(qtyRemaining, pool.RemainingQty);
                    contexts.Add(new WpfAddDocLineContext(
                        line.ItemId,
                        null,
                        line.OrderLineId,
                        allocatedQty,
                        null,
                        null,
                        pool.LocationId,
                        null,
                        pool.HuCode,
                        null));

                    pool.RemainingQty -= allocatedQty;
                    qtyRemaining -= allocatedQty;
                }
            }

        }

        return contexts;
    }

    private IReadOnlyDictionary<long, HashSet<string>> GetOrderBoundHuByItem(long orderId)
    {
        if (_services.WpfReadApi.TryGetOrderBoundHuByItem(orderId, out var fromServer)
            && fromServer.Count > 0)
        {
            return fromServer;
        }

        return CollectOrderBoundHuFromClosedProductionDocs(orderId);
    }

    private IReadOnlyDictionary<long, HashSet<string>> CollectOrderBoundHuFromClosedProductionDocs(long orderId)
    {
        var result = new Dictionary<long, HashSet<string>>();
        if (!_services.WpfReadApi.TryGetDocs(DocType.ProductionReceipt, DocStatus.Closed, out var docs))
        {
            return result;
        }

        foreach (var doc in docs.Where(doc => doc.OrderId == orderId))
        {
            if (!_services.WpfReadApi.TryGetDocLines(doc.Id, out var docLines))
            {
                continue;
            }

            foreach (var line in docLines)
            {
                if (line.Qty <= QtyTolerance)
                {
                    continue;
                }

                var huCode = NormalizeHuValue(line.ToHu);
                if (string.IsNullOrWhiteSpace(huCode))
                {
                    continue;
                }

                if (!result.TryGetValue(line.ItemId, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    result[line.ItemId] = set;
                }

                set.Add(huCode);
            }
        }

        return result;
    }

    private IReadOnlyList<WpfAddDocLineContext> BuildProductionReceiptBatchContexts(long orderId, long? toLocationId, string? toHu)
    {
        var effectiveToLocationId = toLocationId ?? ResolveDefaultReceiptLocation(preferAutoEnabled: true)?.Id;
        var receiptLines = _services.WpfReadApi.TryGetOrderReceiptRemainingDetailed(orderId, out var apiReceiptLines)
            ? apiReceiptLines
            : Array.Empty<OrderReceiptLine>();

        return receiptLines
            .Where(line => line.QtyRemaining > QtyTolerance)
            .OrderBy(line => line.SortOrder)
            .ThenBy(line => line.OrderLineId)
            .Select(line => new WpfAddDocLineContext(
                line.ItemId,
                null,
                line.OrderLineId,
                line.QtyRemaining,
                null,
                null,
                null,
                line.ToLocationId ?? effectiveToLocationId,
                null,
                NormalizeHuValue(line.ToHu) ?? NormalizeHuValue(toHu)))
            .ToList();
    }

    private bool IsServerBatchRebuildEnabled()
    {
        return true;
    }

    private async Task<bool> TryDeleteLinesForServerRebuildAsync(
        IReadOnlyCollection<long> lineIds,
        string caption,
        Action<string>? logInfo = null,
        Action<string>? logWarn = null)
    {
        if (_doc == null || lineIds.Count == 0)
        {
            logInfo?.Invoke("server rebuild delete phase skipped: no active lines");
            return true;
        }

        logInfo?.Invoke($"server rebuild delete phase started: line_count={lineIds.Count}");
        var result = await _services.WpfDeleteDocLines.DeleteLinesAsync(_doc, lineIds);
        if (result.ShouldRefresh)
        {
            LoadDoc();
        }

        if (result.IsSuccess)
        {
            logInfo?.Invoke($"server rebuild delete phase completed: line_count={lineIds.Count}");
            return true;
        }

        logWarn?.Invoke($"server rebuild delete phase failed: {result.Message}");
        MessageBox.Show(
            result.Message,
            caption,
            MessageBoxButton.OK,
            ResolveServerDeleteLineMessageImage(result.Kind));
        return false;
    }

    private static string BuildBatchFailureMessage(WpfBatchAddDocLinesResult result)
    {
        if (result.AddedCount <= 0 && result.ReplayCount <= 0)
        {
            return result.Message;
        }

        return $"{result.Message}{Environment.NewLine}{Environment.NewLine}Уже обработано строк: {result.AddedCount + result.ReplayCount}. Перед повтором обновите документ и проверьте текущие строки.";
    }

    private async Task ClearDocOrderBindingAsync()
    {
        if (_doc == null || _doc.Status != DocStatus.Draft)
        {
            return;
        }

        var partnerId = ResolveDocPartnerFromInput()?.Id;
        var saved = await TryPersistHeaderViaServerAsync(
            partnerId,
            null,
            _doc.Type is DocType.Inbound or DocType.ProductionReceipt or DocType.WriteOff ? null : GetSelectedHuCode(DocHuCombo),
            _doc.Type == DocType.WriteOff ? (DocReasonCombo.SelectedItem as WriteOffReason)?.Code : null,
            _doc.Type == DocType.ProductionReceipt ? DocBatchBox.Text : null,
            _doc.Type == DocType.ProductionReceipt ? DocCommentBox.Text : null,
            "Операция");
        if (!saved)
        {
            return;
        }

        if (_doc.Type == DocType.Outbound)
        {
            _orderedQtyByOrderLine.Clear();
        }

        LoadDoc();
    }

    private async Task<bool> TrySaveHeaderAsync()
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

        var partnerId = ResolveDocPartnerFromInput()?.Id;
        var huCode = _doc.Type is DocType.Inbound or DocType.ProductionReceipt or DocType.WriteOff
            ? null
            : GetSelectedHuCode(DocHuCombo);
        var reasonCode = (DocReasonCombo.SelectedItem as WriteOffReason)?.Code;
        var productionBatch = DocBatchBox.Text;
        var comment = DocCommentBox.Text;
        try
        {
            if (_doc.Type == DocType.ProductionReceipt
                && !string.IsNullOrWhiteSpace(huCode)
                && !TryEnsureHuExists(huCode))
            {
                return false;
            }

            if (!TryResolveOrder(out var orderOption))
            {
                return false;
            }

            if (orderOption != null)
            {
                var headerPartnerId = _doc.Type == DocType.Outbound ? orderOption.PartnerId : partnerId;
                if (_doc.Type == DocType.Outbound)
                {
                    var orderChanged = _doc.OrderId != orderOption.Id
                        || !string.Equals(_doc.OrderRef, orderOption.OrderRef, StringComparison.OrdinalIgnoreCase);
                    if (!await TryPersistHeaderViaServerAsync(headerPartnerId, orderOption.Id, huCode, null, null, null, "Операция"))
                    {
                        return false;
                    }
                    LoadOrderQuantities(orderOption.Id);
                    if (orderChanged)
                    {
                        LogOutboundOrderBoundInfo($"order binding updated in header: order_id={orderOption.Id}");
                    }
                }
                else if (_doc.Type == DocType.ProductionReceipt)
                {
                    if (!await TryPersistHeaderViaServerAsync(headerPartnerId, orderOption.Id, huCode, null, productionBatch, comment, "Операция"))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!await TryPersistHeaderViaServerAsync(headerPartnerId, null, huCode, null, null, null, "Операция"))
                    {
                        return false;
                    }
                }
            }
            else
            {
                if (!await TryPersistHeaderViaServerAsync(partnerId, null, huCode, reasonCode, productionBatch, comment, "Операция"))
                {
                    return false;
                }

                if (_doc.Type == DocType.Outbound)
                {
                    ResetPartialMode();
                }
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

    private async Task<bool> TryPersistHeaderViaServerAsync(
        long? partnerId,
        long? orderId,
        string? shippingRef,
        string? reasonCode,
        string? productionBatchNo,
        string? comment,
        string caption)
    {
        if (_doc == null)
        {
            return false;
        }

        var result = await _services.WpfDocumentRuntimeApi.TrySaveHeaderAsync(
            _doc.Id,
            partnerId,
            orderId,
            shippingRef,
            reasonCode,
            productionBatchNo,
            comment);
        if (result.IsSuccess)
        {
            return true;
        }

        MessageBox.Show(result.Error ?? "Не удалось сохранить шапку документа через сервер.", caption, MessageBoxButton.OK, MessageBoxImage.Error);
        return false;
    }

    private bool TryEnsureHuExists(string? huCode)
    {
        var normalized = NormalizeHuValue(huCode);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            MessageBox.Show("Укажите HU.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        normalized = normalized.ToUpperInvariant();
        if (!normalized.StartsWith("HU-", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("HU должен начинаться с HU-.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (_services.WpfHuApi.TryGetHuByCode(normalized, out var existing) && existing != null)
        {
            return true;
        }

        var confirm = MessageBox.Show(
            $"HU {normalized} не найден. Создать?",
            "HU",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return false;
        }

        try
        {
            var result = _services.WpfHuApi.TryCreateHuAsync(normalized, null)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            if (!result.IsSuccess)
            {
                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    throw new InvalidOperationException(result.Error);
                }
                throw new InvalidOperationException("Сервер не подтвердил создание HU.");
            }
            RefreshHuOptions();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "HU", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void ApplyPartnerFilter(string? query = null, long? forceIncludePartnerId = null)
    {
        var normalizedQuery = NormalizePartnerSearch(query);
        var selectedPartnerId = forceIncludePartnerId ?? (DocPartnerCombo.SelectedItem as Partner)?.Id;

        _suppressPartnerFilter = true;
        _partners.Clear();
        try
        {
            var docType = _doc?.Type;
            foreach (var partner in _partnersAll)
            {
                var status = _partnerStatusesById.TryGetValue(partner.Id, out var value)
                    ? value
                    : PartnerStatus.Both;
                if (selectedPartnerId.HasValue && partner.Id == selectedPartnerId.Value)
                {
                    _partners.Add(partner);
                    continue;
                }

                if (docType == DocType.Inbound && status == PartnerStatus.Client)
                {
                    continue;
                }

                if (docType == DocType.Outbound && status == PartnerStatus.Supplier)
                {
                    continue;
                }

                if (!PartnerMatchesSearch(partner, normalizedQuery))
                {
                    continue;
                }

                _partners.Add(partner);
            }
        }
        finally
        {
            _suppressPartnerFilter = false;
        }
    }

    private void DocPartnerCombo_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressPartnerFilter || _suppressDirtyTracking || !DocPartnerCombo.IsKeyboardFocusWithin)
        {
            return;
        }

        var text = DocPartnerCombo.Text ?? string.Empty;
        ApplyPartnerFilter(text);
        DocPartnerCombo.IsDropDownOpen = !string.IsNullOrWhiteSpace(text) && _partners.Count > 0;
        RestoreComboText(DocPartnerCombo, text);
    }

    private Partner? ResolveDocPartnerFromInput()
    {
        return FindPartnerByQuery(DocPartnerCombo.Text)
               ?? (string.IsNullOrWhiteSpace(DocPartnerCombo.Text) ? DocPartnerCombo.SelectedItem as Partner : null);
    }

    private Partner? FindPartnerByQuery(string? query)
    {
        var normalized = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var matches = _partnersAll
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

    private void LoadOrderQuantities(long orderId)
    {
        _orderedQtyByOrderLine.Clear();
        foreach (var line in GetOrderShipmentRemaining(orderId))
        {
            if (line.QtyRemaining <= 0)
            {
                continue;
            }

            _orderedQtyByOrderLine[line.OrderLineId] = line.QtyRemaining;
        }
    }

    private IReadOnlyDictionary<long, double> GetItemAvailability()
    {
        return _services.WpfReadApi.TryGetItemAvailability(out var availability)
            ? availability
            : new Dictionary<long, double>();
    }

    private IReadOnlyList<OrderShipmentLine> GetOrderShipmentRemaining(long orderId)
    {
        return _services.WpfReadApi.TryGetOrderShipmentRemaining(orderId, out var lines)
            ? lines
            : Array.Empty<OrderShipmentLine>();
    }

    private IReadOnlyList<OrderReceiptLine> GetOrderReceiptRemaining(long orderId)
    {
        return _services.WpfReadApi.TryGetOrderReceiptRemaining(orderId, out var lines)
            ? lines
            : Array.Empty<OrderReceiptLine>();
    }

    private Item? FindItem(long itemId)
    {
        var items = _services.WpfReadApi.TryGetItems(null, out var apiItems)
            ? apiItems
            : Array.Empty<Item>();
        return items.FirstOrDefault(item => item.Id == itemId);
    }

    private string? GetLocationCode(long locationId)
    {
        return _locations.FirstOrDefault(location => location.Id == locationId)?.Code;
    }

    private IReadOnlyList<StockRow> GetStockRows()
    {
        return _services.WpfReadApi.TryGetStockRows(null, out var apiRows)
            ? apiRows
            : Array.Empty<StockRow>();
    }

    private double GetAvailableQty(long itemId, long locationId, string? huCode)
    {
        var locationCode = GetLocationCode(locationId);
        var normalizedHu = NormalizeHuValue(huCode);
        if (string.IsNullOrWhiteSpace(locationCode))
        {
            return 0;
        }

        return GetStockRows()
            .Where(row => row.ItemId == itemId
                          && string.Equals(row.LocationCode, locationCode, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(NormalizeHuValue(row.Hu), normalizedHu, StringComparison.OrdinalIgnoreCase))
            .Sum(row => row.Qty);
    }

    private IReadOnlyList<Item> GetAvailableItemsByLocationAndHu(long locationId, string? huCode)
    {
        var locationCode = GetLocationCode(locationId);
        if (string.IsNullOrWhiteSpace(locationCode))
        {
            return Array.Empty<Item>();
        }

        var rows = GetStockRows()
            .Where(row => string.Equals(row.LocationCode, locationCode, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(NormalizeHuValue(row.Hu), NormalizeHuValue(huCode), StringComparison.OrdinalIgnoreCase)
                          && row.Qty > 0)
            .ToList();
        if (rows.Count == 0)
        {
            return Array.Empty<Item>();
        }

        var items = _services.WpfReadApi.TryGetItems(null, out var apiItems)
            ? apiItems
            : Array.Empty<Item>();
        var itemIds = rows.Select(row => row.ItemId).Distinct().ToHashSet();
        return items.Where(item => itemIds.Contains(item.Id)).ToList();
    }

    private IReadOnlyList<Item> GetAvailableItemsForOutboundHu(string? huCode)
    {
        var normalizedHu = NormalizeHuValue(huCode);
        var itemIds = GetStockRows()
            .Where(row => row.Qty > 0
                          && string.Equals(NormalizeHuValue(row.Hu), normalizedHu, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.ItemId)
            .Distinct()
            .ToHashSet();
        if (itemIds.Count == 0)
        {
            return Array.Empty<Item>();
        }

        var items = _services.WpfReadApi.TryGetItems(null, out var apiItems)
            ? apiItems
            : Array.Empty<Item>();
        return items.Where(item => itemIds.Contains(item.Id)).ToList();
    }

    private IReadOnlyList<Item> GetAvailableItemsForWriteOff()
    {
        var itemIds = GetStockRows()
            .Where(row => row.Qty > 0)
            .Select(row => row.ItemId)
            .Distinct()
            .ToHashSet();
        if (itemIds.Count == 0)
        {
            return Array.Empty<Item>();
        }

        var items = _services.WpfReadApi.TryGetItems(null, out var apiItems)
            ? apiItems
            : Array.Empty<Item>();
        return items.Where(item => itemIds.Contains(item.Id)).ToList();
    }

    private IReadOnlyDictionary<long, double> GetRemainingWriteOffAvailabilityByItem()
    {
        var availability = GetStockRows()
            .Where(row => row.Qty > 0)
            .GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Qty));

        foreach (var line in _docLines.Where(line => line.QtyBase > QtyTolerance))
        {
            if (!availability.ContainsKey(line.ItemId))
            {
                availability[line.ItemId] = 0;
            }

            availability[line.ItemId] -= line.QtyBase;
        }

        return availability
            .Where(entry => entry.Value > QtyTolerance)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
    }

    private IReadOnlyList<WriteOffSource> GetWriteOffSources(long itemId)
    {
        var sources = GetStockRows()
            .Where(row => row.ItemId == itemId && row.Qty > 0)
            .Select(row => new
            {
                Row = row,
                Location = _locations.FirstOrDefault(candidate =>
                    string.Equals(candidate.Code, row.LocationCode, StringComparison.OrdinalIgnoreCase)),
                Hu = NormalizeHuValue(row.Hu)
            })
            .Where(entry => entry.Location != null)
            .GroupBy(entry => new { entry.Location!.Id, entry.Location.Code, entry.Hu })
            .Select(group => new WriteOffSourceBalance(
                group.Key.Id,
                group.Key.Code,
                group.Key.Hu,
                group.Sum(entry => entry.Row.Qty)))
            .Where(source => source.Qty > QtyTolerance)
            .OrderBy(source => source.LocationCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => string.IsNullOrWhiteSpace(source.HuCode) ? 0 : 1)
            .ThenBy(source => source.HuCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var line in _docLines.Where(line => line.ItemId == itemId && line.QtyBase > QtyTolerance))
        {
            ApplyWriteOffReservation(sources, line.FromLocationId, NormalizeHuValue(line.FromHu), line.QtyBase);
        }

        return sources
            .Where(source => source.Qty > QtyTolerance)
            .Select(source => new WriteOffSource(source.LocationId, source.HuCode, source.Qty))
            .ToList();
    }

    private static void ApplyWriteOffReservation(
        List<WriteOffSourceBalance> sources,
        long? locationId,
        string? huCode,
        double qty)
    {
        var scoped = locationId.HasValue
            ? sources.Where(source => source.LocationId == locationId.Value).ToList()
            : sources;
        var remaining = qty;

        if (!string.IsNullOrWhiteSpace(huCode))
        {
            var source = scoped.FirstOrDefault(entry =>
                string.Equals(entry.HuCode, huCode, StringComparison.OrdinalIgnoreCase));
            if (source != null)
            {
                source.Qty = Math.Max(0, source.Qty - remaining);
            }
            return;
        }

        foreach (var source in scoped)
        {
            if (remaining <= QtyTolerance)
            {
                break;
            }

            var take = Math.Min(source.Qty, remaining);
            source.Qty -= take;
            remaining -= take;
        }
    }

    private double GetRemainingWriteOffQty(long itemId, long? locationId, string? huCode, long? exceptLineId = null)
    {
        var locationByCode = _locations
            .Where(location => !string.IsNullOrWhiteSpace(location.Code))
            .ToDictionary(location => location.Code, location => location.Id, StringComparer.OrdinalIgnoreCase);
        var normalizedHu = NormalizeHuValue(huCode);

        var available = GetStockRows()
            .Where(row => row.ItemId == itemId && row.Qty > 0)
            .Where(row => !locationId.HasValue
                          || locationByCode.TryGetValue(row.LocationCode, out var rowLocationId)
                          && rowLocationId == locationId.Value)
            .Where(row => string.IsNullOrWhiteSpace(normalizedHu)
                          || string.Equals(NormalizeHuValue(row.Hu), normalizedHu, StringComparison.OrdinalIgnoreCase))
            .Sum(row => row.Qty);

        var requested = _docLines
            .Where(line => line.Id != exceptLineId
                           && line.ItemId == itemId
                           && line.QtyBase > QtyTolerance)
            .Where(line => !locationId.HasValue || line.FromLocationId == locationId.Value)
            .Where(line => string.IsNullOrWhiteSpace(normalizedHu)
                           || string.Equals(NormalizeHuValue(line.FromHu), normalizedHu, StringComparison.OrdinalIgnoreCase))
            .Sum(line => line.QtyBase);

        return Math.Max(0, available - requested);
    }

    private bool TryResolveWriteOffSource(long itemId, out Location? location, out string? huCode, out double availableQty)
    {
        location = null;
        huCode = null;
        availableQty = 0;

        var rows = GetStockRows()
            .Where(row => row.ItemId == itemId && row.Qty > 0)
            .Select(row => new
            {
                Row = row,
                Location = _locations.FirstOrDefault(candidate =>
                    string.Equals(candidate.Code, row.LocationCode, StringComparison.OrdinalIgnoreCase)),
                Hu = NormalizeHuValue(row.Hu)
            })
            .Where(entry => entry.Location != null)
            .ToList();
        if (rows.Count == 0)
        {
            return false;
        }

        var selectedLocation = DocFromCombo.SelectedItem as Location;
        var selectedHu = NormalizeHuValue(GetSelectedHuCode(DocHuCombo));

        var scopedRows = rows;
        if (selectedLocation != null)
        {
            var rowsByLocation = scopedRows
                .Where(entry => entry.Location!.Id == selectedLocation.Id)
                .ToList();
            if (rowsByLocation.Count > 0)
            {
                scopedRows = rowsByLocation;
            }
        }

        if (!string.IsNullOrWhiteSpace(selectedHu))
        {
            var rowsByHu = scopedRows
                .Where(entry => string.Equals(entry.Hu, selectedHu, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (rowsByHu.Count > 0)
            {
                scopedRows = rowsByHu;
            }
        }

        var preferred = scopedRows
            .GroupBy(entry => new { entry.Location!.Id, entry.Location.Code, entry.Hu })
            .Select(group => new
            {
                LocationId = group.Key.Id,
                LocationCode = group.Key.Code,
                HuCode = group.Key.Hu,
                Qty = group.Sum(entry => entry.Row.Qty)
            })
            .OrderByDescending(entry => selectedLocation != null && entry.LocationId == selectedLocation.Id)
            .ThenByDescending(entry => !string.IsNullOrWhiteSpace(selectedHu) && string.Equals(entry.HuCode, selectedHu, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(entry => entry.Qty)
            .ThenBy(entry => entry.LocationCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.HuCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (preferred == null)
        {
            return false;
        }

        location = _locations.FirstOrDefault(candidate => candidate.Id == preferred.LocationId);
        if (location == null)
        {
            return false;
        }

        huCode = preferred.HuCode;
        availableQty = preferred.Qty;
        return availableQty > QtyTolerance;
    }

    private DocLine? FindCurrentDocLine(long lineId)
    {
        var line = _docLines.FirstOrDefault(entry => entry.Id == lineId);
        if (line == null || _doc == null)
        {
            return null;
        }

        return new DocLine
        {
            Id = line.Id,
            DocId = _doc.Id,
            OrderLineId = line.OrderLineId,
            ItemId = line.ItemId,
            Qty = line.QtyBase,
            QtyInput = line.QtyInput,
            UomCode = line.UomCode,
            FromLocationId = line.FromLocationId,
            ToLocationId = line.ToLocationId,
            FromHu = line.FromHu,
            ToHu = line.ToHu,
            PackSingleHu = line.PackSingleHu
        };
    }

    private void UpdatePartialUi()
    {
        if (_doc?.OrderId.HasValue == true)
        {
            LoadOrderQuantities(_doc.OrderId.Value);
        }
        else
        {
            _orderedQtyByOrderLine.Clear();
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

    private bool TryGetOrderedQty(long orderLineId, out double orderedQty)
    {
        return _orderedQtyByOrderLine.TryGetValue(orderLineId, out orderedQty);
    }

    private (double? AvailableQty, bool ShowAvailableLabel) GetAvailableQtyForDialog(long itemId, DocLineDisplay? currentLine = null)
    {
        if (_doc?.Type != DocType.Outbound)
        {
            if (_doc?.Type == DocType.WriteOff)
            {
                var locationId = currentLine?.ItemId == itemId ? currentLine.FromLocationId : null;
                var huCode = currentLine?.ItemId == itemId ? NormalizeHuValue(currentLine.FromHu) : null;
                return (GetRemainingWriteOffQty(itemId, locationId, huCode, currentLine?.Id), true);
            }

            if (_doc?.Type == DocType.Move)
            {
                if (DocFromCombo.SelectedItem is Location fromLocation)
                {
                    var fromHu = (DocHuFromCombo.SelectedItem as HuOption)?.Code;
                    return (GetAvailableQty(itemId, fromLocation.Id, fromHu), true);
                }

                return (null, true);
            }

            return (null, false);
        }

        if (DocFromCombo.SelectedItem is Location outboundFrom)
        {
            var outboundFromHu = GetSelectedHuCode(DocHuCombo);
            return (GetAvailableQty(itemId, outboundFrom.Id, outboundFromHu), true);
        }

        var availableByItem = GetItemAvailability();
        return (availableByItem.TryGetValue(itemId, out var qty) ? qty : 0, true);
    }

    private bool TryValidateOutboundStock(long docId)
    {
        var lines = _services.WpfReadApi.TryGetDocLines(docId, out var apiLines)
            ? apiLines
            : Array.Empty<DocLineView>();
        var requiredByItem = lines
            .Where(line => line.Qty > 0)
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Qty));
        if (requiredByItem.Count == 0)
        {
            return true;
        }

        var availableByItem = GetItemAvailability();
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

        var packagings = _services.WpfPackagingApi.TryGetPackagings(itemId, includeInactive: true, out var apiPackagings)
            ? apiPackagings
            : Array.Empty<ItemPackaging>();
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

        var totalsByHuAll = GetHuTotals();

        if (_doc.Type == DocType.Move)
        {
            var fromLocation = DocFromCombo.SelectedItem as Location;
            if (fromLocation != null)
            {
                var codes = GetHuCodesByLocation(fromLocation.Id);
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
            Location? selectedLocation;
            if (_doc.Type == DocType.Inbound || _doc.Type == DocType.ProductionReceipt)
            {
                selectedLocation = ResolveDefaultReceiptLocation(preferAutoEnabled: true);
            }
            else if (_doc.Type == DocType.Inventory)
            {
                selectedLocation = DocToCombo.SelectedItem as Location;
            }
            else
            {
                selectedLocation = DocFromCombo.SelectedItem as Location;
            }

            if (selectedLocation != null)
            {
                var totalsByLocation = GetHuTotalsByLocation(selectedLocation.Id);
                var freeCodes = _doc.Type == DocType.Inbound || _doc.Type == DocType.Inventory || _doc.Type == DocType.ProductionReceipt
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
            var hus = _services.WpfHuApi.TryGetHus(null, 2000, out var apiHus)
                ? apiHus
                : Array.Empty<HuRecord>();
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
        var locationCode = GetLocationCode(locationId);
        if (!string.IsNullOrWhiteSpace(locationCode))
        {
            foreach (var row in GetStockRows())
            {
                var huCode = NormalizeHuValue(row.Hu);
                if (!string.Equals(row.LocationCode, locationCode, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(huCode))
                {
                    continue;
                }

                totals[huCode] = totals.TryGetValue(huCode, out var current)
                    ? current + row.Qty
                    : row.Qty;
            }

            return totals;
        }

        return totals;
    }

    private IReadOnlyDictionary<string, double> GetHuTotals()
    {
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in GetStockRows())
        {
            var huCode = NormalizeHuValue(row.Hu);
            if (string.IsNullOrWhiteSpace(huCode))
            {
                continue;
            }

            totals[huCode] = totals.TryGetValue(huCode, out var current)
                ? current + row.Qty
                : row.Qty;
        }

        return totals;
    }

    private IReadOnlyList<string?> GetHuCodesByLocation(long locationId)
    {
        var locationCode = GetLocationCode(locationId);
        if (!string.IsNullOrWhiteSpace(locationCode))
        {
            var codes = GetStockRows()
                .Where(row => string.Equals(row.LocationCode, locationCode, StringComparison.OrdinalIgnoreCase))
                .Select(row => NormalizeHuValue(row.Hu))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Cast<string?>()
                .ToList();
            return codes;
        }

        return Array.Empty<string?>();
    }

    private IEnumerable<string> GetFreeHuCodes(IReadOnlyDictionary<string, double> totalsByHu)
    {
        return GetAvailableHuCodes()
            .Where(code => !totalsByHu.TryGetValue(code, out var qty) || qty <= 0.000001)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase);
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
        if (doc.Type == DocType.ProductionReceipt)
        {
            _suppressDirtyTracking = true;
            DocHuCombo.SelectedItem = _huToOptions.FirstOrDefault(option => option.Code == null) ?? _huToOptions.FirstOrDefault();
            DocHuFromCombo.SelectedItem = _huFromOptions.FirstOrDefault(option => option.Code == null) ?? _huFromOptions.FirstOrDefault();
            _suppressDirtyTracking = false;
            return;
        }

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
        var lines = _docLines
            .Where(line => line.Id > 0)
            .ToList();
        if (lines.Count == 0)
        {
            return null;
        }

        IEnumerable<string?> values = doc.Type switch
        {
            DocType.Inbound or DocType.Inventory or DocType.ProductionReceipt => lines.Select(line => line.ToHu),
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

    private sealed class OutboundSourcePool
    {
        public long ItemId { get; init; }
        public long LocationId { get; init; }
        public string? HuCode { get; init; }
        public double RemainingQty { get; set; }
    }

    private bool TryGetLineLocations(
        out Location? fromLocation,
        out Location? toLocation,
        out string? fromHu,
        out string? toHu,
        [CallerMemberName] string caller = "")
    {
        fromLocation = DocFromCombo.SelectedItem as Location;
        toLocation = DocToCombo.SelectedItem as Location;
        fromHu = null;
        toHu = null;

        if (_doc == null)
        {
            return false;
        }

        if (_doc.Type == DocType.Inbound || _doc.Type == DocType.Inventory || _doc.Type == DocType.ProductionReceipt)
        {
            fromLocation = null;
            if (_doc.Type is DocType.Inbound or DocType.ProductionReceipt)
            {
                toLocation ??= ResolveDefaultReceiptLocation(preferAutoEnabled: true);
            }
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
        else if (_doc.Type == DocType.WriteOff)
        {
            fromHu = null;
            toHu = null;
        }
        else if (_doc.Type == DocType.ProductionReceipt)
        {
            fromHu = null;
            toHu = null;
        }
        else if (_doc.Type == DocType.Inbound)
        {
            fromHu = null;
            toHu = null;
        }
        else
        {
            ApplyLineHu(_doc.Type, GetSelectedHuCode(DocHuCombo), ref fromHu, ref toHu);
        }

        if (_doc.Type == DocType.ProductionReceipt
            && !string.IsNullOrWhiteSpace(toHu)
            && !TryEnsureHuExists(toHu))
        {
            LogLineLocationValidationFailure(caller, fromLocation, toLocation, fromHu, toHu);
            return false;
        }

        var isValid = ValidateLineLocations(_doc, fromLocation, toLocation, fromHu, toHu);
        if (!isValid)
        {
            LogLineLocationValidationFailure(caller, fromLocation, toLocation, fromHu, toHu);
        }

        return isValid;
    }

    private void LogOutboundOrderBoundInfo(string message)
    {
        if (_doc?.Type != DocType.Outbound)
        {
            return;
        }

        _services.AppLogger.Info($"wpf_outbound_order_bound doc_id={_doc.Id} order_id={_doc.OrderId?.ToString(CultureInfo.InvariantCulture) ?? "-"} partial={_isPartialShipment} {message}");
    }

    private void LogOutboundOrderBoundWarn(string message)
    {
        if (_doc?.Type != DocType.Outbound)
        {
            return;
        }

        _services.AppLogger.Warn($"wpf_outbound_order_bound doc_id={_doc.Id} order_id={_doc.OrderId?.ToString(CultureInfo.InvariantCulture) ?? "-"} partial={_isPartialShipment} {message}");
    }

    private void LogHuServerFlowInfo(string message)
    {
        if (_doc == null)
        {
            return;
        }

        _services.AppLogger.Info(
            $"wpf_hu_server_flow doc_id={_doc.Id} doc_type={_doc.Type} order_id={_doc.OrderId?.ToString(CultureInfo.InvariantCulture) ?? "-"} {message}");
    }

    private void LogHuServerFlowWarn(string message)
    {
        if (_doc == null)
        {
            return;
        }

        _services.AppLogger.Warn(
            $"wpf_hu_server_flow doc_id={_doc.Id} doc_type={_doc.Type} order_id={_doc.OrderId?.ToString(CultureInfo.InvariantCulture) ?? "-"} {message}");
    }

    private void LogLineLocationValidationFailure(
        string caller,
        Location? fromLocation,
        Location? toLocation,
        string? fromHu,
        string? toHu)
    {
        if (_doc?.Type != DocType.Outbound)
        {
            return;
        }

        _services.AppLogger.Warn(
            $"wpf_outbound_order_bound line_location_validation_failed caller={caller} doc_id={_doc.Id} order_id={_doc.OrderId?.ToString(CultureInfo.InvariantCulture) ?? "-"} partial={_isPartialShipment} from_location_id={fromLocation?.Id.ToString(CultureInfo.InvariantCulture) ?? "-"} to_location_id={toLocation?.Id.ToString(CultureInfo.InvariantCulture) ?? "-"} from_hu={NormalizeHuValue(fromHu) ?? "-"} to_hu={NormalizeHuValue(toHu) ?? "-"}");
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
            case DocType.ProductionReceipt:
                if (toLocation == null)
                {
                    MessageBox.Show("Для выпуска продукции выберите локацию приёмки.", "Операция", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            case DocType.ProductionReceipt:
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

    private bool SupportsLineHuAssignment()
    {
        return _doc?.Type is DocType.Inbound or DocType.Inventory or DocType.ProductionReceipt or DocType.Outbound;
    }

    private IReadOnlyList<DocLineDisplay> GetSelectedDocLines()
    {
        return DocLinesGrid.SelectedItems
            .OfType<DocLineDisplay>()
            .ToList();
    }

    private sealed class DocLineDisplay
    {
        public long Id { get; init; }
        public long ItemId { get; init; }
        public long? OrderLineId { get; init; }
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
        public bool IsMarked { get; init; }
        public string OrderLineDisplay { get; init; } = string.Empty;
        public string OrderLineHint { get; init; } = string.Empty;
        public bool PackSingleHu { get; init; }
        public string KmDisplay { get; init; } = string.Empty;
        public bool KmDistributeEnabled { get; init; }
        public long? FromLocationId { get; init; }
        public long? ToLocationId { get; init; }
        public string? FromHu { get; init; }
        public string? ToHu { get; init; }
        public string? HuDisplay { get; init; }
        public string? FromLocation { get; init; }
        public string? ToLocation { get; init; }
    }

    private sealed class OutboundHuCandidate
    {
        public string HuCode { get; init; } = string.Empty;
        public long LocationId { get; init; }
        public string LocationDisplay { get; init; } = string.Empty;
        public double Qty { get; init; }
        public string QtyDisplay => OperationDetailsWindow.FormatQty(Qty);
    }

    private sealed record WriteOffSource(long LocationId, string? HuCode, double Qty);

    private sealed class WriteOffSourceBalance
    {
        public WriteOffSourceBalance(long locationId, string locationCode, string? huCode, double qty)
        {
            LocationId = locationId;
            LocationCode = locationCode;
            HuCode = huCode;
            Qty = qty;
        }

        public long LocationId { get; }
        public string LocationCode { get; }
        public string? HuCode { get; }
        public double Qty { get; set; }
    }

    private sealed record OrderOption(long Id, string OrderRef, OrderType Type, long? PartnerId, string PartnerDisplay)
    {
        public string DisplayName => Type == OrderType.Internal
            ? $"{OrderRef} - Внутренний выпуск"
            : string.IsNullOrWhiteSpace(PartnerDisplay)
                ? OrderRef
                : $"{OrderRef} - {PartnerDisplay}";
    }
}


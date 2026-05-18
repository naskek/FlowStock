using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using FlowStock.Core.Models;
using Microsoft.Win32;

namespace FlowStock.App;

public partial class OrderDetailsWindow : Window
{
    private const double QtyTolerance = 0.000001;
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
    private bool _productionPalletHuLocked;

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
        _productionPalletHuLocked = false;
        UpdateTypeUi();
        RefreshLineMetrics();
        SetEditingEnabled(true);
        UpdateMarkingExportButton();
        UpdatePalletButtons();
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

        var isFinalStatus = _order.Status is OrderStatus.Shipped or OrderStatus.Cancelled;
        OrderStatusText.Text = OrderStatusMapper.StatusToDisplayName(_order.Status, _order.Type);

        _lines.Clear();
        var lines = _services.WpfReadApi.TryGetOrderLines(_order.Id, out var apiLines)
            ? apiLines
            : Array.Empty<OrderLineView>();
        foreach (var line in lines)
        {
            _lines.Add(line);
        }
        _productionPalletHuLocked = HasPrintedOrFilledProductionPallets(_order.Id);

        SaveStatusText.Text = string.Empty;
        UpdateTypeUi();
        RefreshLineMetrics();
        SetEditingEnabled(!isFinalStatus);
        UpdateMarkingExportButton();
        UpdatePalletButtons();
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

    private async void ExportMarking_Click(object sender, RoutedEventArgs e)
    {
        if (!HasMarkableLines())
        {
            MessageBox.Show("В заказе нет маркируемых строк с GTIN.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_orderId.HasValue || _hasUnsavedChanges)
        {
            if (!TrySaveOrder(showFeedback: false))
            {
                return;
            }
        }

        if (!_orderId.HasValue)
        {
            MessageBox.Show("Сначала сохраните заказ.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ExportMarkingButton.IsEnabled = false;
        try
        {
            var result = await _services.WpfMarkingApi.TryExportOrderAsync(_orderId.Value).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                MessageBox.Show(result.Message, "Маркировка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (result.FileBytes != null)
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Сохранить Excel ЧЗ",
                    Filter = "Excel (*.xlsx)|*.xlsx",
                    FileName = string.IsNullOrWhiteSpace(result.FileName)
                        ? $"chestny_znak_order_{_orderId.Value}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
                        : result.FileName
                };
                if (dialog.ShowDialog(this) == true)
                {
                    File.WriteAllBytes(dialog.FileName, result.FileBytes);
                }
            }

            MessageBox.Show(result.Message, "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            LoadOrder();
        }
        finally
        {
            UpdateMarkingExportButton();
        }
    }

    private async void PlanPallets_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsurePalletPlanningReady())
        {
            return;
        }

        if (!await EnsureSavedForPalletActionAsync().ConfigureAwait(true))
        {
            return;
        }

        if (!_orderId.HasValue)
        {
            MessageBox.Show("Сначала сохраните заказ.", "Паллеты", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PlanPalletsButton.IsEnabled = false;
        PrintPalletLabelsButton.IsEnabled = false;
        try
        {
            var result = await _services.WpfProductionPalletApi.TryPlanOrderAsync(_orderId.Value).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                MessageBox.Show(result.Message, "Паллеты", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var message =
                $"{result.Message}{Environment.NewLine}{Environment.NewLine}" +
                $"Запланировано паллет: {result.PlannedPalletCount}{Environment.NewLine}" +
                $"Запланировано количество: {FormatQty(result.PlannedQty)}{Environment.NewLine}" +
                $"Осталось наполнить: {FormatQty(result.RemainingQty)}";
            MessageBox.Show(message, "Паллеты", MessageBoxButton.OK, MessageBoxImage.Information);
            LoadOrder();
        }
        finally
        {
            UpdatePalletButtons();
        }
    }

    private async void PrintPalletLabels_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsurePalletPrintReady())
        {
            return;
        }

        if (!await EnsureSavedForPalletActionAsync().ConfigureAwait(true))
        {
            return;
        }

        if (!_orderId.HasValue)
        {
            MessageBox.Show("Сначала сохраните заказ.", "Паллеты", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PlanPalletsButton.IsEnabled = false;
        PrintPalletLabelsButton.IsEnabled = false;
        try
        {
            var rowsResult = await _services.WpfProductionPalletApi.TryGetPrintRowsAsync(_orderId.Value).ConfigureAwait(true);
            if (!rowsResult.IsSuccess)
            {
                MessageBox.Show(rowsResult.Message, "Паллеты", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (rowsResult.Rows.Count == 0)
            {
                MessageBox.Show("Сначала сформируйте план паллет", "Паллеты", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var printResult = await Task.Run(() => _services.PalletLabelPrinter.Print(rowsResult.Rows)).ConfigureAwait(true);
            if (!printResult.IsSuccess)
            {
                MessageBox.Show(printResult.Message, "Паллеты", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var markResult = await _services.WpfProductionPalletApi.TryMarkPrintedAsync(_orderId.Value).ConfigureAwait(true);
            if (!markResult.IsSuccess)
            {
                MessageBox.Show(
                    $"Паллетные этикетки отправлены на печать, но сервер не подтвердил статус PRINTED: {markResult.Error}",
                    "Паллеты",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show("Паллетные этикетки отправлены на печать", "Паллеты", MessageBoxButton.OK, MessageBoxImage.Information);
            LoadOrder();
        }
        finally
        {
            UpdatePalletButtons();
        }
    }

    private bool TrySaveOrder(bool showFeedback)
    {
        if (!TryGetHeaderValues(allowBlankOrderRef: false, out var orderRef, out var type, out var partnerId, out var dueDate, out var comment))
        {
            return false;
        }

        if (!TryResolveBindReservedStockForSave(orderRef, type, partnerId, out var bindReservedStockForCustomer))
        {
            return false;
        }

        try
        {
            if (_orderId.HasValue)
            {
                return TryUpdateOrderViaServer(_orderId.Value, orderRef, type, partnerId, dueDate, comment, bindReservedStockForCustomer, showFeedback);
            }

            return TryCreateOrderViaServer(orderRef, type, partnerId, dueDate, comment, bindReservedStockForCustomer, showFeedback);
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
        bool? bindReservedStockForCustomer,
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
                    _lines.ToList(),
                    bindReservedStockForCustomer))
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
        BeginCustomerOrderSaveFollowUp(type);

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
        bool? bindReservedStockForCustomer,
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
                    _lines.ToList(),
                    bindReservedStockForCustomer))
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
        BeginCustomerOrderSaveFollowUp(type);

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

    private void BeginCustomerOrderSaveFollowUp(OrderType orderType)
    {
        _ = RunCustomerOrderSaveFollowUpSafeAsync(orderType);
    }

    private async Task RunCustomerOrderSaveFollowUpSafeAsync(OrderType orderType)
    {
        try
        {
            await ShowCustomerOrderSaveFollowUpAsync(orderType).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OrderDetails] customer save follow-up failed: {ex}");
            MessageBox.Show(
                "Не удалось завершить проверку резерва HU и автопереноса. Заказ сохранён — откройте его повторно.",
                "Результат сохранения заказа",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task ShowCustomerOrderSaveFollowUpAsync(OrderType orderType)
    {
        if (orderType != OrderType.Customer || !_orderId.HasValue)
        {
            return;
        }

        Debug.WriteLine($"[OrderDetails] before follow-up API order_id={_orderId.Value}");
        var followUp = await _services.WpfReadApi.ApplyCustomerOrderSaveFollowUpAsync(_orderId.Value).ConfigureAwait(true);
        Debug.WriteLine($"[OrderDetails] after follow-up API order_id={_orderId.Value}, success={followUp.IsSuccess}");

        if (!followUp.IsSuccess)
        {
            Debug.WriteLine("[OrderDetails] before follow-up error MessageBox");
            MessageBox.Show(
                followUp.ErrorMessage ?? "Не удалось получить результат резерва HU и автопереноса.",
                "Результат сохранения заказа",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Debug.WriteLine("[OrderDetails] after follow-up error MessageBox");
            return;
        }

        var payload = followUp.Payload!;
        var itemNames = _services.WpfReadApi.TryGetItems(null, out var items)
            ? items.ToDictionary(item => item.Id, item => item.Name)
            : new Dictionary<long, string>();

        var sections = new List<string>();

        if (payload.BindReservedStock)
        {
            if (payload.ReservationLines.Count > 0)
            {
                var reservationLines = payload.ReservationLines
                    .Select(line =>
                    {
                        var itemLabel = FormatItemLabel(line.ItemId, itemNames);
                        return $"• {itemLabel}: HU {line.HuCode}, {line.QtyPlanned:0.###}";
                    });
                sections.Add(
                    "Закреплённые HU (план резерва):\n"
                    + FormatLimitedLines(reservationLines, maxLines: 50));
            }
            else
            {
                sections.Add("Закреплённые HU: в плане заказа пока нет HU (см. предупреждения ниже).");
            }
        }
        else
        {
            sections.Add(
                "Резерв складских HU не включён — автоперенос с INTERNAL не выполнялся. "
                + "При следующем сохранении выберите «Да» в диалоге резерва HU, чтобы разрешить резерв и автоперенос.");
        }

        if (payload.HasTransfers)
        {
            var transferLines = payload.Transfers
                .Select(transfer =>
                {
                    var itemLabel = FormatItemLabel(transfer.ItemId, itemNames);
                    var huPart = transfer.TransferredHuCodes.Count > 0
                        ? $", HU: {string.Join(", ", transfer.TransferredHuCodes)}"
                        : string.Empty;
                    var producedPart = transfer.QtyFromProducedStock > QtyTolerance
                        ? $", со склада: {transfer.QtyFromProducedStock:0.###}"
                        : string.Empty;
                    var unproducedPart = transfer.QtyFromUnproduced > QtyTolerance
                        ? $", из выпуска: {transfer.QtyFromUnproduced:0.###}"
                        : string.Empty;
                    return $"• {transfer.SourceOrderRef} → {itemLabel}: {transfer.QtyTransferred:0.###}{unproducedPart}{producedPart}{huPart}";
                });
            sections.Add(
                "Перенос с внутренних заказов (количество в строках заказа не изменилось):\n"
                + FormatLimitedLines(transferLines, maxLines: 50));
        }
        else if (payload.BindReservedStock && string.IsNullOrWhiteSpace(payload.SkippedReason))
        {
            sections.Add("Перенос с внутренних заказов: совпадений с открытыми INTERNAL не найдено.");
        }

        if (payload.HasIgnoredAttempts)
        {
            var ignoredLines = payload.IgnoredAttempts
                .Select(attempt =>
                {
                    var itemLabel = FormatItemLabel(attempt.ItemId, itemNames);
                    var code = string.IsNullOrWhiteSpace(attempt.ReasonCode) ? "UNKNOWN" : attempt.ReasonCode;
                    return $"• {attempt.SourceOrderRef} / {itemLabel}, {attempt.Qty:0.###} — {code}: {attempt.Reason}";
                });
            sections.Add("Не выполнено (пропущено):\n" + FormatLimitedLines(ignoredLines, maxLines: 50));
        }

        if (payload.Warnings.Count > 0)
        {
            var warningLines = payload.Warnings
                .Select(warning => $"• {warning.Code}: {warning.Message}");
            sections.Add("Предупреждения:\n" + FormatLimitedLines(warningLines, maxLines: 20));
        }

        var icon = payload.HasIgnoredAttempts || payload.Warnings.Count > 0
            ? MessageBoxImage.Warning
            : MessageBoxImage.Information;

        Debug.WriteLine("[OrderDetails] before follow-up result MessageBox");
        MessageBox.Show(
            string.Join("\n\n", sections),
            "Результат сохранения заказа",
            MessageBoxButton.OK,
            icon);
        Debug.WriteLine("[OrderDetails] after follow-up result MessageBox");

        Debug.WriteLine("[OrderDetails] before LoadOrder after follow-up");
        LoadOrder();
        Debug.WriteLine("[OrderDetails] after LoadOrder after follow-up");
    }

    private static string FormatLimitedLines(IEnumerable<string> lines, int maxLines)
    {
        var materialized = lines as IList<string> ?? lines.ToList();
        if (materialized.Count <= maxLines)
        {
            return string.Join("\n", materialized);
        }

        var visible = materialized.Take(maxLines);
        var remaining = materialized.Count - maxLines;
        return string.Join("\n", visible) + $"\n... и ещё {remaining}";
    }

    private static string FormatItemLabel(long itemId, IReadOnlyDictionary<long, string> itemNames)
    {
        return itemNames.TryGetValue(itemId, out var itemName) && !string.IsNullOrWhiteSpace(itemName)
            ? itemName
            : $"товар ID {itemId}";
    }

    private bool TryResolveBindReservedStockForSave(
        string orderRef,
        OrderType type,
        long? partnerId,
        out bool? bindReservedStockForCustomer)
    {
        bindReservedStockForCustomer = null;
        if (type != OrderType.Customer)
        {
            return true;
        }

        var currentValue = _order?.Type == OrderType.Customer
            ? _order.UseReservedStock
            : false;

        const string previewChoiceExplanation =
            "Да — закрепить найденные HU и выполнить автоперенос с подходящих INTERNAL-заказов.\n" +
            "Нет — сохранить без резерва HU; автоперенос с INTERNAL выполнен не будет.";

        const string fallbackChoiceExplanation =
            "Да — сервер попробует закрепить доступные HU и перенести потребность с подходящих INTERNAL-заказов.\n" +
            "Нет — сохранить без резерва HU; автоперенос с INTERNAL выполнен не будет.";

        string dialogText;
        if (TryBuildReservationPreview(orderRef, partnerId, out var previewText))
        {
            dialogText = previewText + "\n\n" + previewChoiceExplanation;
        }
        else
        {
            var partnerDisplay = !partnerId.HasValue
                ? "контрагента"
                : (_partnersAll.FirstOrDefault(partner => partner.Id == partnerId.Value)?.DisplayName ?? $"ID {partnerId.Value}");
            dialogText =
                $"Включить резерв складских HU и автоперенос с внутренних заказов для заказа №{orderRef.Trim()} для {partnerDisplay}?\n\n"
                + fallbackChoiceExplanation;
        }

        var confirm = MessageBox.Show(
            dialogText,
            "Заказы",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            currentValue ? MessageBoxResult.Yes : MessageBoxResult.No);
        if (confirm == MessageBoxResult.Cancel)
        {
            return false;
        }

        bindReservedStockForCustomer = confirm == MessageBoxResult.Yes;
        return true;
    }

    private bool TryBuildReservationPreview(string orderRef, long? partnerId, out string previewText)
    {
        previewText = string.Empty;
        if (_lines.Count == 0)
        {
            return false;
        }

        if (!_services.WpfReadApi.TryGetHuStockRows(out var huStockRows))
        {
            if (!TryHasStockForCurrentOrderLines(out var hasStock) || !hasStock)
            {
                return false;
            }

            var partnerFallback = !partnerId.HasValue
                ? "контрагента"
                : (_partnersAll.FirstOrDefault(partner => partner.Id == partnerId.Value)?.DisplayName ?? $"ID {partnerId.Value}");
            previewText = $"Найден складской остаток. Закрепить его для заказа №{orderRef.Trim()} для контрагента {partnerFallback}?";
            return true;
        }

        var requiredByItem = _lines
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.QtyOrdered));
        if (requiredByItem.Count == 0)
        {
            return false;
        }

        var reservationEnabledItems = GetOrderReservationEnabledItemIds(requiredByItem.Keys);
        if (reservationEnabledItems.Count == 0)
        {
            return false;
        }

        var partnerName = partnerId.HasValue
            ? _partnersAll.FirstOrDefault(partner => partner.Id == partnerId.Value)?.DisplayName
            : null;

        if (!OrderReservationPromptPolicy.ShouldPrompt(
                _lines.ToList(),
                huStockRows,
                reservationEnabledItems,
                _orderId,
                out var huList))
        {
            return false;
        }

        var partnerDisplay = !string.IsNullOrWhiteSpace(partnerName) ? partnerName : "контрагента";
        previewText =
            $"Найдены остатки {string.Join(", ", huList)}. " +
            $"Закрепить их для заказа №{orderRef.Trim()} для контрагента {partnerDisplay}?";
        return true;
    }

    private HashSet<long> GetOrderReservationEnabledItemIds(IEnumerable<long> itemIds)
    {
        var targetItemIds = itemIds.ToHashSet();
        if (targetItemIds.Count == 0)
        {
            return [];
        }

        if (!_services.WpfReadApi.TryGetItems(null, out var items)
            || !_services.WpfCatalogApi.TryGetItemTypes(includeInactive: true, out var itemTypes))
        {
            return [];
        }

        var reservationTypeIds = itemTypes
            .Where(type => type.EnableOrderReservation)
            .Select(type => type.Id)
            .ToHashSet();

        return items
            .Where(item => targetItemIds.Contains(item.Id))
            .Where(item => item.ItemTypeId.HasValue && reservationTypeIds.Contains(item.ItemTypeId.Value))
            .Select(item => item.Id)
            .ToHashSet();
    }

    private bool TryHasStockForCurrentOrderLines(out bool hasStock)
    {
        hasStock = false;
        if (_lines.Count == 0)
        {
            return true;
        }

        var reservationEnabledItems = GetOrderReservationEnabledItemIds(_lines.Select(line => line.ItemId));
        if (reservationEnabledItems.Count == 0)
        {
            return true;
        }

        if (!_services.WpfReadApi.TryGetItemAvailability(out var availability))
        {
            return false;
        }

        hasStock = _lines
            .GroupBy(line => line.ItemId)
            .Where(group => reservationEnabledItems.Contains(group.Key))
            .Any(group => availability.TryGetValue(group.Key, out var qty) && qty > QtyTolerance);
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
        var orderType = GetSelectedOrderType();
        var purpose = orderType == OrderType.Internal
            ? ProductionLinePurpose.InternalStock
            : ProductionLinePurpose.CustomerOrder;
        var existingLine = _lines.FirstOrDefault(line => line.ItemId == item.Id);
        if (existingLine != null)
        {
            SelectOrderLine(existingLine);
            MessageBox.Show(
                $"Строка с товаром \"{existingLine.ItemName}\" уже добавлена. Измените количество в существующей строке при необходимости.",
                "Заказы",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

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
        _lines.Add(new OrderLineView
        {
            ItemId = item.Id,
            ItemName = item.Name,
            Barcode = item.Barcode,
            Gtin = item.Gtin,
            QtyOrdered = qtyBase,
            ProductionPurpose = purpose
        });

        RefreshLineMetrics();
        MarkDirty();
    }

    private void SelectOrderLine(OrderLineView line)
    {
        OrderLinesGrid.SelectedItem = line;
        OrderLinesGrid.ScrollIntoView(line);
        _selectedLine = line;
        EditLineButton.IsEnabled = EnsureEditable(false);
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
        OrderLinesGrid.Items.Refresh();
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

    private void MixedPalletCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureEditable())
        {
            return;
        }

        if (_productionPalletHuLocked)
        {
            LoadOrder();
            MessageBox.Show("Паллетные этикетки уже напечатаны или паллета наполнена. Переназначение общего HU запрещено.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (sender is not System.Windows.Controls.CheckBox checkBox || checkBox.DataContext is not OrderLineView line)
        {
            MessageBox.Show("Выберите строку.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        line.ProductionPalletGroup = checkBox.IsChecked == true
            ? "MIX-1"
            : null;
        MarkDirty();
        OrderLinesGrid.Items.Refresh();
        UpdatePalletButtons();
    }

    private void OrderLinesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedLine = OrderLinesGrid.SelectedItem as OrderLineView;
        DeleteLineButton.IsEnabled = _selectedLine != null && EnsureEditable(false);
        EditLineButton.IsEnabled = _selectedLine != null && EnsureEditable(false);
        UpdateMarkingExportButton();
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
        UpdateMarkingExportButton();
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
        if (_order != null && _order.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            if (showMessage)
            {
                MessageBox.Show($"{OrderStatusMapper.StatusToDisplayName(_order.Status, _order.Type)} заказ нельзя редактировать.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return false;
        }

        return true;
    }

    private bool EnsurePalletPlanningReady()
    {
        if (_order != null && _order.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
        {
            MessageBox.Show(
                $"{OrderStatusMapper.StatusToDisplayName(_order.Status, _order.Type)} заказ недоступен для подготовки паллет.",
                "Паллеты",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private bool EnsurePalletPrintReady()
    {
        if (_order != null && _order.Status is OrderStatus.Cancelled)
        {
            MessageBox.Show(
                $"{OrderStatusMapper.StatusToDisplayName(_order.Status, _order.Type)} заказ недоступен для печати паллетных этикеток.",
                "Паллеты",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private Task<bool> EnsureSavedForPalletActionAsync()
    {
        if (!_orderId.HasValue || _hasUnsavedChanges)
        {
            return Task.FromResult(TrySaveOrder(showFeedback: false));
        }

        return Task.FromResult(true);
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
        UpdateMarkingExportButton();
        UpdatePalletButtons();
        UpdateTypeUi();
    }

    private void UpdateMarkingExportButton()
    {
        if (!IsInitialized)
        {
            return;
        }

        ExportMarkingButton.IsEnabled = OrderMarkingExportUiPolicy.CanExport(_order, _lines.ToList());
    }

    private void UpdatePalletButtons()
    {
        if (!IsInitialized)
        {
            return;
        }

        var canPlan = _orderId.HasValue && _order?.Status is not (OrderStatus.Shipped or OrderStatus.Cancelled);
        var canPrint = _orderId.HasValue && _order?.Status is not OrderStatus.Cancelled;
        PlanPalletsButton.IsEnabled = canPlan;
        PrintPalletLabelsButton.IsEnabled = canPrint;
        OpenProductionReceiptButton.IsEnabled = _orderId.HasValue && GetProductionReceiptsForOrder(_orderId.Value).Count > 0;
        OrderLinesGrid.Tag = EnsureEditable(false) && !_productionPalletHuLocked;
    }

    private void OpenProductionReceipt_Click(object sender, RoutedEventArgs e)
    {
        if (!_orderId.HasValue)
        {
            return;
        }

        var productionReceipts = GetProductionReceiptsForOrder(_orderId.Value);
        if (productionReceipts.Count == 0)
        {
            MessageBox.Show(
                "Черновик выпуска для этого заказа не найден.",
                "Заказы",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var docToOpen = ResolveProductionReceiptToOpen(productionReceipts);
        if (docToOpen == null)
        {
            MessageBox.Show(
                "Черновик выпуска для этого заказа не найден.",
                "Заказы",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var window = new OperationDetailsWindow(_services, docToOpen.Id)
            {
                Owner = this
            };
            window.ShowDialog();
            LoadOrder();
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error($"Open production receipt failed for order_id={_orderId.Value}, doc_id={docToOpen.Id}", ex);
            MessageBox.Show(
                "Не удалось открыть выпуск. Подробности записаны в лог.",
                "Заказы",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static Doc? ResolveProductionReceiptToOpen(IReadOnlyList<Doc> productionReceipts)
    {
        var draft = productionReceipts
            .Where(doc => doc.Status == DocStatus.Draft)
            .OrderByDescending(doc => doc.CreatedAt)
            .ThenByDescending(doc => doc.Id)
            .FirstOrDefault();
        if (draft != null)
        {
            return draft;
        }

        return productionReceipts
            .Where(doc => doc.Status == DocStatus.Closed)
            .OrderByDescending(doc => doc.ClosedAt ?? doc.CreatedAt)
            .ThenByDescending(doc => doc.Id)
            .FirstOrDefault();
    }

    private IReadOnlyList<Doc> GetProductionReceiptsForOrder(long orderId)
    {
        try
        {
            return _services.DataStore.GetDocsByOrder(orderId)
                .Where(doc => doc.Type == DocType.ProductionReceipt)
                .ToList();
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error($"Load production receipts for order_id={orderId} failed", ex);
            return Array.Empty<Doc>();
        }
    }

    private bool HasMarkableLines()
    {
        return _lines.Any(line => !string.IsNullOrWhiteSpace(line.Gtin));
    }

    private bool HasPrintedOrFilledProductionPallets(long orderId)
    {
        try
        {
            return _services.DataStore.GetDocsByOrder(orderId)
                .Where(doc => doc.Type == DocType.ProductionReceipt)
                .SelectMany(doc => _services.DataStore.GetProductionPalletsByDoc(doc.Id))
                .Any(pallet =>
                    !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(pallet.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private void UpdateTypeUi()
    {
        var type = GetSelectedOrderType();
        var canEdit = _order?.Status is not (OrderStatus.Shipped or OrderStatus.Cancelled);

        TypeCombo.IsEnabled = canEdit;
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

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", CultureInfo.CurrentCulture);
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


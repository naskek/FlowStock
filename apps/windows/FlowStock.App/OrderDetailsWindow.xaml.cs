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
using FlowStock.Core.Services;
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
    private bool _isQtyPersistInProgress;
    private readonly CustomerOrderHuBindingCoordinator _huBinding;

    public OrderDetailsWindow(AppServices services)
    {
        _services = services;
        _huBinding = new CustomerOrderHuBindingCoordinator(
            services.WpfReadApi,
            orderId => services.DataStore.GetOrderReceiptPlanLines(orderId));
        InitializeComponent();
        InitializeData();
        LoadPartners();
        PrepareNewOrder();
    }

    public OrderDetailsWindow(AppServices services, long orderId)
    {
        _services = services;
        _huBinding = new CustomerOrderHuBindingCoordinator(
            services.WpfReadApi,
            id => services.DataStore.GetOrderReceiptPlanLines(id));
        _orderId = orderId;
        InitializeComponent();
        InitializeData();
        LoadPartners();
        LoadOrder();
    }

    private void InitializeData()
    {
        OrderLinesGrid.ItemsSource = _huBinding.Lines;
        PartnerCombo.ItemsSource = _partners;
        TypeCombo.ItemsSource = _typeOptions;

        OrderRefBox.TextChanged += OrderHeaderChanged;
        TypeCombo.SelectionChanged += TypeCombo_SelectionChanged;
        PartnerCombo.SelectionChanged += OrderHeaderChanged;
        PartnerCombo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler(PartnerCombo_TextChanged));
        DueDatePicker.SelectedDateChanged += OrderHeaderChanged;
        CommentBox.TextChanged += OrderHeaderChanged;
    }

    private void OrderDetailsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!DeleteKeyGesture.IsDeleteGesture(e)
            || !OrderLinesGrid.IsKeyboardFocusWithin
            || !CanDeleteSelectedOrderLine())
        {
            return;
        }

        e.Handled = true;
        DeleteLine_Click(OrderLinesGrid, new RoutedEventArgs());
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
        _huBinding.ResetForNewOrder();
        UpdateTypeUi();
        RefreshLineMetrics();
        SetEditingEnabled(true);
        UpdateMarkingExportButton();
        UpdatePalletButtons();
        SaveStatusText.Text = string.Empty;
        EndLoad();
    }

    private void ReloadCanonicalOrderStateAfterPersist(long? reselectLineId = null)
    {
        if (!_orderId.HasValue)
        {
            return;
        }

        LoadOrder(reselectLineId);
    }

    private void LoadOrder(long? reselectLineId = null)
    {
        if (!_orderId.HasValue)
        {
            PrepareNewOrder();
            return;
        }

        BeginLoad();
        _huBinding.BeginLoad();
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

        var isFinalStatus = _order.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged;
        OrderStatusText.Text = OrderStatusMapper.StatusToDisplayName(_order.Status, _order.Type);

        _lines.Clear();
        var lines = _services.WpfReadApi.TryGetOrderLines(_order.Id, out var apiLines)
            ? apiLines
            : Array.Empty<OrderLineView>();
        foreach (var line in lines)
        {
            line.MixedPalletGroupNumber = ProductionPalletGroupHelper.ParseNumber(line.ProductionPalletGroup);
            _lines.Add(line);
        }
        _productionPalletHuLocked = HasPrintedOrFilledProductionPallets(_order.Id);

        SaveStatusText.Text = string.Empty;
        UpdateTypeUi();
        RefreshLineMetrics();
        SetEditingEnabled(!isFinalStatus);
        UpdateMarkingExportButton();
        UpdatePalletButtons();
        SyncHuBindingLines();
        _huBinding.EndLoad();
        ApplyProductionHuCodesFromStore(_order.Id);
        EndLoad();
        RestoreSelectedOrderLine(reselectLineId ?? _selectedLine?.Id);
        ForceOrderLinesGridRefresh();
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

        if (!ConfirmAndApplyCustomerWarehouseHuProposal())
        {
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

            if (!result.ProductionRequired)
            {
                MessageBox.Show(result.Message, "Паллеты", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadOrder();
                return;
            }

            var hasPalletPlan = result.PlannedPalletCount > 0 || HasOpenProductionPalletPlan(_orderId.Value);
            if (!hasPalletPlan)
            {
                _services.AppLogger.Error(
                    $"Production pallet plan returned success without pallets for order_id={_orderId.Value}: " +
                    $"planned_pallet_count={result.PlannedPalletCount}, prd_doc_id={result.PrdDocId}, was_existing={result.WasExisting}");
                MessageBox.Show(
                    "Сервер подтвердил операцию, но план паллет не создан. Проверьте строки заказа и max_qty_per_hu.",
                    "Паллеты",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var printRowsResult = await _services.WpfProductionPalletApi.TryGetPrintRowsAsync(_orderId.Value).ConfigureAwait(true);
            if (!printRowsResult.IsSuccess)
            {
                _services.AppLogger.Error(
                    $"Production pallet print rows failed after plan for order_id={_orderId.Value}: {printRowsResult.Message}");
            }
            else if (printRowsResult.Rows.Count == 0)
            {
                _services.AppLogger.Error(
                    $"Production pallet print rows empty after plan for order_id={_orderId.Value}, planned_pallet_count={result.PlannedPalletCount}");
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

    private async void DeletePalletPlan_Click(object sender, RoutedEventArgs e)
    {
        if (!_orderId.HasValue)
        {
            MessageBox.Show("Сначала сохраните заказ.", "Паллеты", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!HasOpenProductionPalletPlan(_orderId.Value))
        {
            MessageBox.Show("План паллет не найден.", "Паллеты", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var hasPrintedLabels = HasPrintedProductionPallets(_orderId.Value);
        var confirmMessage = hasPrintedLabels
            ? "Паллетные этикетки уже были напечатаны. После удаления плана старые этикетки использовать нельзя. Продолжить?"
            : "Удалить текущий план паллет? После этого нужно будет сформировать план заново.";
        var confirmResult = MessageBox.Show(
            confirmMessage,
            "Паллеты",
            MessageBoxButton.YesNo,
            hasPrintedLabels ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        DeletePalletPlanButton.IsEnabled = false;
        PlanPalletsButton.IsEnabled = false;
        PrintPalletLabelsButton.IsEnabled = false;
        try
        {
            var result = await _services.WpfProductionPalletApi.TryCancelPlanAsync(_orderId.Value).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                MessageBox.Show(result.Message, "Паллеты", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(
                $"{result.Message}{Environment.NewLine}{Environment.NewLine}" +
                $"Удалено паллет: {result.RemovedPalletCount}{Environment.NewLine}" +
                $"Удалено строк выпуска: {result.RemovedLineCount}",
                "Паллеты",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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
                var emptyMessage = HasOpenProductionPalletPlan(_orderId.Value)
                    ? "План паллет есть, но сервер не вернул строки для печати. Проверьте журнал приложения."
                    : _order?.Type == OrderType.Customer
                        ? "Нет привязанных HU для печати. Сначала привяжите HU к заказу или сформируйте план паллет."
                        : "Сначала сформируйте план паллет";
                _services.AppLogger.Error(
                    $"Production pallet print rows empty for order_id={_orderId.Value}, order_type={_order?.Type}, has_open_plan={HasOpenProductionPalletPlan(_orderId.Value)}");
                MessageBox.Show(emptyMessage, "Паллеты", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var printRows = rowsResult.Rows;
            var coreRows = printRows
                .Select(row => new FlowStock.Core.Models.ProductionPalletPrintRow
                {
                    PalletId = row.PalletId,
                    OrderId = row.OrderId,
                    OrderRef = row.OrderRef,
                    ClientName = row.ClientName,
                    PrdDocId = 0,
                    PrdRef = row.PrdRef,
                    HuCode = row.HuCode,
                    ItemId = 0,
                    ItemName = row.ItemName,
                    Brand = row.Brand,
                    Qty = row.Qty,
                    Uom = row.Uom,
                    PalletNo = row.PalletNo,
                    PalletCount = row.PalletCount,
                    StoragePlace = row.StoragePlace,
                    ProductionDate = row.ProductionDate,
                    Comment = row.Comment,
                    IsMixedPallet = row.IsMixedPallet,
                    Composition = row.Composition,
                    Status = row.Status,
                    SourceType = row.SourceType
                })
                .ToArray();
            var groups = FlowStock.Core.Services.PalletLabelPrintSelectionService.BuildGroups(coreRows);
            var dialog = new PalletLabelPrintSelectionWindow(groups)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var selectedRows = dialog.MapSelectedRows(printRows);
            if (selectedRows.Count == 0)
            {
                MessageBox.Show("Выберите хотя бы одну паллетную этикетку", "Паллеты", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var printResult = await Task.Run(() => _services.PalletLabelPrinter.Print(selectedRows)).ConfigureAwait(true);
            if (!printResult.IsSuccess)
            {
                MessageBox.Show(printResult.Message, "Паллеты", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_order?.Type != OrderType.Customer || HasOpenProductionPalletPlan(_orderId.Value))
            {
                var selectedPalletIds = dialog.SelectedProductionPalletIds;
                if (selectedPalletIds.Count > 0)
                {
                    var markResult = await _services.WpfProductionPalletApi.TryMarkPrintedAsync(_orderId.Value, selectedPalletIds).ConfigureAwait(true);
                    if (!markResult.IsSuccess)
                    {
                        MessageBox.Show(
                            $"Паллетные этикетки отправлены на печать, но сервер не подтвердил статус PRINTED: {markResult.Error}",
                            "Паллеты",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }
                }
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
        bool showFeedback,
        IReadOnlyList<OrderLineView>? linesOverride = null)
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
                    linesOverride ?? _lines.ToList(),
                    false))
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

        if (!result.IsSuccess)
        {
            if (OrderLineEditPrevalidation.ShouldReloadLineMetricsAfterFailedPersist(false))
            {
                TryRefreshPersistedOrderLineMetricsFromApi(type);
            }

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
        ReloadCanonicalOrderStateAfterPersist(_selectedLine?.Id);

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            MessageBox.Show(result.Message, "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        if (type == OrderType.Customer && !TryApplyHuReservationsAfterSave())
        {
            return false;
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
                    _lines.ToList(),
                    false))
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

        if (type == OrderType.Customer && !TryApplyHuReservationsAfterSave())
        {
            return false;
        }

        if (showFeedback)
        {
            SaveStatusText.Text = "Сохранено";
        }

        return true;
    }

    private bool TryApplyHuReservationsAfterSave()
    {
        if (GetSelectedOrderType() != OrderType.Customer || !_orderId.HasValue)
        {
            return true;
        }

        SyncHuBindingLines();
        _huBinding.RefreshCandidatesForApply();
        var applyLines = _huBinding.BuildApplyLines();
        if (applyLines.Count == 0)
        {
            return true;
        }

        return TryApplyHuReservationLines(applyLines, reloadAfterSuccess: false);
    }

    private bool ConfirmAndApplyCustomerWarehouseHuProposal()
    {
        if (_order?.Type != OrderType.Customer || !_orderId.HasValue)
        {
            return true;
        }

        SyncHuBindingLines();
        _huBinding.RefreshCandidatesForApply();
        var dialog = new CustomerHuReservationProposalWindow(_huBinding.Lines)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        return TryApplyHuReservationLines(dialog.BuildApplyLines(), reloadAfterSuccess: true);
    }

    private bool TryApplyHuReservationLines(
        IReadOnlyList<WpfHuReservationApplyLineRequest> applyLines,
        bool reloadAfterSuccess)
    {
        if (!_orderId.HasValue || applyLines.Count == 0)
        {
            return true;
        }

        if (!_services.WpfReadApi.TryApplyHuReservations(_orderId.Value, applyLines, out _, out var error))
        {
            MessageBox.Show(
                BuildHuReservationApplyErrorMessage(error),
                "Привязка HU",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        _huBinding.MarkApplyCommitted();
        if (reloadAfterSuccess)
        {
            LoadOrder(_selectedLine?.Id);
        }
        else
        {
            SyncHuBindingLines();
        }

        return true;
    }

    private static string BuildHuReservationApplyErrorMessage(WpfHuReservationApplyError? error)
    {
        if (error == null)
        {
            return "Сервер отклонил привязку HU. Проверьте выбор HU и повторите сохранение.";
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(error.Message))
        {
            parts.Add(error.Message);
        }
        else if (!string.IsNullOrWhiteSpace(error.ErrorCode))
        {
            parts.Add(error.ErrorCode);
        }

        if (error.Problems.Count > 0)
        {
            parts.Add(string.Join(Environment.NewLine, error.Problems));
        }

        return parts.Count == 0
            ? "Сервер отклонил привязку HU."
            : string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    private void HuPickerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.DataContext is not CustomerOrderLinePresentation row)
        {
            return;
        }

        if (!EnsureEditable())
        {
            return;
        }

        var state = row.State;
        if (!state.IsHuPickerEnabled)
        {
            var disabledMessage = string.IsNullOrWhiteSpace(state.HuPickerToolTip)
                ? "Привязка HU недоступна для этой строки."
                : state.HuPickerToolTip;
            MessageBox.Show(
                disabledMessage,
                "Привязка HU",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!_huBinding.EnsureLineCandidatesLoaded(state.ClientLineKey))
        {
            MessageBox.Show(
                "Не удалось загрузить доступные HU. Проверьте связь с сервером и повторите.",
                "Привязка HU",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var pickerCandidates = state.GetPickerCandidates();
        if (pickerCandidates.Count == 0)
        {
            MessageBox.Show(
                "Нет доступных HU",
                "Привязка HU",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var remaining = state.ManualBindingCapacity;
        var picker = new HuReservationPickerWindow(
            state.Line.ItemName,
            state.Line.QtyOrdered,
            remaining,
            pickerCandidates,
            state.SelectedHuCodes,
            _huBinding.GetSelectedHuCodesOnOtherLines(state.ClientLineKey))
        {
            Owner = this
        };
        if (picker.ShowDialog() != true)
        {
            return;
        }

        _huBinding.ApplyPickerSelection(state.ClientLineKey, picker.SelectedHuCodes);
        if (!TryApplyHuReservationLines(
                [
                    new WpfHuReservationApplyLineRequest
                    {
                        OrderLineId = state.Line.Id,
                        SelectedHuCodes = picker.SelectedHuCodes
                    }
                ],
                reloadAfterSuccess: true))
        {
            _huBinding.RefreshCandidatesForApply();
            return;
        }
    }

    private void SyncHuBindingLines()
    {
        if (GetSelectedOrderType() != OrderType.Customer)
        {
            return;
        }

        _huBinding.SetOrderContext(_orderId, OrderType.Customer, _lines);
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
        var line = new OrderLineView
        {
            ItemId = item.Id,
            ItemName = item.Name,
            Barcode = item.Barcode,
            Gtin = item.Gtin,
            QtyOrdered = qtyBase,
            ProductionPurpose = purpose
        };
        _lines.Add(line);

        RefreshLineMetrics();
        if (orderType == OrderType.Customer)
        {
            _huBinding.NotifyLineChanged(line);
        }

        MarkDirty();
    }

    private void SelectOrderLine(OrderLineView line)
    {
        OrderLinesGrid.SelectedItem = line;
        OrderLinesGrid.ScrollIntoView(line);
        _selectedLine = line;
        EditLineButton.IsEnabled = EnsureEditable(false);
    }

    private void RestoreSelectedOrderLine(long? lineId)
    {
        if (!lineId.HasValue || lineId.Value <= 0)
        {
            return;
        }

        var line = _lines.FirstOrDefault(candidate => candidate.Id == lineId.Value);
        if (line == null)
        {
            _selectedLine = null;
            return;
        }

        SelectOrderLine(line);
    }

    private void ForceOrderLinesGridRefresh()
    {
        if (GetSelectedOrderType() == OrderType.Customer)
        {
            OrderLinesGrid.ItemsSource = _huBinding.Lines;
        }
        else
        {
            OrderLinesGrid.ItemsSource = _lines;
        }

        OrderLinesGrid.Items.Refresh();
        UpdateEmptyState();
    }

    private async void EditLine_Click(object sender, RoutedEventArgs e)
    {
        if (_isQtyPersistInProgress || !EnsureEditable())
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
        var oldQty = _selectedLine.QtyOrdered;
        var qtyDialog = new QuantityUomDialog(item.BaseUom, packagings, oldQty, defaultUomCode)
        {
            Owner = this
        };
        if (qtyDialog.ShowDialog() != true)
        {
            return;
        }

        var newQty = qtyDialog.QtyBase;
        var orderType = GetSelectedOrderType();
        if (OrderLineEditPrevalidation.ShouldBlockLocalQtyApply(
                TryValidateLineQtyChange(_selectedLine, newQty, orderType, out var validationMessage)))
        {
            MessageBox.Show(
                validationMessage ?? "Нельзя уменьшить количество ниже уже заполненного/выпущенного объема.",
                "Заказы",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if ((orderType == OrderType.Internal || orderType == OrderType.Customer)
            && _orderId.HasValue
            && _selectedLine.Id > 0
            && Math.Abs(oldQty - newQty) > QtyTolerance)
        {
            await TryPersistOrderLineQtyChangeAsync(_selectedLine.Id, oldQty, newQty).ConfigureAwait(true);
            return;
        }

        ApplyLocalLineQtyChange(_selectedLine, newQty, orderType);
    }

    private void ApplyLocalLineQtyChange(OrderLineView line, double newQty, OrderType orderType)
    {
        line.QtyOrdered = newQty;
        MarkDirty();
        RefreshLineMetrics();
        if (orderType == OrderType.Customer)
        {
            _huBinding.NotifyLineChanged(line);
        }

        OrderLinesGrid.Items.Refresh();
    }

    private async Task<bool> TryPersistOrderLineQtyChangeAsync(long orderLineId, double oldQty, double newQty)
    {
        if (!_orderId.HasValue
            || !TryGetHeaderValues(allowBlankOrderRef: true, out var orderRef, out var type, out var partnerId, out var dueDate, out var comment))
        {
            return false;
        }

        var line = _lines.FirstOrDefault(candidate => candidate.Id == orderLineId);
        if (line == null)
        {
            return false;
        }

        var huBefore = line.ProductionHuCodes;
        var payloadLines = OrderLineQtyPersistFlow.BuildLinesForPersist(_lines.ToList(), orderLineId, newQty);
        var payloadLine = payloadLines.First(candidate => candidate.Id == orderLineId);
        var logEntry = new OrderLineQtyEditLogEntry
        {
            OrderId = _orderId.Value,
            OrderLineId = orderLineId,
            OldQty = oldQty,
            NewQty = newQty,
            PayloadQty = payloadLine.QtyOrdered,
            HuCodesBefore = huBefore
        };

        _isQtyPersistInProgress = true;
        EditLineButton.IsEnabled = false;
        try
        {
            var result = await _services.WpfUpdateOrders.UpdateOrderAsync(
                    new WpfUpdateOrderContext(
                        _orderId.Value,
                        string.IsNullOrWhiteSpace(orderRef) ? null : orderRef,
                        type,
                        partnerId,
                        dueDate,
                        OrderStatus.InProgress,
                        comment,
                        payloadLines,
                        false))
                .ConfigureAwait(true);

            logEntry = logEntry with
            {
                PutStatus = result.IsSuccess ? 200 : (int)result.Kind
            };

            if (!result.IsSuccess)
            {
                _services.AppLogger.Info(OrderLineQtyPersistFlow.FormatQtyEditLogLine(logEntry));
                if (OrderLineEditPrevalidation.ShouldReloadLineMetricsAfterFailedPersist(false))
                {
                    ReloadCanonicalOrderStateAfterPersist(orderLineId);
                }

                var icon = result.Kind is WpfUpdateOrderResultKind.Timeout or WpfUpdateOrderResultKind.ServerUnavailable
                    ? MessageBoxImage.Error
                    : MessageBoxImage.Warning;
                MessageBox.Show(result.Message, "Заказы", MessageBoxButton.OK, icon);
                return false;
            }

            if (result.Response == null || result.Response.OrderId <= 0)
            {
                _services.AppLogger.Info(OrderLineQtyPersistFlow.FormatQtyEditLogLine(logEntry));
                MessageBox.Show("Сервер вернул неполный ответ при обновлении заказа.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            logEntry = logEntry with { ReloadStarted = true };
            _orderId = result.Response.OrderId;
            ReloadCanonicalOrderStateAfterPersist(orderLineId);
            logEntry = logEntry with
            {
                ReloadFinished = true,
                HuCodesAfter = _lines.FirstOrDefault(candidate => candidate.Id == orderLineId)?.ProductionHuCodes
            };
            _services.AppLogger.Info(OrderLineQtyPersistFlow.FormatQtyEditLogLine(logEntry));

            _hasUnsavedChanges = false;
            SaveStatusText.Text = "Сохранено";
            ForceOrderLinesGridRefresh();
            return true;
        }
        finally
        {
            _isQtyPersistInProgress = false;
            EditLineButton.IsEnabled = EnsureEditable(false);
        }
    }

    private static bool TryValidateLineQtyChange(
        OrderLineView line,
        double newQty,
        OrderType orderType,
        out string? validationMessage)
    {
        return OrderLineEditPrevalidation.TryValidateQtyChange(
            newQty,
            line,
            orderType,
            out validationMessage);
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

        if (GetSelectedOrderType() == OrderType.Customer)
        {
            _huBinding.RemoveLine(_selectedLine);
        }

        _lines.Remove(_selectedLine);
        _selectedLine = null;
        RefreshLineMetrics();
        MarkDirty();
    }

    private void OrderLinesGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (!DeleteKeyGesture.IsDeleteGesture(e) || !CanDeleteSelectedOrderLine())
        {
            return;
        }

        e.Handled = true;
        DeleteLine_Click(sender, new RoutedEventArgs());
    }

    private void MixedPalletCheckBox_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!EnsureEditable())
            {
                RestoreMixedPalletUiState();
                return;
            }

            if (_productionPalletHuLocked)
            {
                LoadOrder();
                MessageBox.Show("Паллетные этикетки уже напечатаны или паллета наполнена. Переназначение общего HU запрещено.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (sender is not System.Windows.Controls.CheckBox checkBox || TryGetLineFromGridContext(checkBox.DataContext, out var line) == false)
            {
                MessageBox.Show("Выберите строку.", "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
                RestoreMixedPalletUiState();
                return;
            }

            if (checkBox.IsChecked == true)
            {
                if (line.MixedPalletGroupNumber < 1)
                {
                    line.MixedPalletGroupNumber = 1;
                }

                line.ProductionPalletGroup = ProductionPalletGroupHelper.Format(line.MixedPalletGroupNumber);
            }
            else
            {
                line.ProductionPalletGroup = null;
            }

            line.NotifyPresentationChanged();
            MarkDirty();
            OrderLinesGrid.Items.Refresh();
            UpdatePalletButtons();
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Mixed pallet checkbox change failed", ex);
            RestoreMixedPalletUiState();
            MessageBox.Show(
                $"Не удалось изменить признак общего HU: {ex.Message}",
                "Заказы",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void MixedPalletGroupTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Length != 1 || e.Text[0] < '1' || e.Text[0] > '9';
    }

    private void MixedPalletGroupTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!EnsureEditable(false))
            {
                RestoreMixedPalletUiState();
                return;
            }

            if (_productionPalletHuLocked)
            {
                RestoreMixedPalletUiState();
                return;
            }

            if (sender is not System.Windows.Controls.TextBox textBox || TryGetLineFromGridContext(textBox.DataContext, out var line) == false)
            {
                RestoreMixedPalletUiState();
                return;
            }

            if (!line.IsMixedPalletLine)
            {
                return;
            }

            if (!int.TryParse(textBox.Text, out var groupNumber) || groupNumber < 1)
            {
                groupNumber = 1;
                line.MixedPalletGroupNumber = groupNumber;
                textBox.Text = groupNumber.ToString();
            }
            else
            {
                line.MixedPalletGroupNumber = groupNumber;
            }

            line.ProductionPalletGroup = ProductionPalletGroupHelper.Format(line.MixedPalletGroupNumber);
            line.NotifyPresentationChanged();
            MarkDirty();
            OrderLinesGrid.Items.Refresh();
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Mixed pallet group change failed", ex);
            RestoreMixedPalletUiState();
            MessageBox.Show(
                $"Не удалось изменить группу общего HU: {ex.Message}",
                "Заказы",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RestoreMixedPalletUiState()
    {
        try
        {
            OrderLinesGrid.CancelEdit(DataGridEditingUnit.Cell);
            OrderLinesGrid.Items.Refresh();
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Restore mixed pallet UI state failed", ex);
        }
    }

    private void OrderLinesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedLine = GetSelectedOrderLine();
        DeleteLineButton.IsEnabled = _selectedLine != null && EnsureEditable(false);
        EditLineButton.IsEnabled = _selectedLine != null && EnsureEditable(false);
        UpdateMarkingExportButton();
    }

    private OrderLineView? GetSelectedOrderLine()
    {
        return OrderLinesGrid.SelectedItem switch
        {
            CustomerOrderLinePresentation row => row.Line,
            OrderLineView line => line,
            _ => null
        };
    }

    private bool CanDeleteSelectedOrderLine()
    {
        return _selectedLine != null && DeleteLineButton.IsEnabled;
    }

    private static bool TryGetLineFromGridContext(object? dataContext, out OrderLineView line)
    {
        line = null!;
        switch (dataContext)
        {
            case CustomerOrderLinePresentation row:
                line = row.Line;
                return true;
            case OrderLineView direct:
                line = direct;
                return true;
            default:
                return false;
        }
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
        SyncHuBindingLines();
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

            OrderLineCanonicalPresentation.ApplyPersistedLine(line, persisted, type);
        }

        if (_order != null)
        {
            _productionPalletHuLocked = HasPrintedOrFilledProductionPallets(_order.Id);
        }

        UpdateEmptyState();
        OrderLinesGrid.Items.Refresh();
        UpdatePalletButtons();
        SyncHuBindingLines();
        return true;
    }

    private void UpdateEmptyState()
    {
        OrderLinesEmptyText.Visibility = _lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool EnsureEditable(bool showMessage = true)
    {
        if (_order != null && _order.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
        {
            if (showMessage)
            {
                var message = _order.Status == OrderStatus.Merged
                    ? "Заказ объединён с другим заказом. Выпуск по нему не требуется."
                    : $"{OrderStatusMapper.StatusToDisplayName(_order.Status, _order.Type)} заказ нельзя редактировать.";
                MessageBox.Show(message, "Заказы", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return false;
        }

        return true;
    }

    private bool EnsurePalletPlanningReady()
    {
        if (_order != null && _order.Status is OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
        {
            var message = _order.Status == OrderStatus.Merged
                ? "Заказ объединён с другим заказом. Выпуск по нему не требуется."
                : $"{OrderStatusMapper.StatusToDisplayName(_order.Status, _order.Type)} заказ недоступен для подготовки паллет.";
            MessageBox.Show(message, "Паллеты", MessageBoxButton.OK, MessageBoxImage.Information);
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

        var canPlan = _orderId.HasValue && _order?.Status is not (OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged);
        var canPrint = _orderId.HasValue && _order?.Status is not (OrderStatus.Cancelled or OrderStatus.Merged);
        var canDeletePlan = _orderId.HasValue
                            && _order?.Status is not (OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged)
                            && HasOpenProductionPalletPlan(_orderId.Value);
        PlanPalletsButton.IsEnabled = canPlan;
        PrintPalletLabelsButton.IsEnabled = canPrint;
        DeletePalletPlanButton.IsEnabled = canDeletePlan;
        OpenProductionReceiptButton.IsEnabled = _orderId.HasValue && CanOpenProductionReceipt(_orderId.Value);
        OrderLinesGrid.Tag = EnsureEditable(false) && !_productionPalletHuLocked;
    }

    private void OpenProductionReceipt_Click(object sender, RoutedEventArgs e)
    {
        if (!_orderId.HasValue)
        {
            return;
        }

        if (_order?.Type == OrderType.Customer
            && !CustomerOutboundBoundHuService.HasReceiptProductionNeed(_services.DataStore, _orderId.Value))
        {
            MessageBox.Show(
                "Выпуск не требуется: заказ уже покрыт привязанными HU/остатками.",
                "Заказы",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var productionReceipts = GetProductionReceiptsForOrder(_orderId.Value);
        var docToOpen = ResolveProductionReceiptToOpen(productionReceipts, _order?.Type);
        if (docToOpen == null)
        {
            MessageBox.Show(
                _order?.Type == OrderType.Customer
                    ? "Выпуск не требуется: заказ уже покрыт привязанными HU/остатками."
                    : "Черновик выпуска для этого заказа не найден.",
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

    private bool CanOpenProductionReceipt(long orderId)
    {
        if (_order?.Type == OrderType.Customer)
        {
            return CustomerOutboundBoundHuService.HasReceiptProductionNeed(_services.DataStore, orderId);
        }

        return GetProductionReceiptsForOrder(orderId).Count > 0;
    }

    private Doc? ResolveProductionReceiptToOpen(IReadOnlyList<Doc> productionReceipts, OrderType? orderType)
    {
        if (orderType == OrderType.Customer)
        {
            var draftWithContent = productionReceipts
                .Where(doc => doc.Status == DocStatus.Draft)
                .Where(doc => _services.DataStore.GetDocLines(doc.Id).Count > 0
                              || _services.DataStore.HasProductionPallets(doc.Id))
                .OrderByDescending(doc => doc.CreatedAt)
                .ThenByDescending(doc => doc.Id)
                .FirstOrDefault();
            if (draftWithContent != null)
            {
                return draftWithContent;
            }

            return productionReceipts
                .Where(doc => doc.Status == DocStatus.Draft)
                .OrderByDescending(doc => doc.CreatedAt)
                .ThenByDescending(doc => doc.Id)
                .FirstOrDefault();
        }

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

    private bool HasOpenProductionPalletPlan(long orderId)
    {
        try
        {
            return _services.DataStore.GetDocsByOrder(orderId)
                .Any(doc => doc.Type == DocType.ProductionReceipt
                            && doc.Status != DocStatus.Closed
                            && _services.DataStore.HasProductionPallets(doc.Id));
        }
        catch
        {
            return false;
        }
    }

    private void ApplyProductionHuCodesFromStore(long orderId)
    {
        try
        {
            var huByLine = ProductionOrderLineHuCodes.BuildByOrder(_services.DataStore, orderId);
            var productionDisplayByLine = ProductionOrderLineHuCodes.BuildProductionDisplayByOrder(_services.DataStore, orderId);
            foreach (var line in _lines)
            {
                productionDisplayByLine.TryGetValue(line.Id, out var displayEntries);
                line.ProductionHuDisplayEntries = displayEntries ?? Array.Empty<OrderLineHuDisplayEntry>();

                if (!huByLine.TryGetValue(line.Id, out var codes) || codes.Length == 0)
                {
                    continue;
                }

                var display = OrderLineCanonicalPresentation.ResolveProductionHuCodesDisplay(null, codes);
                if (!string.Equals(line.ProductionHuCodes, display, StringComparison.Ordinal))
                {
                    line.ProductionHuCodes = display;
                }
            }
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error($"Apply production HU codes for order_id={orderId} failed", ex);
        }
    }

    private bool HasPrintedProductionPallets(long orderId)
    {
        try
        {
            return _services.DataStore.GetDocsByOrder(orderId)
                .Where(doc => doc.Type == DocType.ProductionReceipt && doc.Status != DocStatus.Closed)
                .SelectMany(doc => _services.DataStore.GetProductionPalletsByDoc(doc.Id))
                .Any(pallet =>
                    !string.Equals(pallet.Status, ProductionPalletStatus.Cancelled, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(pallet.Status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
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
        var canEdit = _order?.Status is not (OrderStatus.Shipped or OrderStatus.Cancelled or OrderStatus.Merged);

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

        var isCustomer = type == OrderType.Customer;
        OrderLinesGrid.ItemsSource = isCustomer ? _huBinding.Lines : _lines;
        HuAvailableColumn.Visibility = isCustomer ? Visibility.Visible : Visibility.Collapsed;
        HuBoundColumn.Visibility = isCustomer ? Visibility.Visible : Visibility.Collapsed;
        HuRemainingColumn.Visibility = isCustomer ? Visibility.Visible : Visibility.Collapsed;
        HuPickerColumn.Visibility = isCustomer ? Visibility.Visible : Visibility.Collapsed;
        if (isCustomer)
        {
            SyncHuBindingLines();
        }
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
        _huBinding.BeginLoad();
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
        var orders = _services.WpfReadApi.TryGetOrdersPage(
            includeInternal: true,
            search: normalized,
            limit: 50,
            offset: 0,
            includeCancelledMerged: true,
            out var apiOrders)
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
        const int pageSize = 200;
        var offset = 0;
        while (_services.WpfReadApi.TryGetOrdersPage(
                   includeInternal: true,
                   search: null,
                   limit: pageSize,
                   offset: offset,
                   includeCancelledMerged: true,
                   out var apiOrders)
               && apiOrders.Count > 0)
        {
            foreach (var order in apiOrders)
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

            if (apiOrders.Count < pageSize)
            {
                break;
            }

            offset += apiOrders.Count;
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


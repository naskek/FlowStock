using System.Reflection;
using FlowStock.App;
using FlowStock.Core.Models;

namespace FlowStock.Server.Tests.Orders;

public sealed class WpfHuReservationApiGuardTests
{
    [Fact]
    public void WpfReadApiService_BuildsHuReservationCandidatesRequest()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfReadApiService.cs");
        var models = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfHuReservationApiModels.cs");

        Assert.Contains("TryGetHuReservationCandidates(", source);
        Assert.Contains("/api/orders/hu-reservation-candidates", source);
        Assert.Contains("TryPost(", source);
        Assert.Contains("[JsonPropertyName(\"order_line_id\")]", models);
        Assert.Contains("[JsonPropertyName(\"selected_hu_codes\")]", models);
        Assert.Contains("[JsonPropertyName(\"client_line_key\")]", models);
        Assert.Contains("[JsonPropertyName(\"item_id\")]", models);
        Assert.Contains("[JsonPropertyName(\"qty_ordered\")]", models);
        Assert.Contains("[JsonPropertyName(\"exclude_hu_codes\")]", models);
    }

    [Fact]
    public void WpfReadApiService_BuildsHuReservationsApplyRequest()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfReadApiService.cs");

        Assert.Contains("TryApplyHuReservations(", source);
        Assert.Contains("/hu-reservations/apply", source);
        Assert.Contains("HttpMethod.Post", source);
        Assert.Contains("JsonContent.Create(body, options: JsonOptions)", source);
    }

    [Fact]
    public void WpfReadApiService_BuildsHuBindingsApplyFinalRequest()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfReadApiService.cs");
        var models = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfHuReservationApiModels.cs");

        Assert.Contains("TryApplyFinalHuBindings(", source);
        Assert.Contains("/hu-bindings/apply-final", source);
        Assert.Contains("replace_final_selection", models);
        Assert.Contains("[JsonPropertyName(\"expected_bound_hu_codes\")]", models);
        Assert.Contains("[JsonPropertyName(\"final_hu_codes\")]", models);
        Assert.Contains("TryMapHuBindingApplyFinalError", source);
        Assert.Contains("ReadStringArray(root, \"problems\")", source);
    }

    [Fact]
    public void WpfIncomingRequests_MapsReadyHuBindingSummary()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfIncomingRequestsApiService.cs");

        Assert.Contains("ready_hu_binding_pending", source);
        Assert.Contains("ReadyHuBindingPending", source);
        Assert.Contains("ItemRequestsPending + OrderRequestsPending + ReadyHuBindingPending", source);
    }

    [Fact]
    public void WpfReadApiService_BuildsReadyHuBindingReadModelRequest()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfReadApiService.cs");
        var models = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfReadyHuBindingApiModels.cs");

        Assert.Contains("TryGetReadyHuBindingReadModel", source);
        Assert.Contains("/api/orders/hu-bindings/ready", source);
        Assert.Contains("request_type", source);
        Assert.Contains("hu_count", source);
        Assert.Contains("order_count", source);
        Assert.Contains("line_count", source);
        Assert.Contains("compatible_orders", source);
        Assert.Contains("current_bound_hu_codes", source);
        Assert.Contains("max_additional_bind_qty", source);
        Assert.Contains("WpfReadyHuBindingReadModel", models);
        Assert.Contains("WpfReadyHuBindingHuRow", models);
        Assert.Contains("WpfReadyHuBindingCompatibleOrderRow", models);
        Assert.Contains("WpfReadyHuBindingCompatibleLineRow", models);
    }

    [Fact]
    public void WpfReadApiService_BuildsHuBindingManagementRequests()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfReadApiService.cs");
        var models = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfHuBindingManageApiModels.cs");
        var session = ReadRepoFile("apps", "windows", "FlowStock.App", "HuAssignmentManagementSession.cs");

        Assert.Contains("TryGetManageItems(", source);
        Assert.Contains("TryGetManageHus(", source);
        Assert.Contains("TryGetManageTargets(", source);
        Assert.Contains("TryApplyManageHuBindings(", source);
        Assert.Contains("/api/orders/hu-bindings/manage/items", source);
        Assert.Contains("/api/orders/hu-bindings/manage/items/{itemId}/targets", source);
        Assert.Contains("/api/orders/hu-bindings/manage/apply-final", source);
        Assert.Contains("TryMapHuBindingManageApplyError", source);
        Assert.Contains("[JsonPropertyName(\"expected_hu_states\")]", models);
        Assert.Contains("[JsonPropertyName(\"expected_order_line_id\")]", models);
        Assert.Contains("[JsonPropertyName(\"order_id\")]", models);
        Assert.Contains("[JsonPropertyName(\"final_hu_codes\")]", models);
        Assert.Contains("BuildApplyRequest", session);
        Assert.Contains("ExpectedHuStates", session);
        Assert.Contains("MarkSaveSuccess", session);
    }

    [Fact]
    public void HuAssignmentManagementWindow_UsesStage5ApiAndSessionWithoutBackendRules()
    {
        var xaml = ReadRepoFile("apps", "windows", "FlowStock.App", "HuAssignmentManagementWindow.xaml");
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "HuAssignmentManagementWindow.xaml.cs");
        var controller = ReadRepoFile("apps", "windows", "FlowStock.App", "HuAssignmentManagementController.cs");
        var mainWindowXaml = ReadRepoFile("apps", "windows", "FlowStock.App", "MainWindow.xaml");
        var mainWindowSource = ReadRepoFile("apps", "windows", "FlowStock.App", "MainWindow.xaml.cs");

        Assert.Contains("Управление привязками HU", xaml);
        Assert.Contains("Управление HU", mainWindowXaml);
        Assert.Contains("HuAssignmentManagement_Click", mainWindowSource);
        Assert.Contains("new HuAssignmentManagementWindow(_services)", mainWindowSource);

        Assert.Contains("HuAssignmentManagementSession", controller);
        Assert.Contains("TryGetManageItems", controller);
        Assert.Contains("TryGetManageHus", controller);
        Assert.Contains("TryGetManageTargets", controller);
        Assert.Contains("TryApplyManageHuBindings", controller);
        Assert.Contains("BuildApplyRequest", controller);
        Assert.Contains("MarkSaveSuccess", controller);
        Assert.Contains("HU_OWNER_CHANGED", controller);
        Assert.Contains("HU_QTY_CHANGED", controller);
        Assert.Contains("NetworkFailure", controller);
        Assert.Contains("Сначала сохраните или отмените изменения перед переходом", controller);

        Assert.DoesNotContain("DataStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderHuBindingManageApplyService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplaceOrderReceiptPlanLines", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderHuBindingManageApplyService", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplaceOrderReceiptPlanLines", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("GetHuStockRows", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void IncomingRequestsWindow_ExposesReadyHuFilterAndOpensGlobalWindow()
    {
        var xaml = ReadRepoFile("apps", "windows", "FlowStock.App", "IncomingRequestsWindow.xaml");
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "IncomingRequestsWindow.xaml.cs");
        var builder = ReadRepoFile("apps", "windows", "FlowStock.App", "IncomingRequestsRowsBuilder.cs");

        Assert.Contains("RequestTypeFilterCombo", xaml);
        Assert.Contains("Привязка готовых HU", xaml);
        Assert.Contains("ManualReadyHuBinding_Click", source);
        Assert.Contains("TryGetReadyHuBindingReadModel(out var readyHuBinding)", source);
        Assert.Contains("Готовые HU", source);
        Assert.Contains("TryGetReadyHuBindingReadModel", source);
        Assert.Contains("READY_HU_BINDING_AVAILABLE", builder);
        Assert.Contains("CanApprove = false", builder);
        Assert.Contains("CanReject = false", builder);
        Assert.Contains("CanOpenDetails = true", builder);
        Assert.Contains("OpenGlobalReadyHuBinding", source);
        Assert.Contains("OpenGlobalReadyHuBinding(readyHuBinding)", source);
        Assert.Contains("OpenGlobalReadyHuBinding(row.ReadyHuBinding)", source);
        Assert.Contains("new GlobalReadyHuBindingWindow(_services, readyHuBinding)", source);
        Assert.DoesNotContain("new ReadyHuBindingWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryApplyFinalHuBindings", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryApplyHuReservations", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/hu-reservations/apply", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GlobalReadyHuBindingWindow_ExistsAndUsesApplyFinalOnly()
    {
        var xaml = ReadRepoFile("apps", "windows", "FlowStock.App", "GlobalReadyHuBindingWindow.xaml");
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "GlobalReadyHuBindingWindow.xaml.cs");
        var session = ReadRepoFile("apps", "windows", "FlowStock.App", "GlobalReadyHuBindingSession.cs");

        Assert.Contains("Глобальная привязка готовых HU", xaml);
        Assert.Contains("Авто", xaml);
        Assert.Contains("Обновить", xaml);
        Assert.Contains("Сохранить", xaml);
        Assert.Contains("Закрыть", xaml);
        Assert.Contains("TryApplyFinalHuBindings", source);
        Assert.Contains("BuildApplyFinalByOrder", session);
        Assert.Contains("ExpectedBoundHuCodes", session);
        Assert.Contains("FinalHuCodes", session);
        Assert.Contains("MarkOrderApplySuccess", source);
        Assert.Contains("Обновите список перед продолжением", source);
        Assert.DoesNotContain("TryApplyHuReservations", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryApplyHuReservations", session, StringComparison.Ordinal);
        Assert.DoesNotContain("/hu-reservations/apply", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/hu-reservations/apply", session, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrderDetailsWindow_ExposesOrderScopedHuBindingButtonForCustomerOnly()
    {
        var xaml = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml");
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        Assert.Contains("x:Name=\"ReadyHuBindingButton\"", xaml);
        Assert.Contains("Content=\"Привязка HU\"", xaml);
        Assert.Contains("ReadyHuBinding_Click", source);
        Assert.Contains("ReadyHuBindingButton.Visibility = isCustomer ? Visibility.Visible : Visibility.Collapsed;", source);
        Assert.Contains("&& !_hasUnsavedChanges", source);
        Assert.Contains("new ReadyHuBindingWindow(_services, _orderId.Value)", source);

        var palletBlockStart = xaml.IndexOf("Header=\"Паллетный план\"", StringComparison.Ordinal);
        Assert.True(palletBlockStart >= 0);
        var palletBlockEnd = xaml.IndexOf("</GroupBox>", palletBlockStart, StringComparison.Ordinal);
        Assert.True(palletBlockEnd > palletBlockStart);
        var palletBlock = xaml[palletBlockStart..palletBlockEnd];
        Assert.Contains("ReadyHuBindingButton", palletBlock);

        var linesBlockStart = xaml.IndexOf("Header=\"Строки заказа\"", StringComparison.Ordinal);
        Assert.True(linesBlockStart >= 0);
        var linesBlockEnd = xaml.IndexOf("</GroupBox>", linesBlockStart, StringComparison.Ordinal);
        Assert.True(linesBlockEnd > linesBlockStart);
        var linesBlock = xaml[linesBlockStart..linesBlockEnd];
        Assert.DoesNotContain("ReadyHuBindingButton", linesBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadyHuBindingWindow_UsesApplyFinalAndNotLegacyApply()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "ReadyHuBindingWindow.xaml.cs");
        var session = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderScopedHuBindingSession.cs");

        Assert.Contains("TryApplyFinalHuBindings", source);
        Assert.Contains("BuildApplyFinalLines", session);
        Assert.Contains("ExpectedBoundHuCodes", session);
        Assert.Contains("FinalHuCodes", session);
        Assert.Contains("Список HU изменился. Обновите заказ и повторите действие.", source);
        Assert.DoesNotContain("TryApplyHuReservations", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/hu-reservations/apply", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadyHuBindingWindow_PreservesTreeExpandedAndSelectionState()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "ReadyHuBindingWindow.xaml.cs");
        var session = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderScopedHuBindingSession.cs");

        Assert.Contains("CaptureTreeState", source);
        Assert.Contains("RestoreTreeState", source);
        Assert.Contains("CollectExpandedKeys", source);
        Assert.Contains("RestoreExpandedKeys", source);
        Assert.Contains("SelectTreeItem", source);
        Assert.Contains("CaptureUiState", session);
        Assert.Contains("ExpandedCandidateGroupKeys", session);
        Assert.Contains("ExpandedOrderGroupKeys", session);
    }

    [Fact]
    public void WarehouseProductionStateFingerprint_IncludesHuReservationStatus()
    {
        var method = typeof(MainWindow).GetMethod(
            "BuildWarehouseProductionStateFingerprint",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var unreserved = BuildWarehouseProductionStateRow(reservedOrderRef: null);
        var reserved = BuildWarehouseProductionStateRow(reservedOrderRef: "004");

        var first = Assert.IsType<string>(method!.Invoke(null, [new[] { unreserved }]));
        var second = Assert.IsType<string>(method.Invoke(null, [new[] { reserved }]));

        Assert.NotEqual(first, second);
        Assert.Contains("На складе", first);
        Assert.Contains("Зарезервирован: заказ 004", second);
        Assert.Contains("004", second);
    }

    [Fact]
    public void OrderDetailsWindow_KeepsLegacyExplicitHuBindingFlow()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        Assert.Contains("CustomerOrderHuBindingCoordinator", source);
        Assert.Contains("ConfirmAndApplyCustomerWarehouseHuProposal", source);
        Assert.Contains("TryApplyHuReservationLines", source);
        Assert.Contains("HuReservationPickerWindow", source);
        Assert.Contains("CustomerHuReservationProposalWindow", source);
        Assert.DoesNotContain("TryResolveBindReservedStockForSave", source);
        Assert.DoesNotContain("auto-redistribute-from-internal", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reserve-produced-hu", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/api/orders/redistribute", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApplyCustomerOrderSaveFollowUp", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderDetailsWindow_SaveFlow_DoesNotAutoApplyHuReservations()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        var saveBlockStart = source.IndexOf("private bool TryUpdateOrderViaServer", StringComparison.Ordinal);
        Assert.True(saveBlockStart >= 0);
        var saveBlockEnd = source.IndexOf("private bool TryApplyHuReservationsAfterSave", saveBlockStart, StringComparison.Ordinal);
        Assert.True(saveBlockEnd > saveBlockStart);
        var saveBlock = source[saveBlockStart..saveBlockEnd];

        Assert.DoesNotContain("TryApplyHuReservationsAfterSave", saveBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("TryApplyHuReservationLines", saveBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderDetailsWindow_PlanPalletsDoesNotAutoApplyWarehouseHuProposal()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        var methodStart = source.IndexOf("private async void PlanPallets_Click", StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        var methodEnd = source.IndexOf("private async void PrintPalletLabels_Click", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        Assert.DoesNotContain("ConfirmAndApplyCustomerWarehouseHuProposal", method, StringComparison.Ordinal);
        Assert.DoesNotContain("TryApplyHuReservationLines", method, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderDetailsWindow_DeletePalletPlanSavesBeforeReadingCancelOptions()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        var methodStart = source.IndexOf("private async void DeletePalletPlan_Click", StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        var methodEnd = source.IndexOf("private async void PrintPalletLabels_Click", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        var saveIndex = method.IndexOf("EnsureSavedForPalletActionAsync()", StringComparison.Ordinal);
        var hasPlanIndex = method.IndexOf("HasOpenProductionPalletPlan(_orderId.Value)", StringComparison.Ordinal);
        var optionsIndex = method.IndexOf("TryGetCancelPlanOptionsAsync(_orderId.Value)", StringComparison.Ordinal);
        Assert.True(saveIndex >= 0);
        Assert.True(hasPlanIndex > saveIndex);
        Assert.True(optionsIndex > saveIndex);
        Assert.Contains("if (!await EnsureSavedForPalletActionAsync().ConfigureAwait(true))", method);
    }

    [Fact]
    public void OrderDetailsWindow_PlanPalletsReportsAddedAndTotalCounts()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        var methodStart = source.IndexOf("private async void PlanPallets_Click", StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        var methodEnd = source.IndexOf("private void ReadyHuBinding_Click", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        var optionsIndex = method.IndexOf("TryGetCancelPlanOptionsAsync(_orderId.Value)", StringComparison.Ordinal);
        var planIndex = method.IndexOf("TryPlanOrderAsync(_orderId.Value)", StringComparison.Ordinal);
        var afterOptionsIndex = method.IndexOf("TryGetCancelPlanOptionsAsync(_orderId.Value)", planIndex, StringComparison.Ordinal);
        Assert.True(optionsIndex >= 0);
        Assert.True(planIndex > optionsIndex);
        Assert.True(afterOptionsIndex > planIndex);
        Assert.DoesNotContain("return;", method[optionsIndex..planIndex], StringComparison.Ordinal);
        Assert.Contains("activePalletCountBefore = beforeOptionsResult.Rows.Count;", method);
        Assert.Contains("activePalletCountAfter = afterOptionsResult.Rows.Count;", method);
        Assert.Contains("Добавлено паллет:", method);
        Assert.Contains("Всего активных паллет в плане:", method);
        Assert.Contains("Всего паллет в плане:", method);
        Assert.Contains("Math.Max(0, activePalletCountAfter.Value - activePalletCountBefore.Value)", method);
        Assert.DoesNotContain("Math.Max(0, result.PlannedPalletCount - activePalletCountBefore.Value)", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Запланировано паллет: {result.PlannedPalletCount}", method, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderService_CreateAndUpdateCustomerFlows_DoNotAutoBindWarehouseHu()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Core", "Services", "OrderService.cs");

        var createStart = source.IndexOf("private long CreateOrderCore", StringComparison.Ordinal);
        Assert.True(createStart >= 0);
        var createEnd = source.IndexOf("public void UpdateOrder", createStart, StringComparison.Ordinal);
        Assert.True(createEnd > createStart);
        var createMethod = source[createStart..createEnd];

        var updateStart = createEnd;
        var updateEnd = source.IndexOf("private static void ValidateIncomingLineQuantities", updateStart, StringComparison.Ordinal);
        Assert.True(updateEnd > updateStart);
        var updateMethod = source[updateStart..updateEnd];

        Assert.DoesNotContain("TryBindBestWarehouseHuForCustomerOrder", createMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("TryBindBestWarehouseHuForCustomerOrder", updateMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderService_CustomerRefreshRebuild_DoesNotAllocateFreeHuReservations()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Core", "Services", "OrderService.cs");

        var rebuildStart = source.IndexOf("private void RebuildCustomerOrderReceiptPlan", StringComparison.Ordinal);
        Assert.True(rebuildStart >= 0);
        var rebuildEnd = source.IndexOf("private static void ExhaustHuSource", rebuildStart, StringComparison.Ordinal);
        Assert.True(rebuildEnd > rebuildStart);
        var rebuildMethod = source[rebuildStart..rebuildEnd];

        Assert.DoesNotContain("BuildAvailableReservationSources", rebuildMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("ExhaustHuSource", rebuildMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("new OrderReceiptPlanLine", rebuildMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderDetailsWindow_PersistsCustomerQtyEditImmediatelyWithoutHuApply()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        Assert.Contains("orderType == OrderType.Internal || orderType == OrderType.Customer", source);
        Assert.Contains("var lineId = selectedLine.Id;", source);
        Assert.Contains("TryPersistOrderLineQtyChangeAsync(lineId, oldQty, newQty, selectedHuCodes)", source);

        var editStart = source.IndexOf("private async void EditLine_Click", StringComparison.Ordinal);
        Assert.True(editStart >= 0);
        var editEnd = source.IndexOf("private void ApplyLocalLineQtyChange", editStart, StringComparison.Ordinal);
        Assert.True(editEnd > editStart);
        var editMethod = source[editStart..editEnd];
        Assert.DoesNotContain("ConfirmAndApplyCustomerWarehouseHuProposal", editMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomerHuReservationProposalWindow", editMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("TryApplyHuReservationLines", editMethod, StringComparison.Ordinal);

        var methodStart = source.IndexOf("private async Task<bool> TryPersistOrderLineQtyChangeAsync", StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        var methodEnd = source.IndexOf("private static bool TryValidateLineQtyChange", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        Assert.Contains("WpfUpdateOrderContext", method);
        Assert.Contains("new Dictionary<long, IReadOnlyList<string>>", method);
        Assert.Contains("ReloadCanonicalOrderStateAfterPersist(orderLineId)", method);
        Assert.Contains("ForceOrderLinesGridRefresh()", method);
        Assert.DoesNotContain("TryApplyHuReservationsAfterSave", method, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderDetailsWindow_ConfirmsReservedHuReductionBeforeImmediateQtyPersist()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");
        var dialog = ReadRepoFile("apps", "windows", "FlowStock.App", "CustomerReservedHuReductionWindow.xaml.cs");

        Assert.Contains("TryConfirmCustomerReservedHuReduction", source);
        Assert.Contains("CustomerReservedHuReductionWindow", source);
        Assert.Contains("SelectedHuCodes", dialog);
        Assert.Contains("SelectedQty", dialog);

        var methodStart = source.IndexOf("private bool TryConfirmCustomerReservedHuReduction", StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        var methodEnd = source.IndexOf("private static string BuildReservedHuSourceStatus", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        Assert.Contains("out IReadOnlyList<string>? selectedHuCodes", method);
        Assert.Contains("selectedHuCodes = dialog.SelectedHuCodes", method);
        Assert.Contains("selectedHuCodes = options.Select(option => option.HuCode).ToArray()", method);
        Assert.DoesNotContain("TryApplyHuReservationLines", method, StringComparison.Ordinal);
        Assert.DoesNotContain("/hu-reservations/apply", method, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HuReservationPicker_ToggleAndApplyHandlersAreGuarded()
    {
        var picker = ReadRepoFile("apps", "windows", "FlowStock.App", "HuReservationPickerWindow.xaml.cs");
        var orderDetails = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        var pickerMethodStart = picker.IndexOf("private void PickerRow_PropertyChanged", StringComparison.Ordinal);
        Assert.True(pickerMethodStart >= 0);
        var pickerMethodEnd = picker.IndexOf("private void UpdateSummary", pickerMethodStart, StringComparison.Ordinal);
        Assert.True(pickerMethodEnd > pickerMethodStart);
        var pickerMethod = picker[pickerMethodStart..pickerMethodEnd];
        Assert.Contains("try", pickerMethod);
        Assert.Contains("catch (Exception ex)", pickerMethod);
        Assert.Contains("FailAndClose", pickerMethod);
        Assert.Contains("HasFatalError", picker);

        var applyStart = orderDetails.IndexOf("private void HuPickerButton_Click", StringComparison.Ordinal);
        Assert.True(applyStart >= 0);
        var applyEnd = orderDetails.IndexOf("private void SyncHuBindingLines", applyStart, StringComparison.Ordinal);
        Assert.True(applyEnd > applyStart);
        var applyMethod = orderDetails[applyStart..applyEnd];
        Assert.Contains("try", applyMethod);
        Assert.Contains("catch (Exception ex)", applyMethod);
        Assert.Contains("LoadOrder", applyMethod);
        Assert.Contains("picker.HasFatalError", applyMethod);
        Assert.Contains("var lineId = state.Line.Id;", applyMethod);
    }

    [Fact]
    public void OrderDetailsWindow_LoadOrderOnlyRefreshesHuCandidates()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        var methodStart = source.IndexOf("private void LoadOrder(long? reselectLineId = null)", StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        var methodEnd = source.IndexOf("private void Save_Click", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        Assert.Contains("_huBinding.BeginLoad();", method);
        Assert.Contains("_huBinding.EndLoad();", method);
        Assert.DoesNotContain("BuildApplyLines", method, StringComparison.Ordinal);
        Assert.DoesNotContain("TryApplyHuReservationLines", method, StringComparison.Ordinal);
        Assert.DoesNotContain("TryApplyHuReservations", method, StringComparison.Ordinal);
        Assert.DoesNotContain("TryApplyFinalHuBindings", method, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderDetailsWindow_UsesMarkingPreviewBeforeExport()
    {
        var codeBehind = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");
        var markingApi = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfMarkingApiService.cs");

        Assert.Contains("TryPreviewOrderAsync", codeBehind);
        Assert.Contains("preview.LineCount == 0 && preview.TotalQty <= 0", codeBehind);
        Assert.Contains("OrderMarkingExportPreviewWindow", codeBehind);
        Assert.Contains("/api/orders/{orderId}/marking/preview", markingApi);
        Assert.Contains("TryExportOrderAsync", codeBehind);
    }

    [Fact]
    public void OrderDetailsWindow_ReloadsAndNotifiesAfterSuccessfulMarkingExport()
    {
        var codeBehind = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");
        var mainWindow = ReadRepoFile("apps", "windows", "FlowStock.App", "MainWindow.xaml.cs");

        var exportStart = codeBehind.IndexOf("private async void ExportMarking_Click", StringComparison.Ordinal);
        Assert.True(exportStart >= 0);
        var exportEnd = codeBehind.IndexOf("private async void PlanPallets_Click", exportStart, StringComparison.Ordinal);
        Assert.True(exportEnd > exportStart);
        var exportMethod = codeBehind[exportStart..exportEnd];
        Assert.Contains("LoadOrder();", exportMethod);
        Assert.Contains("OrderStateChanged?.Invoke(this, EventArgs.Empty);", exportMethod);
        Assert.Contains("window.OrderStateChanged += (_, _) => RefreshOrdersKeepingPagedDepth();", mainWindow);
    }

    [Fact]
    public void OrderDetailsWindow_DoesNotExposeManualPalletPlanAdoption()
    {
        var xaml = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml");
        var codeBehind = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        Assert.DoesNotContain("Перенести план паллет", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Перенести план", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AdoptPalletPlanButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AdoptPalletPlan_Click", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderDetailsWindow_HuColumnHasReadableDefaultWidth()
    {
        var xaml = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml");

        Assert.Contains("Header=\"HU по строке\" Width=\"460\" MinWidth=\"420\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding HuDisplayRows}\"", xaml);
    }

    [Fact]
    public void OrderDetailsWindow_ShowsHuColumnForInternalAndCustomerOrders()
    {
        var codeBehind = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");
        var model = ReadRepoFile("apps", "windows", "FlowStock.Core", "Models", "OrderLineView.cs");

        Assert.Contains("public IReadOnlyList<OrderLineHuDisplayRow> HuDisplayRows", model);
        Assert.Contains("ProductionHuDisplayEntries", model);

        var methodStart = codeBehind.IndexOf("private void UpdateTypeUi()", StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        var methodEnd = codeBehind.IndexOf("private OrderType GetSelectedOrderType()", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);
        var method = codeBehind[methodStart..methodEnd];

        Assert.Contains("HuBoundColumn.Visibility = Visibility.Visible;", method);
        Assert.DoesNotContain("HuBoundColumn.Visibility = isCustomer ? Visibility.Visible : Visibility.Collapsed", method, StringComparison.Ordinal);
        Assert.Contains("HuAvailableColumn.Visibility = isCustomer ? Visibility.Visible : Visibility.Collapsed;", method);
        Assert.Contains("HuPickerColumn.Visibility = isCustomer ? Visibility.Visible : Visibility.Collapsed;", method);
    }

    [Fact]
    public void OrderDetailsWindow_OrderLinesGridStretchesVertically()
    {
        var xaml = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml");

        Assert.DoesNotContain("Grid.Row=\"1\" Height=\"255\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Grid Grid.Row=\"1\" VerticalAlignment=\"Stretch\" HorizontalAlignment=\"Stretch\">", xaml);

        var gridStart = xaml.IndexOf("<DataGrid x:Name=\"OrderLinesGrid\"", StringComparison.Ordinal);
        Assert.True(gridStart >= 0);
        var gridEnd = xaml.IndexOf("SelectionChanged=\"OrderLinesGrid_SelectionChanged\"", gridStart, StringComparison.Ordinal);
        Assert.True(gridEnd > gridStart);
        var gridHeader = xaml[gridStart..gridEnd];
        Assert.Contains("VerticalAlignment=\"Stretch\"", gridHeader);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", gridHeader);
        Assert.DoesNotContain("Height=", gridHeader, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        return File.ReadAllText(FindRepoFile(parts).FullName);
    }

    private static FileInfo FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return new FileInfo(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }

    private static WarehouseProductionStateRow BuildWarehouseProductionStateRow(string? reservedOrderRef) =>
        new()
        {
            ItemId = 6,
            StockQty = 600,
            FreeQty = 600,
            ReservedQty = 0,
            HuRows =
            [
                new WarehouseProductionStateHuRow
                {
                    Location = "MAIN",
                    LocationId = 1,
                    HuCode = "HU-0000661",
                    Qty = 600,
                    ReservedCustomerOrderId = reservedOrderRef == null ? null : 4,
                    ReservedCustomerOrderRef = reservedOrderRef,
                    ReservedCustomerName = reservedOrderRef == null ? null : "Клиент",
                    StockStatus = reservedOrderRef == null ? "На складе" : $"Зарезервирован: заказ {reservedOrderRef}"
                }
            ]
        };
}

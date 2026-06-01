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
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}

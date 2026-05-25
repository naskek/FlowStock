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
    public void OrderDetailsWindow_UsesHuBindingFlow()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        Assert.Contains("CustomerOrderHuBindingCoordinator", source);
        Assert.Contains("TryApplyHuReservationsAfterSave", source);
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
        Assert.Contains("MessageBox.Show", pickerMethod);

        var applyStart = orderDetails.IndexOf("private void HuPickerButton_Click", StringComparison.Ordinal);
        Assert.True(applyStart >= 0);
        var applyEnd = orderDetails.IndexOf("private void SyncHuBindingLines", applyStart, StringComparison.Ordinal);
        Assert.True(applyEnd > applyStart);
        var applyMethod = orderDetails[applyStart..applyEnd];
        Assert.Contains("try", applyMethod);
        Assert.Contains("catch (Exception ex)", applyMethod);
        Assert.Contains("LoadOrder", applyMethod);
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
    public void OrderDetailsWindow_DoesNotExposeManualPalletPlanAdoption()
    {
        var xaml = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml");
        var codeBehind = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        Assert.DoesNotContain("Перенести план паллет", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Перенести план", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AdoptPalletPlanButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AdoptPalletPlan_Click", codeBehind, StringComparison.Ordinal);
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

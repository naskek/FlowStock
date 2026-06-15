using FlowStock.App;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class WpfMixedComponentFillTests
{
    [Fact]
    public void DialogViewModel_CompletedRowsAreCheckedDisabled_AndRemainingRowsSelectable()
    {
        var pallet = new WpfProductionPalletDetail(
            10,
            20,
            30,
            40,
            null,
            50,
            "Микс",
            "HU-MIX",
            1500,
            "PRINTED",
            "PARTIALLY_FILLED",
            true,
            1,
            2,
            [
                new WpfProductionPalletComponentDetail(101, 1, "Хрен", 1200, 1200, new DateTime(2026, 6, 8, 12, 0, 0), true, "шт"),
                new WpfProductionPalletComponentDetail(102, 2, "Горчица", 300, 0, null, false, "шт")
            ],
            null);

        var viewModel = new MixedPalletComponentFillViewModel(pallet);

        var completed = viewModel.Rows.Single(row => row.ComponentLineId == 101);
        Assert.True(completed.IsSelected);
        Assert.False(completed.IsSelectable);
        Assert.Equal("наполнено", completed.StateDisplay);

        var remaining = viewModel.Rows.Single(row => row.ComponentLineId == 102);
        Assert.False(remaining.IsSelected);
        Assert.True(remaining.IsSelectable);
        Assert.Equal("ожидает", remaining.StateDisplay);
        Assert.False(viewModel.CanConfirm);

        remaining.IsSelected = true;

        Assert.True(viewModel.CanConfirm);
        Assert.Equal([102], viewModel.SelectedComponentLineIds);
    }

    [Fact]
    public void WpfMixedFillApi_UsesComponentEndpointWithIdsAndNoQuantities()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfProductionPalletApiService.cs");
        var method = SliceMethod(source, "public async Task<WpfProductionPalletMixedComponentFillApiResult> TryFillMixedPalletComponentsAsync", "    private bool TryLoadConfiguration");

        Assert.Contains("/api/tsd/production/fill-mixed-pallet-components", method, StringComparison.Ordinal);
        Assert.Contains("component_line_ids = componentLineIds", method, StringComparison.Ordinal);
        Assert.DoesNotContain("qty", method, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OperationDetailsWindow_RoutesMixedPalletThroughDialogAndKeepsSingleFillPath()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OperationDetailsWindow.xaml.cs");
        var clickMethod = SliceMethod(source, "private async void FillPalletButton_Click", "    private async Task FillMixedPalletComponentsAsync");
        var mixedMethod = SliceMethod(source, "private async Task FillMixedPalletComponentsAsync", "    private void ReselectDocLine");

        Assert.Contains("selectedPallet?.IsMixedPallet == true", clickMethod, StringComparison.Ordinal);
        Assert.Contains("await FillMixedPalletComponentsAsync(huCode, selectedLineId);", clickMethod, StringComparison.Ordinal);
        Assert.Contains("TryFillPalletAsync(", clickMethod, StringComparison.Ordinal);

        Assert.Contains("TryGetProductionPalletDocumentAsync(_doc.Id)", mixedMethod, StringComparison.Ordinal);
        Assert.Contains("pallet.EffectiveStatus", mixedMethod, StringComparison.Ordinal);
        Assert.Contains("TrySelectComponents(this, pallet, out var componentLineIds)", mixedMethod, StringComparison.Ordinal);
        Assert.Contains("TryFillMixedPalletComponentsAsync(", mixedMethod, StringComparison.Ordinal);
        Assert.Contains("result.AlreadyFilled", mixedMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("TryFillPalletAsync(", mixedMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationDetailsWindow_UsesComponentStatusForMixedPalletLineDisplay()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OperationDetailsWindow.xaml.cs");
        var loadDocLines = SliceMethod(source, "    private void LoadDocLines()", "    private void LoadOutboundHuCandidates()");
        var helper = SliceMethod(source, "    private static string ResolveMixedPalletComponentProgressLabel", "    private void LoadHuOptions()");

        Assert.Contains("ResolveMixedPalletComponentProgressLabel(component)", loadDocLines, StringComparison.Ordinal);
        Assert.Contains("component.FilledQty + StockQuantityRules.QtyTolerance >= component.PlannedQty", helper, StringComparison.Ordinal);
        Assert.Contains("\"Частично наполнено\"", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("component.IsCompleted", loadDocLines, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderLineHuDisplayRows_IncludeFilledProductionEntries()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Core", "Models", "OrderLineView.cs");
        var displayRows = SliceMethod(source, "    public IReadOnlyList<OrderLineHuDisplayRow> HuDisplayRows =>", "    public double QtyShipped");

        Assert.Contains("\"наполнено\"", displayRows, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedDialogFactory_IsolatedFromRoutingForTestableWindowCreation()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "MixedPalletComponentFillDialogFactory.cs");
        var operationDetails = ReadRepoFile("apps", "windows", "FlowStock.App", "OperationDetailsWindow.xaml.cs");

        Assert.Contains("IMixedPalletComponentFillDialogFactory", source, StringComparison.Ordinal);
        Assert.Contains("new MixedPalletComponentFillWindow(pallet)", source, StringComparison.Ordinal);
        Assert.Contains("IMixedPalletComponentFillDialogFactory _mixedPalletComponentFillDialogFactory", operationDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void WpfOrderRequests_SendProductionPalletGroup_AndServerMapsIt()
    {
        var updateService = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfUpdateOrderService.cs");
        var createService = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfCreateOrderService.cs");
        var apiModels = ReadRepoFile("apps", "windows", "FlowStock.Server", "ApiModels.cs");
        var updateEndpoint = ReadRepoFile("apps", "windows", "FlowStock.Server", "OrderUpdateEndpoint.cs");
        var createEndpoint = ReadRepoFile("apps", "windows", "FlowStock.Server", "OrderCreateEndpoint.cs");

        Assert.Contains("ProductionPalletGroup = NormalizeValue(line.ProductionPalletGroup)?.ToUpperInvariant()", updateService, StringComparison.Ordinal);
        Assert.Contains("ProductionPalletGroup = NormalizeValue(line.ProductionPalletGroup)?.ToUpperInvariant()", createService, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"production_pallet_group\")]", apiModels, StringComparison.Ordinal);
        Assert.Contains("ProductionPalletGroup = NormalizePalletGroup(line.ProductionPalletGroup)", updateEndpoint, StringComparison.Ordinal);
        Assert.Contains("ProductionPalletGroup = NormalizePalletGroup(line.ProductionPalletGroup)", createEndpoint, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(current, string.Concat(Enumerable.Repeat("..\\", i)), Path.Combine(parts)));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("Не удалось найти файл в репозитории.", Path.Combine(parts));
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Не найден метод: {startMarker}");

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Не найдена граница метода: {endMarker}");

        return source[start..end];
    }
}

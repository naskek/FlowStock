using FlowStock.App;
using FlowStock.Core.Models;

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
    public void WpfProductionPalletApi_MapsStorageConditionsFromPrintRows()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfProductionPalletApiService.cs");
        var mapper = SliceMethod(source, "    private static PalletLabelPrintRow MapPrintRow(PrintRowResponse row)", "    private static string? NormalizeBaseUrl");

        Assert.Contains("[JsonPropertyName(\"storage_conditions\")]", source, StringComparison.Ordinal);
        Assert.Contains("StorageConditions = row.StorageConditions ?? string.Empty", mapper, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationDetailsWindow_UsesWholePalletEndpointForSingleAndMixedHu()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "OperationDetailsWindow.xaml.cs");
        var clickMethod = SliceMethod(source, "private async void FillPalletButton_Click", "    private void ReselectDocLine");

        Assert.Contains("TryFillPalletAsync(", clickMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("IsMixedPallet", clickMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("FillMixedPalletComponentsAsync", clickMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("TrySelectComponents", source, StringComparison.Ordinal);
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
    public void OrderLineHuDisplayRows_PreferCanonicalPresentation()
    {
        var line = new OrderLineView
        {
            ProductionHuDisplayEntries =
            [
                new OrderLineHuDisplayEntry("HU-LEGACY-PRODUCTION", "план", 10, false, 1)
            ],
            HuFateDisplayEntries =
            [
                new OrderLineHuDisplayEntry(
                    "HU-LEGACY-FATE",
                    "отгружено",
                    5,
                    false,
                    2,
                    FateCode: "SHIPPED",
                    FateLabel: "отгружено")
            ]
        };

        Assert.Empty(line.OperatorHuDisplayRows);
        Assert.NotEmpty(line.HuDisplayRows); // Legacy compatibility API remains available.

        line.HuPresentation = new OrderLineHuPresentation
        {
            OperationalHus =
            [
                new OperationalHuPresentation
                {
                    HuCode = "HU-CANONICAL-OPERATIONAL",
                    Qty = 5,
                    State = new HuSemanticStatePresentation(
                        OperationalHuSemanticCode.Inconsistent,
                        "Серверное несогласованное состояние")
                }
            ],
            ProductionTasks =
            [
                new ProductionTaskPresentation
                {
                    HuCode = "HU-CANONICAL-PRODUCTION",
                    Qty = 10,
                    State = new HuSemanticStatePresentation(
                        ProductionTaskSemanticCode.AwaitingFill,
                        "Серверное ожидание наполнения")
                }
            ]
        };

        var operatorRows = line.OperatorHuDisplayRows;
        Assert.Collection(
            operatorRows,
            row =>
            {
                Assert.Equal("HU-CANONICAL-OPERATIONAL", row.HuCode);
                Assert.Equal(OperationalHuSemanticCode.Inconsistent, row.StateCode);
                Assert.Equal("Серверное несогласованное состояние", row.Label);
            },
            row =>
            {
                Assert.Equal("HU-CANONICAL-PRODUCTION", row.HuCode);
                Assert.Equal(ProductionTaskSemanticCode.AwaitingFill, row.StateCode);
                Assert.Equal("Серверное ожидание наполнения", row.Label);
            });
        Assert.Equal(operatorRows, line.HuDisplayRows);
        Assert.DoesNotContain(operatorRows, row => row.HuCode.StartsWith("HU-LEGACY-", StringComparison.Ordinal));
    }

    [Fact]
    public void MixedDialogFactory_RemainsLegacyButHasNoNormalRuntimeConsumer()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.App", "MixedPalletComponentFillDialogFactory.cs");
        var operationDetails = ReadRepoFile("apps", "windows", "FlowStock.App", "OperationDetailsWindow.xaml.cs");

        Assert.Contains("IMixedPalletComponentFillDialogFactory", source, StringComparison.Ordinal);
        Assert.Contains("new MixedPalletComponentFillWindow(pallet)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IMixedPalletComponentFillDialogFactory", operationDetails, StringComparison.Ordinal);
        Assert.DoesNotContain("MixedPalletComponentFillWindow", operationDetails, StringComparison.Ordinal);
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

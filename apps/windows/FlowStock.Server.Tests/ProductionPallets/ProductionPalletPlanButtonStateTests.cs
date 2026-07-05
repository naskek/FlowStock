using FlowStock.App;

namespace FlowStock.Server.Tests.ProductionPallets;

/// <summary>
/// Four states of the order-card pallet plan button. All enabled states open the same
/// constructor window; the legacy /plan endpoint is not called from this button.
/// </summary>
public sealed class ProductionPalletPlanButtonStateTests
{
    [Fact]
    public void NoSavedPallets_WithShortfall_IsCreate()
    {
        var state = ProductionPalletPlanButtonState.Resolve(hasSavedPallets: false, productionRequired: true);

        Assert.Equal("Создать план паллет", state.Label);
        Assert.True(state.IsEnabled);
    }

    [Fact]
    public void SavedPallets_WithShortfall_IsAppend()
    {
        var state = ProductionPalletPlanButtonState.Resolve(hasSavedPallets: true, productionRequired: true);

        Assert.Equal("Дополнить план паллет", state.Label);
        Assert.True(state.IsEnabled);
    }

    [Fact]
    public void SavedPallets_WithoutShortfall_IsOpen()
    {
        var state = ProductionPalletPlanButtonState.Resolve(hasSavedPallets: true, productionRequired: false);

        Assert.Equal("Открыть план паллет", state.Label);
        Assert.True(state.IsEnabled);
    }

    [Fact]
    public void NoSavedPallets_WithoutShortfall_IsDisabled()
    {
        var state = ProductionPalletPlanButtonState.Resolve(hasSavedPallets: false, productionRequired: false);

        Assert.False(state.IsEnabled);
    }

    [Fact]
    public void PlanPalletsButton_OpensBuilderWindow_AndDoesNotCallLegacyPlanEndpoint()
    {
        var source = TestSources.ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");
        var start = source.IndexOf("private async void PlanPallets_Click", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("private ", start + 30, StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains("ProductionPalletBuilderWindow", method, StringComparison.Ordinal);
        Assert.DoesNotContain("TryPlanOrderAsync", method, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderGrid_HasNoMixedGroupColumns_AndNoGroupWritePath_ButRoundTripsValue()
    {
        var xaml = TestSources.ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml");
        Assert.DoesNotContain("Общий HU", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Группа HU", xaml, StringComparison.Ordinal);

        var codeBehind = TestSources.ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");
        Assert.DoesNotContain("line.ProductionPalletGroup =", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("MixedPalletCheckBox", codeBehind, StringComparison.Ordinal);

        // Inert legacy value is still round-tripped by order save, never invented by the UI.
        var updateService = TestSources.ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfUpdateOrderService.cs");
        Assert.Contains("ProductionPalletGroup", updateService, StringComparison.Ordinal);
    }
}

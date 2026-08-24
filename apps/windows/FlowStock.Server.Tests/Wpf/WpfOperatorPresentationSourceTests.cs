using FlowStock.Core.Models;

namespace FlowStock.Server.Tests.Wpf;

public sealed class WpfOperatorPresentationSourceTests
{
    [Fact]
    public void OrderViews_UseCanonicalCodesForToneAndServerLabelsForText()
    {
        var mainXaml = ReadRepoFile("apps", "windows", "FlowStock.App", "MainWindow.xaml");
        var detailsXaml = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml");
        var detailsCode = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");

        Assert.Contains("Binding=\"{Binding StatusPresentationCode}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding OperatorStatusLabel}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding OperatorStatusLabel}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding Tag, ElementName=OrderStatusBadge}\"", detailsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DataTrigger Binding=\"{Binding StatusDisplay}\" Value=\"Готов\"", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Binding=\"{Binding Text, ElementName=OrderStatusText}\"", detailsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("_order.StatusDisplay", detailsCode, StringComparison.Ordinal);
        Assert.Contains("SetOrderStatusPresentation(_order.OperatorStatusPresentation)", detailsCode, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderPresentation_UsesCanonicalLabelWithoutChangingLegacyCompatibilityDisplay()
    {
        var accepted = new Order
        {
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            OperatorStatusPresentation = new OrderOperatorStatusPresentation("ACCEPTED", "Готов к отгрузке")
        };
        var partial = new Order
        {
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            IsPartiallyShipped = true,
            OperatorStatusPresentation = new OrderOperatorStatusPresentation("PARTIALLY_SHIPPED", "Частично отгружен")
        };

        Assert.Equal("Готов", accepted.StatusDisplay);
        Assert.Equal("Готов к отгрузке", accepted.OperatorStatusLabel);
        Assert.Equal("Частично отгружено", partial.StatusDisplay);
        Assert.Equal("Частично отгружен", partial.OperatorStatusLabel);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("ACCEPTED", "")]
    [InlineData("", "Готов к отгрузке")]
    public void OrderPresentation_MissingOrIncompleteCanonicalData_IsNeutral(string? code, string? label)
    {
        var order = new Order
        {
            Type = OrderType.Customer,
            Status = OrderStatus.Accepted,
            OperatorStatusPresentation = code == null && label == null
                ? null
                : new OrderOperatorStatusPresentation(code ?? string.Empty, label ?? string.Empty)
        };

        Assert.Equal("Готов", order.StatusDisplay);
        Assert.Equal("UNKNOWN", order.StatusPresentationCode);
        Assert.Equal("Неизвестно", order.OperatorStatusLabel);
    }

    [Fact]
    public void ReadApiAndWarehouseView_MapCanonicalHuAndOrderPresentation()
    {
        var readApi = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfReadApiService.cs");
        var mainCode = ReadRepoFile("apps", "windows", "FlowStock.App", "MainWindow.xaml.cs");
        var details = SliceMethod(mainCode, "        private void LoadDetailRows", "        private static string BuildSummaryFingerprint");

        Assert.Contains("MapOrderOperatorStatusPresentation(element)", readApi, StringComparison.Ordinal);
        Assert.Contains("MapOrderLineHuPresentation(element)", readApi, StringComparison.Ordinal);
        Assert.Contains("MapGlobalHuOperatorPresentation(element)", readApi, StringComparison.Ordinal);
        Assert.Contains("ResolveCanonicalOperatorState(hu.OperatorPresentation)", details, StringComparison.Ordinal);
        Assert.Contains("ResolveCanonicalOperatorState(prd.OperatorPresentation)", details, StringComparison.Ordinal);
        Assert.Contains("presentation.OperationalHu?.State ?? presentation.ProductionTask?.State", details, StringComparison.Ordinal);
        Assert.Contains("order.OrderStatusPresentation.Label", details, StringComparison.Ordinal);
        Assert.DoesNotContain("hu.StockStatus", details, StringComparison.Ordinal);
        Assert.DoesNotContain("prd.PalletStatusDisplay", details, StringComparison.Ordinal);
        Assert.Contains("UnknownOperatorStateLabel", details, StringComparison.Ordinal);
        Assert.DoesNotContain("TranslatePalletStatus", details, StringComparison.Ordinal);
        Assert.DoesNotContain(".Append(':').Append(hu.StockStatus", mainCode, StringComparison.Ordinal);
        Assert.Contains(".Append(':').Append(hu.OperatorPresentation.OperationalHu?.State.Code", mainCode, StringComparison.Ordinal);
        Assert.Contains(".Append(':').Append(prd.OperatorPresentation.OperationalHu?.State.Code", mainCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyMarkingWindow_IsDeletedWhileOrderCardRemainsCanonical()
    {
        var server = ReadRepoFile("apps", "windows", "FlowStock.Server", "Program.cs");
        var client = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfMarkingApiService.cs");
        var orderCard = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml");
        var root = FindRepoRoot();

        Assert.Contains("order_status_presentation = OrderOperatorStatusResolver.Resolve", server, StringComparison.Ordinal);
        Assert.Contains("OperatorStatusPresentation = MapOrderStatusPresentation(element)", client, StringComparison.Ordinal);
        Assert.Contains("ImportMarkingButton", orderCard, StringComparison.Ordinal);
        Assert.Contains("marking/import/preview", client, StringComparison.Ordinal);
        Assert.Contains("marking/import/confirm", client, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "apps", "windows", "FlowStock.App", "MarkingWindow.xaml")));
        Assert.False(File.Exists(Path.Combine(root, "apps", "windows", "FlowStock.App", "MarkingWindow.xaml.cs")));
        Assert.False(File.Exists(Path.Combine(root, "apps", "windows", "FlowStock.App", "KmImportWindow.xaml")));
        Assert.DoesNotContain("Маркировка (КМ)", ReadRepoFile("apps", "windows", "FlowStock.App", "MainWindow.xaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("KmUiEnabled", ReadRepoFile("apps", "windows", "FlowStock.App", "OperationDetailsWindow.xaml.cs"), StringComparison.Ordinal);
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Не найден метод: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Не найдена граница: {endMarker}");
        return source[start..end];
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(
                current,
                string.Concat(Enumerable.Repeat("..\\", i)),
                Path.Combine(parts)));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("Не удалось найти файл в репозитории.", Path.Combine(parts));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

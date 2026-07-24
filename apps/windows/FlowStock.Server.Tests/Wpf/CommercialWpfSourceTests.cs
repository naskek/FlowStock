namespace FlowStock.Server.Tests.Wpf;

public sealed class CommercialWpfSourceTests
{
    private static readonly string OrderWindow = ReadRepoFile(
        "apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml.cs");
    private static readonly string QuantityDialog = ReadRepoFile(
        "apps", "windows", "FlowStock.App", "QuantityUomDialog.xaml.cs");
    private static readonly string ItemWindow = ReadRepoFile(
        "apps", "windows", "FlowStock.App", "ItemEditWindow.xaml.cs");
    private static readonly string MainWindow = ReadRepoFile(
        "apps", "windows", "FlowStock.App", "MainWindow.xaml");

    [Fact]
    public void Customer_order_uses_preview_but_only_checkbox_creates_manual_price_intent()
    {
        Assert.Contains("GetPreviewAsync", OrderWindow, StringComparison.Ordinal);
        Assert.Contains("qtyDialog.ChangeUnitPriceGross", OrderWindow, StringComparison.Ordinal);
        Assert.Contains("ManualPriceOverrideCheck.IsChecked == true", QuantityDialog, StringComparison.Ordinal);
        Assert.Contains("Автоматическая цена не задана. Укажите цену вручную.", QuantityDialog, StringComparison.Ordinal);
    }

    [Fact]
    public void Vat_is_read_only_and_customer_partner_is_required_before_line_selection()
    {
        var orderXaml = ReadRepoFile("apps", "windows", "FlowStock.App", "OrderDetailsWindow.xaml");
        Assert.Contains("x:Name=\"VatRateColumn\"", orderXaml, StringComparison.Ordinal);
        Assert.Contains("IsReadOnly=\"True\"", orderXaml, StringComparison.Ordinal);
        Assert.Contains("Сначала выберите контрагента клиентского заказа.", OrderWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("change_vat_rate", OrderWindow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Item_and_main_windows_expose_customer_prices_and_statistics()
    {
        Assert.Contains("PartnerItemSalePriceWindow", ItemWindow, StringComparison.Ordinal);
        Assert.Contains("Цены клиентов...", MainWindow, StringComparison.Ordinal);
        Assert.Contains("<TabItem Header=\"Статистика\">", MainWindow, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}

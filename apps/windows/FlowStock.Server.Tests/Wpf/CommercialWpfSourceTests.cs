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
    private static readonly string PartnerItemSalePriceWindow = ReadRepoFile(
        "apps", "windows", "FlowStock.App", "PartnerItemSalePriceWindow.xaml.cs");

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

    [Fact]
    public void Order_item_picker_filters_inactive_items_without_filtering_shared_read_api()
    {
        Assert.Contains("apiItems.Where(item => item.IsActive)", OrderWindow, StringComparison.Ordinal);
        var readApi = ReadRepoFile("apps", "windows", "FlowStock.App", "Services", "WpfReadApiService.cs");
        Assert.DoesNotContain("Where(item => item.IsActive)", readApi, StringComparison.Ordinal);
    }

    [Fact]
    public void Customer_price_partner_picker_is_editable_and_filters_an_isolated_option_collection()
    {
        var xaml = ReadRepoFile("apps", "windows", "FlowStock.App", "PartnerItemSalePriceWindow.xaml");

        Assert.Contains("x:Name=\"PartnerCombo\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEditable=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsTextSearchEnabled=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("StaysOpenOnEdit=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PartnerCombo.ItemsSource = _partnerAutocompleteOptions", PartnerItemSalePriceWindow, StringComparison.Ordinal);
        Assert.Contains("FilterPartnerCombo.ItemsSource = _partners", PartnerItemSalePriceWindow, StringComparison.Ordinal);
        Assert.Contains("partner.DisplayName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)", PartnerItemSalePriceWindow, StringComparison.Ordinal);
        Assert.Contains("TextBoxBase.TextChangedEvent", PartnerItemSalePriceWindow, StringComparison.Ordinal);
        Assert.Contains("PartnerCombo.IsDropDownOpen = _partnerAutocompleteOptions.Count > 0", PartnerItemSalePriceWindow, StringComparison.Ordinal);
        Assert.Contains("PartnerCombo.SelectedItem is not Partner partner", PartnerItemSalePriceWindow, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("РОМАШ", true)]
    [InlineData("123456", true)]
    [InlineData("маш", true)]
    [InlineData("другой", false)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    public void Customer_price_partner_picker_matches_full_display_name_by_substring(
        string query,
        bool expected)
    {
        var partner = new FlowStock.Core.Models.Partner
        {
            Name = "Ромашка",
            Code = "1234567890"
        };

        Assert.Equal(expected, FlowStock.App.PartnerItemSalePriceWindow.PartnerMatchesAutocomplete(partner, query));
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

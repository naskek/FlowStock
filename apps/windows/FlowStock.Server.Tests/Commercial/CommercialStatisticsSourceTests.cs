namespace FlowStock.Server.Tests.Commercial;

public sealed class CommercialStatisticsSourceTests
{
    private static readonly string StoreSource = File.ReadAllText(FindRepoFile(
        "apps",
        "windows",
        "FlowStock.Data",
        "PostgresDataStore.cs"));

    [Theory]
    [InlineData("PARTNER")]
    [InlineData("ITEM")]
    [InlineData("GTIN")]
    [InlineData("BRAND")]
    [InlineData("VOLUME")]
    public void Statistics_supports_all_required_groupings(string grouping)
    {
        Assert.Contains($"CommercialStatisticsGroupBy.{ToPascalCase(grouping)}", StoreSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Orders_and_sales_use_distinct_partner_and_date_sources()
    {
        Assert.Contains("o.partner_id", StoreSource, StringComparison.Ordinal);
        Assert.Contains("o.created_at", StoreSource, StringComparison.Ordinal);
        Assert.Contains("d.partner_id", StoreSource, StringComparison.Ordinal);
        Assert.Contains("d.closed_at", StoreSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Financial_sql_uses_order_line_snapshots_and_canonical_active_doc_lines()
    {
        Assert.Contains("ol.unit_price_gross", StoreSource, StringComparison.Ordinal);
        Assert.Contains("ol.vat_rate", StoreSource, StringComparison.Ordinal);
        Assert.Contains("newer.replaces_line_id = dl.id", StoreSource, StringComparison.Ordinal);
        Assert.DoesNotContain("items.default_sale_price_gross", StoreSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("partner_item_sale_prices.unit_price_gross", StoreSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vat_rates.rate", StoreSource, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToPascalCase(string value) =>
        value[..1] + value[1..].ToLowerInvariant();

    private static string FindRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}

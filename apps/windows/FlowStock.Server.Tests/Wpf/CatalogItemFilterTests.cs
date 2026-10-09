using System.Collections.ObjectModel;
using FlowStock.App.Services;
using FlowStock.Core.Models;

namespace FlowStock.Server.Tests.Wpf;

public sealed class CatalogItemFilterTests
{
    [Fact]
    public void AllValuesSelected_DoesNotLimitRows()
    {
        var options = Options(("A", "A", true), ("B", "B", true));

        Assert.True(CatalogItemFilter.MatchesGroup("A", options));
        Assert.True(CatalogItemFilter.MatchesGroup("B", options));
    }

    [Fact]
    public void NoValuesSelected_HidesRows()
    {
        var options = Options(("A", "A", false), ("B", "B", false));

        Assert.False(CatalogItemFilter.MatchesGroup("A", options));
        Assert.False(CatalogItemFilter.MatchesGroup("B", options));
    }

    [Fact]
    public void ThreeGroups_CombineWithAnd()
    {
        var brand = Options(("Acme", "Acme", true), ("Other", "Other", false));
        var volume = Options(("1L", "1L", true), ("2L", "2L", false));
        var uom = Options(("шт", "шт", true), ("кг", "кг", false));

        Assert.True(
            CatalogItemFilter.MatchesGroup("Acme", brand)
            && CatalogItemFilter.MatchesGroup("1L", volume)
            && CatalogItemFilter.MatchesGroup("шт", uom));
        Assert.False(
            CatalogItemFilter.MatchesGroup("Acme", brand)
            && CatalogItemFilter.MatchesGroup("2L", volume)
            && CatalogItemFilter.MatchesGroup("шт", uom));
    }

    [Fact]
    public void EmptyValue_MatchesEmptyOption()
    {
        var options = Options((CatalogItemFilter.EmptyLabel, null, true), ("A", "A", false));

        Assert.True(CatalogItemFilter.MatchesGroup("   ", options));
        Assert.True(CatalogItemFilter.MatchesGroup(null, options));
        Assert.False(CatalogItemFilter.MatchesGroup("A", options));
    }

    [Fact]
    public void FilterValues_AreCaseInsensitive()
    {
        var options = Options(("Acme", "Acme", true), ("Other", "Other", false));

        Assert.True(CatalogItemFilter.MatchesGroup("acme", options));
        Assert.False(CatalogItemFilter.MatchesGroup("other", options));
    }

    [Fact]
    public void SearchAndFilters_WorkTogether()
    {
        var item = new Item
        {
            Name = "Томатная паста",
            Barcode = "SKU-100",
            Gtin = "04601234567890",
            Brand = "Acme",
            Volume = "1L",
            BaseUom = "шт"
        };
        var brand = Options(("Acme", "Acme", true), ("Other", "Other", false));
        var volume = Options(("1L", "1L", true));
        var uom = Options(("шт", "шт", true));

        Assert.True(
            CatalogItemFilter.MatchesGroup(item.Brand, brand)
            && CatalogItemFilter.MatchesGroup(item.Volume, volume)
            && CatalogItemFilter.MatchesGroup(item.BaseUom, uom)
            && CatalogItemFilter.MatchesSearch(item, "sku-100"));
        Assert.False(
            CatalogItemFilter.MatchesGroup("Other", brand)
            && CatalogItemFilter.MatchesSearch(item, "sku-100"));
    }

    [Theory]
    [InlineData(true, CatalogItemActivityFilter.All, true)]
    [InlineData(false, CatalogItemActivityFilter.All, true)]
    [InlineData(true, CatalogItemActivityFilter.Active, true)]
    [InlineData(false, CatalogItemActivityFilter.Active, false)]
    [InlineData(true, CatalogItemActivityFilter.Inactive, false)]
    [InlineData(false, CatalogItemActivityFilter.Inactive, true)]
    public void ActivityFilter_RespectsActiveFlagAndAllDefault(
        bool isActive, CatalogItemActivityFilter mode, bool expected)
    {
        Assert.Equal(expected, CatalogItemFilter.MatchesActivity(new Item { IsActive = isActive }, mode));
    }

    [Fact]
    public void ActivityAndExistingFilters_CombineWithAnd()
    {
        var item = new Item
        {
            Name = "Хрен столовый",
            Barcode = "SKU-100",
            Gtin = "04601234567890",
            Brand = "Acme",
            Volume = "200 г",
            BaseUom = "шт",
            IsActive = false
        };
        var brand = Options(("Acme", "Acme", true), ("Other", "Other", false));
        var volume = Options(("200 г", "200 г", true));
        var uom = Options(("шт", "шт", true));

        Assert.True(CatalogItemFilter.Matches(item, brand, volume, uom, "sku-100", CatalogItemActivityFilter.All));
        Assert.True(CatalogItemFilter.Matches(item, brand, volume, uom, "хрен", CatalogItemActivityFilter.Inactive));
        Assert.False(CatalogItemFilter.Matches(item, brand, volume, uom, "хрен", CatalogItemActivityFilter.Active));
        Assert.False(CatalogItemFilter.Matches(item, brand, volume, uom, "unknown", CatalogItemActivityFilter.Inactive));
        Assert.False(CatalogItemFilter.Matches(item, Options(("Acme", "Acme", false)), volume, uom, "хрен", CatalogItemActivityFilter.Inactive));
        Assert.False(CatalogItemFilter.Matches(item, brand, Options(("200 г", "200 г", false)), uom, "хрен", CatalogItemActivityFilter.Inactive));
        Assert.False(CatalogItemFilter.Matches(item, brand, volume, Options(("шт", "шт", false)), "хрен", CatalogItemActivityFilter.Inactive));
    }

    [Fact]
    public void ActivityFilter_EmptyResultAndResetDoNotMutateItems()
    {
        var items = new[] { new Item { Id = 1, IsActive = false }, new Item { Id = 2, IsActive = false } };

        Assert.Empty(items.Where(item => CatalogItemFilter.MatchesActivity(item, CatalogItemActivityFilter.Active)));
        Assert.Equal(2, items.Count(item => CatalogItemFilter.MatchesActivity(item, CatalogItemActivityFilter.All)));
        Assert.All(items, item => Assert.False(item.IsActive));
    }

    [Theory]
    [InlineData(true, "Активна")]
    [InlineData(false, "Неактивна")]
    public void ActivityStatusConverter_HasExplicitReadableText(bool active, string expected)
    {
        var converter = new CatalogItemActivityStatusConverter();

        Assert.Equal(expected, converter.Convert(active, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static ObservableCollection<CatalogItemFilterOption> Options(params (string Label, string? Value, bool IsChecked)[] values)
    {
        var options = new ObservableCollection<CatalogItemFilterOption>();
        foreach (var value in values)
        {
            options.Add(new CatalogItemFilterOption(value.Label, value.Value) { IsChecked = value.IsChecked });
        }

        return options;
    }
}

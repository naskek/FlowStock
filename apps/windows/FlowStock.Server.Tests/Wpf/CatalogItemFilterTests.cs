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

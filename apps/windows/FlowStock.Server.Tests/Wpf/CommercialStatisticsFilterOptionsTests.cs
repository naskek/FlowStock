using FlowStock.App;
using FlowStock.Core.Models;

namespace FlowStock.Server.Tests.Wpf;

public sealed class CommercialStatisticsFilterOptionsTests
{
    [Fact]
    public void Partner_search_matches_start_of_name_word()
    {
        var options = CommercialStatisticsFilterOptions.BuildPartners(
        [
            new Partner { Id = 1, Code = "CL-01", Name = "Иван Печагин" },
            new Partner { Id = 2, Code = "CL-02", Name = "Мария Соколова" }
        ]);

        var matches = CommercialStatisticsFilterOptions.SearchEntities(options, "  ПЕ  ");

        var match = Assert.Single(matches);
        Assert.Equal(1, match.Id);
    }

    [Theory]
    [InlineData("CL-02", 2)]
    [InlineData("иван", 1)]
    public void Partner_search_matches_code_and_name_prefix(string query, long expectedId)
    {
        var options = CommercialStatisticsFilterOptions.BuildPartners(
        [
            new Partner { Id = 1, Code = "CL-01", Name = "Иван Печагин" },
            new Partner { Id = 2, Code = "CL-02", Name = "Мария Соколова" }
        ]);

        var match = Assert.Single(
            CommercialStatisticsFilterOptions.SearchEntities(options, query));

        Assert.Equal(expectedId, match.Id);
    }

    [Theory]
    [InlineData("ябл", 10)]
    [InlineData("  сок   яб  ", 10)]
    [InlineData("0460123", 10)]
    [InlineData("sku-20", 20)]
    public void Item_search_matches_name_gtin_and_barcode(string query, long expectedId)
    {
        var options = CommercialStatisticsFilterOptions.BuildItems(
        [
            new Item { Id = 10, Name = "Сок яблочный", Gtin = "04601234567890" },
            new Item { Id = 20, Name = "Вода", Barcode = "SKU-20" }
        ]);

        var match = Assert.Single(
            CommercialStatisticsFilterOptions.SearchEntities(options, query));

        Assert.Equal(expectedId, match.Id);
    }

    [Fact]
    public void Empty_entity_search_keeps_full_list_with_all_first()
    {
        var options = CommercialStatisticsFilterOptions.BuildPartners(
        [
            new Partner { Id = 1, Code = "CL-01", Name = "Иван Печагин" }
        ]);

        var matches = CommercialStatisticsFilterOptions.SearchEntities(options, "   ");

        Assert.Equal(options, matches);
        Assert.Null(matches[0].Id);
    }

    [Theory]
    [InlineData("бе", "Белый бренд")]
    [InlineData("КО", "Короб 12 шт")]
    public void Text_search_matches_value_word_prefix_without_case(
        string query,
        string expectedValue)
    {
        var options = new[]
        {
            new CommercialStatisticsTextFilterOption(null, "Все"),
            new CommercialStatisticsTextFilterOption("Белый бренд", "Белый бренд"),
            new CommercialStatisticsTextFilterOption("Короб 12 шт", "Короб 12 шт")
        };

        var match = Assert.Single(
            CommercialStatisticsFilterOptions.SearchText(options, query));

        Assert.Equal(expectedValue, match.Value);
    }

    [Fact]
    public void Restore_selection_keeps_previous_option_or_falls_back_to_all()
    {
        var options = CommercialStatisticsFilterOptions.BuildPartners(
        [
            new Partner { Id = 1, Code = "CL-01", Name = "Иван Печагин" }
        ]);

        Assert.Equal(
            1,
            CommercialStatisticsFilterOptions.RestoreEntitySelection(options, 1).Id);
        Assert.Null(
            CommercialStatisticsFilterOptions.RestoreEntitySelection(options, 999).Id);

        var textOptions = new[]
        {
            new CommercialStatisticsTextFilterOption(null, "Все бренды"),
            new CommercialStatisticsTextFilterOption("Бренд", "Бренд")
        };
        Assert.Equal(
            "Бренд",
            CommercialStatisticsFilterOptions.RestoreTextSelection(textOptions, "бренд").Value);
        Assert.Null(
            CommercialStatisticsFilterOptions.RestoreTextSelection(textOptions, "нет").Value);
    }

    [Fact]
    public void Catalog_options_put_all_first_and_map_selected_entities_to_ids()
    {
        var partners = CommercialStatisticsFilterOptions.BuildPartners(
        [
            new Partner { Id = 2, Code = "B", Name = "Бета" },
            new Partner { Id = 1, Code = "A", Name = "Альфа" }
        ]);
        var items = CommercialStatisticsFilterOptions.BuildItems(
        [
            new Item
            {
                Id = 20,
                Name = "Сок",
                Gtin = "04600000000002",
                Barcode = "SKU-20"
            },
            new Item { Id = 10, Name = "Вода", Barcode = "SKU-10" }
        ]);

        Assert.Null(partners[0].Id);
        Assert.Equal("Все контрагенты", partners[0].Label);
        Assert.Equal([1L, 2L], partners.Skip(1).Select(option => option.Id!.Value));
        Assert.Equal(
            2,
            CommercialStatisticsFilterOptions.SelectedId(partners[2], "B - Бета"));
        Assert.Null(CommercialStatisticsFilterOptions.SelectedId(partners[2], "произвольный текст"));

        Assert.Null(items[0].Id);
        Assert.Equal("Все товары", items[0].Label);
        Assert.Contains("штрихкод SKU-10", items[1].Label);
        Assert.Contains("GTIN 04600000000002", items[2].Label);
        Assert.Contains("штрихкод SKU-20", items[2].Label);
        Assert.Equal(
            10,
            CommercialStatisticsFilterOptions.SelectedId(items[1], items[1].Label));
    }

    [Fact]
    public void Text_options_remove_blanks_deduplicate_and_sort()
    {
        Item[] source =
        [
            new Item { Gtin = " 0460 0002 ", Brand = " Бета ", Volume = "1 л" },
            new Item { Gtin = "04600002", Brand = "бета", Volume = " 1 Л " },
            new Item { Gtin = "04600001", Brand = "Альфа", Volume = "0,5 л" },
            new Item { Gtin = " ", Brand = null, Volume = string.Empty }
        ];

        var gtins = CommercialStatisticsFilterOptions.BuildGtins(source);
        var brands = CommercialStatisticsFilterOptions.BuildBrands(source);
        var volumes = CommercialStatisticsFilterOptions.BuildVolumes(source);

        Assert.Equal(
            [null, "04600001", "04600002"],
            gtins.Select(option => option.Value));
        Assert.Equal(
            [null, "Альфа", "Бета"],
            brands.Select(option => option.Value));
        Assert.Equal(
            [null, "0,5 л", "1 л"],
            volumes.Select(option => option.Value));
        Assert.Equal("Все GTIN", gtins[0].Label);
        Assert.Equal("Все бренды", brands[0].Label);
        Assert.Equal("Все фасовки", volumes[0].Label);
        Assert.Equal(
            "04600001",
            CommercialStatisticsFilterOptions.SelectedValue(gtins[1], gtins[1].Label));
        Assert.Null(
            CommercialStatisticsFilterOptions.SelectedValue(gtins[1], "произвольный текст"));
    }

    [Fact]
    public void Supported_statuses_use_canonical_csv_and_sales_omits_filter()
    {
        var statuses = CommercialStatisticsFilterOptions.BuildStatuses();

        Assert.Null(CommercialStatisticsFilterOptions.BuildStatusesCsv("orders", statuses));
        Assert.Equal("Все статусы", CommercialStatisticsFilterOptions.BuildStatusesLabel(statuses));

        statuses.Single(option => option.Code == "DRAFT").IsChecked = false;
        statuses.Single(option => option.Code == "SHIPPED").IsChecked = false;

        Assert.Equal(
            "ACCEPTED,IN_PROGRESS",
            CommercialStatisticsFilterOptions.BuildStatusesCsv("orders", statuses));
        Assert.Equal(
            "Готов, В работе",
            CommercialStatisticsFilterOptions.BuildStatusesLabel(statuses));
        Assert.Null(CommercialStatisticsFilterOptions.BuildStatusesCsv("sales", statuses));
        Assert.DoesNotContain(statuses, option => option.Code is "CANCELLED" or "MERGED");

        Assert.Equal(
            "ACCEPTED,IN_PROGRESS",
            CommercialStatisticsFilterOptions.BuildStatusesCsv("orders", statuses));
    }
}

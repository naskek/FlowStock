using FlowStock.Core.Services;
using FlowStock.Data;
using Npgsql;

namespace FlowStock.Server.Tests.Commercial;

public sealed class CommercialPriceListPostgresTests
{
    [Fact]
    public void Price_list_resolves_base_override_zero_inactive_and_missing_without_writing()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var id = Guid.NewGuid().ToString("N");
        var store = new PostgresDataStore(connectionString);
        var catalog = new CatalogService(store);
        var prices = new PartnerItemSalePriceService(store);
        var partnerA = catalog.CreatePartner($"Прайс клиент А {id}", null, "CLIENT");
        var partnerB = catalog.CreatePartner($"Прайс клиент Б {id}", null, "CLIENT");
        var baseItem = catalog.CreateItem(
            $"Прайс товар А {id}", $"PL-ITEM-A-{id}", null, "шт",
            null, null, null, null, false);
        var missingItem = catalog.CreateItem(
            $"Прайс товар Б {id}", $"PL-ITEM-B-{id}", null, "шт",
            null, null, null, null, false);

        using (var conn = new NpgsqlConnection(connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
UPDATE items
   SET default_sale_price_gross = 150.00, brand = @brand, volume = '200 г'
 WHERE id = @item_id;
""";
            cmd.Parameters.AddWithValue("brand", $"Brand-{id}");
            cmd.Parameters.AddWithValue("item_id", baseItem);
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }

        prices.Create(partnerA, baseItem, 0m, isActive: true);
        prices.Create(partnerB, baseItem, 77m, isActive: false);
        var reader = new CommercialPriceListReader(connectionString);

        // Product view: baseline + only genuinely stored active assignments.
        var product = reader.Get(new CommercialPriceListQuery("item", ItemId: baseItem));
        Assert.Equal(2, product.TotalCount);
        var baseline = Assert.Single(product.Items, r => r.RowKind == "BASE");
        Assert.Equal(150m, baseline.DefaultPriceGross);
        Assert.Equal("ITEM_DEFAULT", baseline.PriceSource);
        var zero = Assert.Single(product.Items, r => r.PartnerId == partnerA);
        Assert.Equal(0m, zero.PartnerPriceGross);
        Assert.Equal(0m, zero.EffectivePriceGross);
        Assert.Equal("PARTNER_ITEM", zero.PriceSource);
        Assert.DoesNotContain(product.Items, r => r.PartnerId == partnerB);

        var audited = reader.Get(new CommercialPriceListQuery(
            "item", ItemId: baseItem, IncludeInactiveOverrides: true));
        Assert.Equal(3, audited.TotalCount);
        var inactive = Assert.Single(audited.Items, r => r.PartnerId == partnerB);
        Assert.False(inactive.PartnerPriceIsActive);
        Assert.Equal(77m, inactive.PartnerPriceGross);
        Assert.Equal(150m, inactive.EffectivePriceGross);
        Assert.Equal("ITEM_DEFAULT", inactive.PriceSource);

        // Partner view: one catalog item each, no implicit all-partner cross product.
        var firstPartner = reader.Get(new CommercialPriceListQuery(
            "partner", PartnerId: partnerA, Search: $"Прайс товар А {id}"));
        Assert.Equal(1, firstPartner.TotalCount);
        Assert.Equal(0m, firstPartner.Items[0].EffectivePriceGross);
        var secondPartner = reader.Get(new CommercialPriceListQuery(
            "partner", PartnerId: partnerB, ItemId: baseItem));
        Assert.Single(secondPartner.Items);
        Assert.Null(secondPartner.Items[0].PartnerPriceGross);
        Assert.Equal(150m, secondPartner.Items[0].EffectivePriceGross);
        Assert.Equal("ITEM_DEFAULT", secondPartner.Items[0].PriceSource);

        var unpriced = reader.Get(new CommercialPriceListQuery(
            "partner", PartnerId: partnerA, ItemId: missingItem));
        Assert.Empty(unpriced.Items);
        var includingMissing = reader.Get(new CommercialPriceListQuery(
            "partner", PartnerId: partnerA, ItemId: missingItem, IncludeUnpriced: true));
        Assert.Single(includingMissing.Items);
        Assert.Null(includingMissing.Items[0].EffectivePriceGross);
        Assert.Equal("NONE", includingMissing.Items[0].PriceSource);

        // Legacy partner rows can have NULL role; the report must not drop them.
        using (var conn = new NpgsqlConnection(connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE partners SET partner_role = NULL WHERE id = @partner_id";
            cmd.Parameters.AddWithValue("partner_id", partnerB);
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        var legacy = reader.Get(new CommercialPriceListQuery(
            "partner", PartnerId: partnerB, ItemId: baseItem));
        Assert.Single(legacy.Items);
        Assert.Equal(150m, legacy.Items[0].EffectivePriceGross);

        var filter = reader.Get(new CommercialPriceListQuery(
            "item", ItemId: baseItem, Brand: $"brand-{id}", Volume: "200 Г"));
        Assert.Equal(2, filter.TotalCount);

        // The server read model never changes persisted commercial data.
        using var verify = new NpgsqlConnection(connectionString);
        verify.Open();
        using var priceCmd = verify.CreateCommand();
        priceCmd.CommandText = """
SELECT (SELECT count(*) FROM partner_item_sale_prices WHERE item_id=@item_id),
       (SELECT unit_price_gross FROM items WHERE id=@item_id)
""";
        priceCmd.Parameters.AddWithValue("item_id", baseItem);
        using var row = priceCmd.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal(2L, row.GetInt64(0));
        Assert.Equal(150m, row.GetDecimal(1));
    }

    [Fact]
    public void All_mode_is_one_base_row_per_item_not_cross_product()
    {
        var connectionString = ResolveRequiredPostgresTestConnectionString();
        var id = Guid.NewGuid().ToString("N");
        var catalog = new CatalogService(new PostgresDataStore(connectionString));
        var itemId = catalog.CreateItem(
            $"Прайс аудит {id}", $"PL-AUDIT-{id}", null, "шт",
            null, null, null, null, false);
        var rows = new CommercialPriceListReader(connectionString)
            .Get(new CommercialPriceListQuery("all", ItemId: itemId));
        Assert.Equal(1, rows.TotalCount);
        Assert.Equal("BASE", rows.Items[0].RowKind);
        Assert.Equal("NONE", rows.Items[0].PriceSource);
    }

    private static string ResolveRequiredPostgresTestConnectionString()
    {
        foreach (var key in new[]
        {
            "FLOWSTOCK_POSTGRES_TEST_CONNECTION",
            "FLOWSTOCK_POSTGRES_CONNECTION",
            "POSTGRES_CONNECTION_STRING"
        })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        throw new InvalidOperationException(
            "PostgreSQL test connection is required. Set FLOWSTOCK_POSTGRES_TEST_CONNECTION.");
    }
}

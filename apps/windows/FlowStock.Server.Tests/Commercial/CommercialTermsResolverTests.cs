using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Commercial;

public sealed class CommercialTermsResolverTests
{
    [Fact]
    public void Partner_price_has_priority_over_item_default_and_vat_is_snapshotted()
    {
        var (store, resolver) = CreateResolver(ItemWithCommercialDefaults(150m));
        store.Setup(row => row.GetActivePartnerItemSalePrice(7, 10))
            .Returns(new PartnerItemSalePrice
            {
                PartnerId = 7,
                ItemId = 10,
                UnitPriceGross = 125.50m,
                IsActive = true
            });

        var result = resolver.ResolveForNewCustomerLine(7, 10, false, null);

        Assert.Equal(125.50m, result.UnitPriceGross);
        Assert.Equal(22m, result.VatRate);
        Assert.Equal(SalePriceSource.PartnerItem, result.PriceSource);
    }

    [Fact]
    public void Inactive_partner_price_falls_back_to_item_default()
    {
        var (store, resolver) = CreateResolver(ItemWithCommercialDefaults(150m));
        store.Setup(row => row.GetActivePartnerItemSalePrice(7, 10))
            .Returns((PartnerItemSalePrice?)null);

        var result = resolver.ResolveForNewCustomerLine(7, 10, false, null);

        Assert.Equal(150m, result.UnitPriceGross);
        Assert.Equal(SalePriceSource.ItemDefault, result.PriceSource);
    }

    [Fact]
    public void Zero_manual_override_is_a_known_price_and_does_not_need_automatic_price()
    {
        var (_, resolver) = CreateResolver(ItemWithCommercialDefaults(null));

        var result = resolver.ResolveForNewCustomerLine(7, 10, true, 0m);

        Assert.Equal(0m, result.UnitPriceGross);
        Assert.Equal(SalePriceSource.Manual, result.PriceSource);
    }

    [Fact]
    public void Preview_value_without_intent_is_rejected()
    {
        var (_, resolver) = CreateResolver(ItemWithCommercialDefaults(150m));

        var error = Assert.Throws<CommercialTermsException>(
            () => resolver.ResolveForNewCustomerLine(7, 10, false, 149m));

        Assert.Equal(CommercialTermsResolver.UnitPriceIntentRequired, error.ErrorCode);
    }

    [Fact]
    public void Manual_override_cannot_bypass_required_vat()
    {
        var (_, resolver) = CreateResolver(new Item
        {
            Id = 10,
            Name = "Товар",
            DefaultSalePriceGross = null,
            DefaultSaleVatRateId = null
        });

        var error = Assert.Throws<CommercialTermsException>(
            () => resolver.ResolveForNewCustomerLine(7, 10, true, 100m));

        Assert.Equal(CommercialTermsResolver.ItemSaleVatRateRequired, error.ErrorCode);
        Assert.Contains("карточке товара", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inactive_vat_is_rejected_for_new_customer_line()
    {
        var item = ItemWithCommercialDefaults(150m);
        item = new Item
        {
            Id = item.Id,
            Name = item.Name,
            DefaultSalePriceGross = item.DefaultSalePriceGross,
            DefaultSaleVatRateId = item.DefaultSaleVatRateId,
            DefaultSaleVatRate = item.DefaultSaleVatRate,
            DefaultSaleVatRateIsActive = false
        };
        var (_, resolver) = CreateResolver(item);

        var error = Assert.Throws<CommercialTermsException>(
            () => resolver.ResolveForNewCustomerLine(7, 10, false, null));

        Assert.Equal(CommercialTermsResolver.VatRateInactive, error.ErrorCode);
    }

    [Fact]
    public void Missing_automatic_price_without_override_is_rejected()
    {
        var (store, resolver) = CreateResolver(ItemWithCommercialDefaults(null));
        store.Setup(row => row.GetActivePartnerItemSalePrice(7, 10))
            .Returns((PartnerItemSalePrice?)null);

        var error = Assert.Throws<CommercialTermsException>(
            () => resolver.ResolveForNewCustomerLine(7, 10, false, null));

        Assert.Equal(CommercialTermsResolver.ItemSalePriceRequired, error.ErrorCode);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.00001")]
    public void Invalid_manual_price_is_rejected(string raw)
    {
        var price = decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);

        var error = Assert.Throws<CommercialTermsException>(
            () => CommercialTermsResolver.ValidateManualPrice(price));

        Assert.Equal(CommercialTermsResolver.InvalidUnitPrice, error.ErrorCode);
    }

    private static (Mock<IDataStore> Store, CommercialTermsResolver Resolver) CreateResolver(Item item)
    {
        var store = new Mock<IDataStore>(MockBehavior.Loose);
        store.Setup(row => row.GetPartner(7))
            .Returns(new Partner { Id = 7, Name = "Клиент" });
        store.Setup(row => row.FindItemById(10)).Returns(item);
        return (store, new CommercialTermsResolver(store.Object));
    }

    private static Item ItemWithCommercialDefaults(decimal? price) => new()
    {
        Id = 10,
        Name = "Товар",
        DefaultSalePriceGross = price,
        DefaultSaleVatRateId = 3,
        DefaultSaleVatRate = 22m,
        DefaultSaleVatRateIsActive = true
    };
}

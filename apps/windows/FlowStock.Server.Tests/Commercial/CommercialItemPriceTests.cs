using FlowStock.Core.Abstractions;
using FlowStock.Core.Commercial;
using FlowStock.Core.Models;
using Moq;

namespace FlowStock.Server.Tests.Commercial;

public sealed class CommercialItemPriceTests
{
    private static readonly PriceGroup BaseGroup = new()
    {
        Id = 1,
        Name = CommercialPricingConstants.BasePriceGroupName,
        IsActive = true,
        IsSystem = true,
        IsDefault = true
    };

    [Fact]
    public void GetItemPriceCatalog_EnrichesCalculatedPrice()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        commercial.Setup(s => s.GetPriceGroup(5)).Returns(new PriceGroup
        {
            Id = 5,
            Name = "Retail",
            IsActive = true,
            DefaultDiscountPercent = 10m
        });
        commercial.Setup(s => s.GetSystemBasePriceGroup()).Returns(BaseGroup);
        commercial.Setup(s => s.GetItemPriceCatalogForGroup(5, 1, null, null, null))
            .Returns(new[]
            {
                new ItemPriceCatalogRow
                {
                    ItemId = 1,
                    ItemName = "With base",
                    PriceGroupId = 5,
                    BasePrice = 100m,
                    BaseItemPriceId = 11
                },
                new ItemPriceCatalogRow
                {
                    ItemId = 2,
                    ItemName = "No base",
                    PriceGroupId = 5
                }
            });

        var service = new CommercialPricingService(commercial.Object, data.Object);
        var rows = service.GetItemPriceCatalogForGroup(5, null, null, null);

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].HasPrice);
        Assert.Equal(90m, rows[0].CalculatedPrice);
        Assert.False(rows[1].HasBasePrice);
        Assert.Equal("Нет базовой цены", rows[1].PriceMissingReason);
    }

    [Fact]
    public void UpsertItemPrice_CreatesBasePrice()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        data.Setup(d => d.FindItemById(100)).Returns(new Item { Id = 100, Name = "Item" });
        commercial.Setup(s => s.GetPriceGroup(1)).Returns(BaseGroup);
        commercial.Setup(s => s.CloseOverlappingActiveItemPrices(100, 1, It.IsAny<DateOnly>(), null));
        commercial.Setup(s => s.AddItemPrice(It.IsAny<ItemPrice>())).Returns(42L);

        var service = new CommercialPricingService(commercial.Object, data.Object);
        var result = service.UpsertItemPrice(new UpsertItemPriceCommand
        {
            ItemId = 100,
            PriceGroupId = 1,
            Price = 150m,
            Currency = "RUB",
            ValidFrom = DateOnly.FromDateTime(DateTime.Today)
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(42L, result.ItemPriceId);
    }

    [Fact]
    public void UpsertItemPrice_RejectsZeroPrice()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        var service = new CommercialPricingService(commercial.Object, data.Object);

        var result = service.UpsertItemPrice(new UpsertItemPriceCommand
        {
            ItemId = 1,
            PriceGroupId = 1,
            Price = 0m,
            Currency = "RUB",
            ValidFrom = DateOnly.FromDateTime(DateTime.Today)
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("PRICE_IS_ZERO", result.ErrorCode);
    }

    [Fact]
    public void UpsertItemPrice_CustomGroupRequiresBasePrice()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        data.Setup(d => d.FindItemById(100)).Returns(new Item { Id = 100, Name = "Item" });
        commercial.Setup(s => s.GetPriceGroup(5)).Returns(new PriceGroup { Id = 5, Name = "Retail", IsActive = true });
        commercial.Setup(s => s.GetSystemBasePriceGroup()).Returns(BaseGroup);
        commercial.Setup(s => s.GetActiveItemPrice(100, 1, It.IsAny<DateOnly>())).Returns((ItemPrice?)null);

        var service = new CommercialPricingService(commercial.Object, data.Object);
        var result = service.UpsertItemPrice(new UpsertItemPriceCommand
        {
            ItemId = 100,
            PriceGroupId = 5,
            Price = 120m,
            Currency = "RUB",
            ValidFrom = DateOnly.FromDateTime(DateTime.Today)
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("PRICE_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public void Quote_FindsSavedBasePrice()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        commercial.Setup(s => s.GetPartnerCommercialSettings(10)).Returns((PartnerCommercialSettings?)null);
        commercial.Setup(s => s.GetSystemBasePriceGroup()).Returns(BaseGroup);
        commercial.Setup(s => s.GetPriceGroup(1)).Returns(BaseGroup);
        commercial.Setup(s => s.GetActiveItemPrice(100, 1, It.IsAny<DateOnly>())).Returns(new ItemPrice
        {
            ItemId = 100,
            PriceGroupId = 1,
            Price = 200m,
            Currency = "RUB",
            ValidFrom = DateOnly.FromDateTime(DateTime.Today)
        });
        data.Setup(d => d.FindItemById(100)).Returns(new Item { Id = 100, Name = "Item" });
        data.Setup(d => d.GetPartner(10)).Returns(new Partner { Id = 10, Name = "Client" });

        var service = new CommercialPricingService(commercial.Object, data.Object);
        var result = service.Quote(new PricingQuoteRequest
        {
            ItemId = 100,
            PartnerId = 10,
            Qty = 1,
            AsOfDate = DateOnly.FromDateTime(DateTime.Today),
            PriceGroupOverrideId = 1
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(200m, result.GroupPrice);
    }

    [Fact]
    public void AddLineFromQuote_ThrowsPriceNotFound_WhenNoBasePrice()
    {
        var service = CreateOfferService(out var commercial, out var data, out _);
        commercial.Setup(s => s.GetCommercialOffer(1)).Returns(CreateDraftOffer());
        commercial.Setup(s => s.GetCommercialOfferLines(1)).Returns(Array.Empty<CommercialOfferLine>());
        data.Setup(d => d.FindItemById(100)).Returns(new Item { Id = 100, Name = "Item" });
        data.Setup(d => d.GetPartner(10)).Returns(new Partner { Id = 10, Name = "Client" });
        commercial.Setup(s => s.GetSystemBasePriceGroup()).Returns(BaseGroup);
        commercial.Setup(s => s.GetPriceGroup(5)).Returns(new PriceGroup { Id = 5, IsActive = true });
        commercial.Setup(s => s.GetPartnerCommercialSettings(10)).Returns((PartnerCommercialSettings?)null);
        commercial.Setup(s => s.GetActiveItemPrice(100, 1, It.IsAny<DateOnly>())).Returns((ItemPrice?)null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.AddLineFromQuote(1, 100, 1, null, 0m, null));

        Assert.Equal("PRICE_NOT_FOUND", ex.Message);
    }

    [Fact]
    public void AddLineFromQuote_ThrowsPriceIsZero_WhenManualDiscountIs100()
    {
        var service = CreateOfferService(out var commercial, out var data, out _);
        commercial.Setup(s => s.GetCommercialOffer(1)).Returns(CreateDraftOffer());
        data.Setup(d => d.FindItemById(100)).Returns(new Item { Id = 100, Name = "Item" });
        data.Setup(d => d.GetPartner(10)).Returns(new Partner { Id = 10, Name = "Client" });
        commercial.Setup(s => s.GetSystemBasePriceGroup()).Returns(BaseGroup);
        commercial.Setup(s => s.GetPriceGroup(5)).Returns(new PriceGroup { Id = 5, IsActive = true });
        commercial.Setup(s => s.GetPartnerCommercialSettings(10)).Returns((PartnerCommercialSettings?)null);
        commercial.Setup(s => s.GetActiveItemPrice(100, 1, It.IsAny<DateOnly>())).Returns(new ItemPrice
        {
            ItemId = 100,
            PriceGroupId = 1,
            Price = 100m,
            Currency = "RUB",
            ValidFrom = DateOnly.FromDateTime(DateTime.Today)
        });
        commercial.Setup(s => s.GetActiveItemPrice(100, 5, It.IsAny<DateOnly>())).Returns((ItemPrice?)null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.AddLineFromQuote(1, 100, 1, null, 100m, null));

        Assert.Equal("PRICE_IS_ZERO", ex.Message);
        commercial.Verify(s => s.AddCommercialOfferLine(It.IsAny<CommercialOfferLine>()), Times.Never);
    }

  private static CommercialOfferService CreateOfferService(
        out Mock<ICommercialDataStore> commercial,
        out Mock<IDataStore> data,
        out CommercialPricingService pricing)
    {
        commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        data = new Mock<IDataStore>(MockBehavior.Strict);
        pricing = new CommercialPricingService(commercial.Object, data.Object);
        return new CommercialOfferService(commercial.Object, data.Object, pricing);
    }

    private static CommercialOffer CreateDraftOffer() => new()
    {
        Id = 1,
        OfferRef = "CO-2026-000001",
        PartnerId = 10,
        PriceGroupId = 5,
        Status = CommercialOfferStatus.Draft,
        Currency = "RUB",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
}


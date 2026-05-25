using FlowStock.Core.Abstractions;
using FlowStock.Core.Commercial;
using FlowStock.Core.Models;
using Moq;

namespace FlowStock.Server.Tests.Commercial;

public sealed class CommercialPricingServiceTests
{
  private static readonly PriceGroup BaseGroup = new()
  {
    Id = 1,
    Name = CommercialPricingConstants.BasePriceGroupName,
    IsActive = true,
    IsSystem = true,
    IsDefault = true
  };

  private static readonly PriceGroup CustomGroup = new()
  {
    Id = 5,
    Name = "HoReCa",
    IsActive = true,
    DefaultDiscountPercent = 10m,
    DefaultMarkupPercent = 0m
  };

  [Fact]
  public void Quote_BaseGroup_ReturnsBasePrice()
  {
    var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
    var data = new Mock<IDataStore>(MockBehavior.Strict);
    SetupPartnerAndItem(commercial, data);
    commercial.Setup(s => s.GetSystemBasePriceGroup()).Returns(BaseGroup);
    commercial.Setup(s => s.GetPriceGroup(5)).Returns(CustomGroup);
        commercial.Setup(s => s.GetActiveItemPrice(100, 1, It.IsAny<DateOnly>())).Returns(BaseItemPrice(100m));
        commercial.Setup(s => s.GetActiveItemPrice(100, 5, It.IsAny<DateOnly>())).Returns((ItemPrice?)null);

        var service = new CommercialPricingService(commercial.Object, data.Object);
        var result = service.Quote(new PricingQuoteRequest
        {
            ItemId = 100,
            PartnerId = 10,
            Qty = 1,
            AsOfDate = DateOnly.FromDateTime(DateTime.Today),
            PriceGroupOverrideId = 5
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(100m, result.CatalogBasePrice);
        Assert.Equal(90m, result.GroupPrice);
        Assert.Equal(PriceSourceKindMapper.ToCode(PriceSourceKind.GroupRule), result.PriceSource);
    }

    [Fact]
    public void Quote_CustomGroupWithOverride_UsesOverridePrice()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        SetupPartnerAndItem(commercial, data);
        commercial.Setup(s => s.GetSystemBasePriceGroup()).Returns(BaseGroup);
        commercial.Setup(s => s.GetPriceGroup(5)).Returns(CustomGroup);
        commercial.Setup(s => s.GetActiveItemPrice(100, 1, It.IsAny<DateOnly>())).Returns(BaseItemPrice(100m));
        commercial.Setup(s => s.GetActiveItemPrice(100, 5, It.IsAny<DateOnly>())).Returns(new ItemPrice
        {
            ItemId = 100,
            PriceGroupId = 5,
            Price = 77m,
            Currency = "RUB",
            ValidFrom = DateOnly.FromDateTime(DateTime.Today)
        });

        var service = new CommercialPricingService(commercial.Object, data.Object);
        var result = service.Quote(new PricingQuoteRequest
        {
            ItemId = 100,
            PartnerId = 10,
            Qty = 1,
            AsOfDate = DateOnly.FromDateTime(DateTime.Today),
            PriceGroupOverrideId = 5
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(77m, result.GroupPrice);
        Assert.Equal(PriceSourceKindMapper.ToCode(PriceSourceKind.GroupOverride), result.PriceSource);
    }

    [Fact]
    public void Quote_UsesPartnerPriceGroup_WhenConfigured()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        SetupPartnerAndItem(commercial, data);
        commercial.Setup(s => s.GetPartnerCommercialSettings(10)).Returns(new PartnerCommercialSettings
        {
            PartnerId = 10,
            PriceGroupId = 5,
            DefaultDiscountPercent = 0m,
            UpdatedAt = DateTime.UtcNow
        });
        commercial.Setup(s => s.GetSystemBasePriceGroup()).Returns(BaseGroup);
        commercial.Setup(s => s.GetPriceGroup(5)).Returns(CustomGroup);
        commercial.Setup(s => s.GetActiveItemPrice(100, 1, It.IsAny<DateOnly>())).Returns(BaseItemPrice(100m));
        commercial.Setup(s => s.GetActiveItemPrice(100, 5, It.IsAny<DateOnly>())).Returns((ItemPrice?)null);

        var service = new CommercialPricingService(commercial.Object, data.Object);
        var result = service.Quote(new PricingQuoteRequest
        {
            ItemId = 100,
            PartnerId = 10,
            Qty = 1,
            AsOfDate = DateOnly.FromDateTime(DateTime.Today)
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.PriceGroupId);
        Assert.Equal(90m, result.GroupPrice);
    }

    [Fact]
    public void Quote_FallsBackToSystemBaseGroup()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        SetupPartnerAndItem(commercial, data);
        commercial.Setup(s => s.GetPartnerCommercialSettings(10)).Returns((PartnerCommercialSettings?)null);
        commercial.Setup(s => s.GetSystemBasePriceGroup()).Returns(BaseGroup);
        commercial.Setup(s => s.GetPriceGroup(1)).Returns(BaseGroup);
        commercial.Setup(s => s.GetActiveItemPrice(100, 1, It.IsAny<DateOnly>())).Returns(BaseItemPrice(50m));

        var service = new CommercialPricingService(commercial.Object, data.Object);
        var result = service.Quote(new PricingQuoteRequest
        {
            ItemId = 100,
            PartnerId = 10,
            Qty = 2,
            AsOfDate = DateOnly.FromDateTime(DateTime.Today)
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.PriceGroupId);
        Assert.Equal(50m, result.GroupPrice);
    }

    [Fact]
    public void Quote_ReturnsPriceNotFound_WhenBasePriceMissing()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        SetupPartnerAndItem(commercial, data);
        commercial.Setup(s => s.GetPartnerCommercialSettings(10)).Returns((PartnerCommercialSettings?)null);
        commercial.Setup(s => s.GetSystemBasePriceGroup()).Returns(BaseGroup);
        commercial.Setup(s => s.GetPriceGroup(1)).Returns(BaseGroup);
        commercial.Setup(s => s.GetActiveItemPrice(100, 1, It.IsAny<DateOnly>())).Returns((ItemPrice?)null);

        var service = new CommercialPricingService(commercial.Object, data.Object);
        var result = service.Quote(new PricingQuoteRequest
        {
            ItemId = 100,
            PartnerId = 10,
            Qty = 1,
            AsOfDate = DateOnly.FromDateTime(DateTime.Today)
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("PRICE_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public void Quote_AppliesPartnerAndManualDiscountsSequentially()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        SetupPartnerAndItem(commercial, data);
        commercial.Setup(s => s.GetPartnerCommercialSettings(10)).Returns(new PartnerCommercialSettings
        {
            PartnerId = 10,
            PriceGroupId = 1,
            DefaultDiscountPercent = 10m,
            UpdatedAt = DateTime.UtcNow
        });
        commercial.Setup(s => s.GetSystemBasePriceGroup()).Returns(BaseGroup);
        commercial.Setup(s => s.GetPriceGroup(1)).Returns(BaseGroup);
        commercial.Setup(s => s.GetActiveItemPrice(100, 1, It.IsAny<DateOnly>())).Returns(BaseItemPrice(100m));

        var service = new CommercialPricingService(commercial.Object, data.Object);
        var result = service.Quote(new PricingQuoteRequest
        {
            ItemId = 100,
            PartnerId = 10,
            Qty = 1,
            AsOfDate = DateOnly.FromDateTime(DateTime.Today),
            ManualDiscountPercent = 10m
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(81m, result.FinalPrice);
        Assert.Equal(20m, result.FinalDiscountPercent);
    }

    [Fact]
    public void Quote_ReturnsPriceIsZero_WhenManualDiscountMakesFinalPriceZero()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        SetupPartnerAndItem(commercial, data);
        commercial.Setup(s => s.GetSystemBasePriceGroup()).Returns(BaseGroup);
        commercial.Setup(s => s.GetPriceGroup(1)).Returns(BaseGroup);
        commercial.Setup(s => s.GetActiveItemPrice(100, 1, It.IsAny<DateOnly>())).Returns(BaseItemPrice(100m));

        var service = new CommercialPricingService(commercial.Object, data.Object);
        var result = service.Quote(new PricingQuoteRequest
        {
            ItemId = 100,
            PartnerId = 10,
            Qty = 1,
            AsOfDate = DateOnly.FromDateTime(DateTime.Today),
            PriceGroupOverrideId = 1,
            ManualDiscountPercent = 100m
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("PRICE_IS_ZERO", result.ErrorCode);
    }

    [Fact]
    public void Quote_ReturnsPriceIsZero_WhenPartnerDiscountMakesFinalPriceZero()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        SetupPartnerAndItem(commercial, data);
        commercial.Setup(s => s.GetPartnerCommercialSettings(10)).Returns(new PartnerCommercialSettings
        {
            PartnerId = 10,
            PriceGroupId = 1,
            DefaultDiscountPercent = 100m,
            UpdatedAt = DateTime.UtcNow
        });
        commercial.Setup(s => s.GetSystemBasePriceGroup()).Returns(BaseGroup);
        commercial.Setup(s => s.GetPriceGroup(1)).Returns(BaseGroup);
        commercial.Setup(s => s.GetActiveItemPrice(100, 1, It.IsAny<DateOnly>())).Returns(BaseItemPrice(100m));

        var service = new CommercialPricingService(commercial.Object, data.Object);
        var result = service.Quote(new PricingQuoteRequest
        {
            ItemId = 100,
            PartnerId = 10,
            Qty = 1,
            AsOfDate = DateOnly.FromDateTime(DateTime.Today)
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("PRICE_IS_ZERO", result.ErrorCode);
    }

    [Fact]
    public void Quote_AllowsNormalPartnerAndManualDiscounts()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        SetupPartnerAndItem(commercial, data);
        commercial.Setup(s => s.GetPartnerCommercialSettings(10)).Returns(new PartnerCommercialSettings
        {
            PartnerId = 10,
            PriceGroupId = 1,
            DefaultDiscountPercent = 15m,
            UpdatedAt = DateTime.UtcNow
        });
        commercial.Setup(s => s.GetSystemBasePriceGroup()).Returns(BaseGroup);
        commercial.Setup(s => s.GetPriceGroup(1)).Returns(BaseGroup);
        commercial.Setup(s => s.GetActiveItemPrice(100, 1, It.IsAny<DateOnly>())).Returns(BaseItemPrice(100m));

        var service = new CommercialPricingService(commercial.Object, data.Object);
        var result = service.Quote(new PricingQuoteRequest
        {
            ItemId = 100,
            PartnerId = 10,
            Qty = 1,
            AsOfDate = DateOnly.FromDateTime(DateTime.Today),
            ManualDiscountPercent = 10m
        });

        Assert.True(result.IsSuccess);
        Assert.True(result.FinalPrice > 0m);
        Assert.Equal(76.5m, result.FinalPrice);
    }

    [Fact]
    public void CombineSequentialPercent_CapsAt100()
    {
        var combined = CommercialPricingService.CombineSequentialPercent(60m, 60m);
        Assert.Equal(100m, combined);
    }

    [Fact]
    public void RoundMoney_AvoidsFloatDrift()
    {
        var value = CommercialPricingService.RoundMoney(10.005m);
        Assert.Equal(10.01m, value);
    }

    private static void SetupPartnerAndItem(Mock<ICommercialDataStore> commercial, Mock<IDataStore> data)
    {
        commercial.Setup(s => s.GetPartnerCommercialSettings(10)).Returns((PartnerCommercialSettings?)null);
        data.Setup(d => d.FindItemById(100)).Returns(new Item { Id = 100, Name = "Item" });
        data.Setup(d => d.GetPartner(10)).Returns(new Partner { Id = 10, Name = "Client" });
    }

    private static ItemPrice BaseItemPrice(decimal price) => new()
    {
        ItemId = 100,
        PriceGroupId = 1,
        Price = price,
        Currency = "RUB",
        ValidFrom = DateOnly.FromDateTime(DateTime.Today)
    };
}


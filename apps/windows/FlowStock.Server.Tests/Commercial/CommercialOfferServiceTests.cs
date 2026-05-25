using FlowStock.Core.Abstractions;
using FlowStock.Core.Commercial;
using FlowStock.Core.Models;
using Moq;

namespace FlowStock.Server.Tests.Commercial;

public sealed class CommercialOfferServiceTests
{
    [Fact]
    public void CreateDraftOffer_WithoutPartnerId_ThrowsPartnerIdRequired()
    {
        var service = CreateService(out _, out _);

        var ex = Assert.Throws<InvalidOperationException>(() => service.CreateDraftOffer(new CreateCommercialOfferCommand
        {
            PartnerId = 0
        }));

        Assert.Equal("PARTNER_ID_REQUIRED", ex.Message);
    }

    [Fact]
    public void CreateDraftOffer_WithPartnerAndPriceGroup_UsesSelectedPartnerAndGroup()
    {
        var service = CreateService(out var commercial, out var data);
        data.Setup(d => d.GetPartner(10)).Returns(new Partner { Id = 10, Name = "Client" });
        commercial.Setup(s => s.GetPriceGroup(5)).Returns(new PriceGroup { Id = 5, Name = "Retail", IsActive = true });
        commercial.Setup(s => s.GetMaxCommercialOfferRefSequenceByYear(It.IsAny<int>())).Returns(0);
        commercial.Setup(s => s.AddCommercialOffer(It.IsAny<CommercialOffer>())).Returns(100L);
        commercial.Setup(s => s.AddCommercialOfferStatusHistory(It.IsAny<CommercialOfferStatusHistoryEntry>()));

        var (offerId, offerRef) = service.CreateDraftOffer(new CreateCommercialOfferCommand
        {
            PartnerId = 10,
            PriceGroupId = 5,
            ManagerName = "manager"
        });

        Assert.Equal(100L, offerId);
        Assert.StartsWith("CO-", offerRef);
        commercial.Verify(s => s.AddCommercialOffer(It.Is<CommercialOffer>(offer =>
            offer.PartnerId == 10 && offer.PriceGroupId == 5)), Times.Once);
    }

    [Fact]
    public void CreateDraftOffer_WithoutPriceGroup_UsesPartnerSettingsGroup()
    {
        var service = CreateService(out var commercial, out var data);
        data.Setup(d => d.GetPartner(10)).Returns(new Partner { Id = 10, Name = "Client" });
        commercial.Setup(s => s.GetPartnerCommercialSettings(10)).Returns(new PartnerCommercialSettings
        {
            PartnerId = 10,
            PriceGroupId = 7,
            UpdatedAt = DateTime.UtcNow
        });
        commercial.Setup(s => s.GetPriceGroup(7)).Returns(new PriceGroup { Id = 7, Name = "HoReCa", IsActive = true });
        commercial.Setup(s => s.GetMaxCommercialOfferRefSequenceByYear(It.IsAny<int>())).Returns(1);
        commercial.Setup(s => s.AddCommercialOffer(It.IsAny<CommercialOffer>())).Returns(101L);
        commercial.Setup(s => s.AddCommercialOfferStatusHistory(It.IsAny<CommercialOfferStatusHistoryEntry>()));

        var (_, _) = service.CreateDraftOffer(new CreateCommercialOfferCommand
        {
            PartnerId = 10
        });

        commercial.Verify(s => s.AddCommercialOffer(It.Is<CommercialOffer>(offer => offer.PriceGroupId == 7)), Times.Once);
    }

    [Fact]
    public void CreateDraftOffer_WithoutPriceGroup_UsesDefaultGroupWhenPartnerHasNoSettings()
    {
        var service = CreateService(out var commercial, out var data);
        data.Setup(d => d.GetPartner(10)).Returns(new Partner { Id = 10, Name = "Client" });
        commercial.Setup(s => s.GetPartnerCommercialSettings(10)).Returns((PartnerCommercialSettings?)null);
        commercial.Setup(s => s.GetSystemBasePriceGroup()).Returns(new PriceGroup { Id = 1, Name = CommercialPricingConstants.BasePriceGroupName, IsActive = true, IsSystem = true });
        commercial.Setup(s => s.GetPriceGroup(1)).Returns(new PriceGroup { Id = 1, Name = CommercialPricingConstants.BasePriceGroupName, IsActive = true, IsSystem = true });
        commercial.Setup(s => s.GetMaxCommercialOfferRefSequenceByYear(It.IsAny<int>())).Returns(0);
        commercial.Setup(s => s.AddCommercialOffer(It.IsAny<CommercialOffer>())).Returns(102L);
        commercial.Setup(s => s.AddCommercialOfferStatusHistory(It.IsAny<CommercialOfferStatusHistoryEntry>()));

        service.CreateDraftOffer(new CreateCommercialOfferCommand { PartnerId = 10 });

        commercial.Verify(s => s.AddCommercialOffer(It.Is<CommercialOffer>(offer => offer.PriceGroupId == 1)), Times.Once);
    }

    [Fact]
    public void ChangeStatus_ToSent_WithoutLines_ThrowsOfferLinesRequired()
    {
        var service = CreateService(out var commercial, out _);
        commercial.Setup(s => s.GetCommercialOffer(1)).Returns(CreateOffer(CommercialOfferStatus.Draft));
        commercial.Setup(s => s.GetCommercialOfferLines(1)).Returns(Array.Empty<CommercialOfferLine>());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.ChangeStatus(1, CommercialOfferStatus.Sent, null, "tester"));

        Assert.Equal("OFFER_LINES_REQUIRED", ex.Message);
    }

    [Fact]
    public void ChangeStatus_ToWon_WithoutLines_ThrowsOfferLinesRequired()
    {
        var service = CreateService(out var commercial, out _);
        commercial.Setup(s => s.GetCommercialOffer(1)).Returns(CreateOffer(CommercialOfferStatus.Sent));
        commercial.Setup(s => s.GetCommercialOfferLines(1)).Returns(Array.Empty<CommercialOfferLine>());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.ChangeStatus(1, CommercialOfferStatus.Won, null, "tester"));

        Assert.Equal("OFFER_LINES_REQUIRED", ex.Message);
    }

    [Fact]
    public void CreateCustomerOrderFromWonOffer_WithoutLines_ThrowsOfferLinesRequired()
    {
        var service = CreateService(out var commercial, out _);
        commercial.Setup(s => s.GetCommercialOffer(1)).Returns(CreateOffer(CommercialOfferStatus.Won));
        commercial.Setup(s => s.GetCommercialOfferLines(1)).Returns(Array.Empty<CommercialOfferLine>());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.CreateCustomerOrderFromWonOffer(1, "ORD-1", null, null));

        Assert.Equal("OFFER_LINES_REQUIRED", ex.Message);
    }

    private static CommercialOfferService CreateService(
        out Mock<ICommercialDataStore> commercial,
        out Mock<IDataStore> data)
    {
        commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        data = new Mock<IDataStore>(MockBehavior.Strict);
        var pricingService = new CommercialPricingService(commercial.Object, data.Object);
        return new CommercialOfferService(commercial.Object, data.Object, pricingService);
    }

    private static CommercialOffer CreateOffer(CommercialOfferStatus status) => new()
    {
        Id = 1,
        OfferRef = "CO-2026-000001",
        PartnerId = 10,
        PriceGroupId = 5,
        Status = status,
        Currency = "RUB",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
}

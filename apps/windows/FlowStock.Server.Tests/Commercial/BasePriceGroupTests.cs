using FlowStock.Core.Abstractions;
using FlowStock.Core.Commercial;
using Moq;

namespace FlowStock.Server.Tests.Commercial;

public sealed class BasePriceGroupTests
{
    [Fact]
    public void EnsureSystemBasePriceGroup_CreatesWhenMissing()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        commercial.Setup(s => s.EnsureSystemBasePriceGroup()).Returns(1L);

        var id = commercial.Object.EnsureSystemBasePriceGroup();

        Assert.Equal(1L, id);
        commercial.Verify(s => s.EnsureSystemBasePriceGroup(), Times.Once);
    }

    [Fact]
    public void GetSystemBasePriceGroup_ReturnsSystemGroup()
    {
        var commercial = new Mock<ICommercialDataStore>(MockBehavior.Strict);
        commercial.Setup(s => s.GetSystemBasePriceGroup()).Returns(new PriceGroup
        {
            Id = 1,
            Name = CommercialPricingConstants.BasePriceGroupName,
            IsSystem = true,
            IsDefault = true,
            IsActive = true
        });

        var group = commercial.Object.GetSystemBasePriceGroup();

        Assert.NotNull(group);
        Assert.True(group!.IsSystem);
        Assert.Equal(CommercialPricingConstants.BasePriceGroupName, group.Name);
    }
}

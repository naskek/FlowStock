using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Commercial;

public sealed class VatRateAndCustomerPriceServiceTests
{
    [Fact]
    public void Used_vat_rate_value_cannot_change_but_metadata_can()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(row => row.UpdateVatRate(It.Is<VatRate>(vat => vat.Rate == 20m)))
            .Throws(new InvalidOperationException(
                "Нельзя изменить числовое значение ставки НДС, которая назначена товарам."));
        store.Setup(row => row.UpdateVatRate(It.Is<VatRate>(vat => vat.Rate == 22m)));
        var service = new VatRateService(store.Object);

        Assert.Throws<InvalidOperationException>(
            () => service.UpdateVatRate(1, "НДС 20%", 20m, 0, true));

        service.UpdateVatRate(1, "НДС 22 (архив)", 22m, 10, false);
        store.Verify(row => row.UpdateVatRate(It.Is<VatRate>(vat =>
            vat.Id == 1
            && vat.Name == "НДС 22 (архив)"
            && vat.SortOrder == 10
            && !vat.IsActive)), Times.Once);
    }

    [Fact]
    public void Unused_vat_rate_can_be_deleted_and_used_rate_cannot()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.SetupSequence(row => row.DeleteVatRate(1))
            .Pass()
            .Throws(new InvalidOperationException(
                "Нельзя удалить ставку НДС, которая назначена товарам."));
        var service = new VatRateService(store.Object);

        service.DeleteVatRate(1);
        Assert.Throws<InvalidOperationException>(() => service.DeleteVatRate(1));
        store.Verify(row => row.DeleteVatRate(1), Times.Exactly(2));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("22.00001")]
    [InlineData("1000")]
    public void Invalid_vat_rate_is_rejected(string raw)
    {
        var store = new Mock<IDataStore>(MockBehavior.Loose);
        var service = new VatRateService(store.Object);
        var rate = decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Throws<ArgumentException>(() => service.CreateVatRate("НДС", rate, 0, true));
    }

    [Fact]
    public void Active_customer_price_must_be_deactivated_before_delete()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(row => row.DeletePartnerItemSalePrice(3))
            .Throws(new CommercialTermsException(
                "PARTNER_ITEM_PRICE_MUST_BE_INACTIVE_BEFORE_DELETE",
                "Перед удалением цену клиента необходимо деактивировать."));
        var service = new PartnerItemSalePriceService(store.Object);

        var error = Assert.Throws<CommercialTermsException>(() => service.Delete(3));

        Assert.Equal("PARTNER_ITEM_PRICE_MUST_BE_INACTIVE_BEFORE_DELETE", error.ErrorCode);
        store.Verify(row => row.DeletePartnerItemSalePrice(3), Times.Once);
    }
}

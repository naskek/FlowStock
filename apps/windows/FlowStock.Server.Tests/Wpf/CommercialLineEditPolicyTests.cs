using FlowStock.App;
using FlowStock.Core.Models;

namespace FlowStock.Server.Tests.Wpf;

public sealed class CommercialLineEditPolicyTests
{
    [Theory]
    [InlineData("ITEM_SALE_PRICE_REQUIRED", false)]
    [InlineData("ITEM_SALE_VAT_RATE_REQUIRED", true)]
    [InlineData("VAT_RATE_INACTIVE_FOR_NEW_ORDER_LINE", true)]
    [InlineData("ITEM_SALE_VAT_RATE_REFERENCE_INVALID", true)]
    public void Preview_issue_controls_whether_new_line_can_enter_local_collection(
        string issueCode,
        bool expectedBlocked)
    {
        var decision = CommercialLineEditPolicy.ForNewLine(new CommercialTermsPreview(
            PartnerId: 10,
            ItemId: 20,
            AutomaticUnitPriceGross: null,
            PriceSource: SalePriceSource.None,
            VatRate: null,
            IssueCode: issueCode,
            IssueMessage: $"Ошибка {issueCode}"));

        Assert.Equal(expectedBlocked, decision.BlockAddingLine);
        Assert.Equal(!expectedBlocked, decision.AllowDialog);
    }

    [Fact]
    public void Missing_automatic_price_requires_explicit_override_only_for_new_line()
    {
        Assert.False(CommercialLineEditPolicy.CanSaveWithoutManualPrice(
            requirePriceForSave: true,
            automaticUnitPriceGross: null));
        Assert.True(CommercialLineEditPolicy.CanSaveWithoutManualPrice(
            requirePriceForSave: false,
            automaticUnitPriceGross: null));
    }
}

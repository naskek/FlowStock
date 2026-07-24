using FlowStock.Core.Models;

namespace FlowStock.App;

internal static class CommercialLineEditPolicy
{
    public static CommercialLinePreviewDecision ForNewLine(CommercialTermsPreview? preview)
    {
        var blockAddingLine = preview?.IssueCode?.Trim().ToUpperInvariant() is
            "ITEM_SALE_VAT_RATE_REQUIRED"
            or "VAT_RATE_INACTIVE_FOR_NEW_ORDER_LINE"
            or "ITEM_SALE_VAT_RATE_REFERENCE_INVALID";
        return new CommercialLinePreviewDecision(
            AllowDialog: !blockAddingLine,
            BlockAddingLine: blockAddingLine,
            IssueCode: preview?.IssueCode,
            IssueMessage: preview?.IssueMessage);
    }

    public static bool CanSaveWithoutManualPrice(
        bool requirePriceForSave,
        decimal? automaticUnitPriceGross) =>
        !requirePriceForSave || automaticUnitPriceGross.HasValue;
}

internal sealed record CommercialLinePreviewDecision(
    bool AllowDialog,
    bool BlockAddingLine,
    string? IssueCode,
    string? IssueMessage);

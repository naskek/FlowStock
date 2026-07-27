using FlowStock.Core.Models;

namespace FlowStock.App;

internal static class CommercialLineEditPolicy
{
    public static CommercialLinePreviewDecision ForNewLine(CommercialTermsPreview? preview)
    {
        var issueCode = preview?.IssueCode?.Trim().ToUpperInvariant();
        var blockAddingLine = !string.IsNullOrWhiteSpace(issueCode)
                              && issueCode != "ITEM_SALE_PRICE_REQUIRED";
        return new CommercialLinePreviewDecision(
            AllowDialog: !blockAddingLine,
            BlockAddingLine: blockAddingLine,
            IssueCode: issueCode,
            IssueMessage: ResolveIssueMessage(issueCode, preview?.IssueMessage));
    }

    public static bool CanSaveWithoutManualPrice(
        bool requirePriceForSave,
        decimal? automaticUnitPriceGross) =>
        !requirePriceForSave || automaticUnitPriceGross.HasValue;

    private static string? ResolveIssueMessage(string? issueCode, string? serverMessage)
    {
        if (!string.IsNullOrWhiteSpace(serverMessage))
        {
            return serverMessage;
        }

        return issueCode switch
        {
            "ITEM_SALE_VAT_RATE_REQUIRED" =>
                "Для товара не выбрана ставка НДС продажи. Укажите ставку в карточке товара.",
            "VAT_RATE_INACTIVE_FOR_NEW_ORDER_LINE" =>
                "Выбранная в карточке товара ставка НДС неактивна. Укажите активную ставку НДС продажи.",
            "ITEM_SALE_VAT_RATE_REFERENCE_INVALID" =>
                "Выбранная в карточке товара ставка НДС не найдена. Исправьте ставку НДС продажи.",
            _ => serverMessage
        };
    }
}

internal sealed record CommercialLinePreviewDecision(
    bool AllowDialog,
    bool BlockAddingLine,
    string? IssueCode,
    string? IssueMessage);

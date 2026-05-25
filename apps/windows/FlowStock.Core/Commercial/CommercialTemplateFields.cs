namespace FlowStock.Core.Commercial;

public static class CommercialTemplateFields
{
    public static IReadOnlyList<CommercialTemplateFieldGroup> GetFieldGroups() =>
    [
        new CommercialTemplateFieldGroup(
            "Заголовок КП",
            [
                "{{OfferNumber}}", "{{OfferDate}}", "{{ValidUntil}}",
                "{{PartnerName}}", "{{PartnerInn}}", "{{PartnerCode}}", "{{PartnerAddress}}",
                "{{ContactPerson}}", "{{ContactPhone}}", "{{ContactEmail}}",
                "{{ManagerName}}", "{{CompanyName}}", "{{CompanyPhone}}", "{{CompanyEmail}}",
                "{{PaymentTerms}}", "{{DeliveryTerms}}", "{{Currency}}",
                "{{Subtotal}}", "{{DiscountTotal}}", "{{Total}}", "{{VatText}}", "{{Comment}}"
            ]),
        new CommercialTemplateFieldGroup(
            "Строки КП",
            [
                "{{#Lines}}",
                "{{LineNo}}", "{{ItemName}}", "{{ItemSku}}", "{{Barcode}}", "{{Gtin}}",
                "{{Brand}}", "{{Volume}}", "{{PackageInfo}}", "{{Qty}}", "{{Uom}}",
                "{{BasePrice}}", "{{VolumeDiscountPercent}}", "{{ManualDiscountPercent}}",
                "{{FinalDiscountPercent}}", "{{FinalPrice}}", "{{LineTotal}}", "{{LineComment}}",
                "{{/Lines}}"
            ])
    ];
}

public sealed record CommercialTemplateFieldGroup(string Title, IReadOnlyList<string> Fields);

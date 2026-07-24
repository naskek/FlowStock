using FlowStock.Core.Services;

namespace FlowStock.Server;

public static class CommercialTermsPreviewEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/commercial-terms/preview", (
            long? partner_id,
            long? item_id,
            CommercialTermsResolver resolver,
            PartnerRoleResolver roleResolver) =>
        {
            if (!partner_id.HasValue || partner_id.Value <= 0)
            {
                return Results.BadRequest(new ApiErrorResult(false, "PARTNER_REQUIRED", "Выберите клиента."));
            }
            if (!item_id.HasValue || item_id.Value <= 0)
            {
                return Results.BadRequest(new ApiErrorResult(false, "ITEM_REQUIRED", "Выберите товар."));
            }
            if (!roleResolver.IsCustomer(partner_id.Value))
            {
                return Results.BadRequest(new ApiErrorResult(false, "PARTNER_IS_SUPPLIER", "Выбранный контрагент не является клиентом."));
            }

            try
            {
                var preview = resolver.Preview(partner_id.Value, item_id.Value);
                return Results.Ok(new
                {
                    partner_id = preview.PartnerId,
                    item_id = preview.ItemId,
                    automatic_unit_price_gross = preview.AutomaticUnitPriceGross,
                    price_source = preview.PriceSource.ToString().ToUpperInvariant(),
                    vat_rate = preview.VatRate,
                    issue_code = preview.IssueCode,
                    issue_message = preview.IssueMessage
                });
            }
            catch (CommercialTermsException ex)
            {
                return Results.BadRequest(new ApiErrorResult(false, ex.ErrorCode, ex.Message));
            }
        });
    }
}

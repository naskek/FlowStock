using System.Globalization;
using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Commercial;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class CommercialOfferEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/commercial/offers", (ICommercialDataStore store, string? status, long? partner_id, string? from, string? to) =>
        {
            DateOnly? fromDate = ParseDate(from);
            DateOnly? toDate = ParseDate(to);
            var offers = store.GetCommercialOffers(status, partner_id, fromDate, toDate);
            return Results.Ok(offers.Select(MapOfferSummary));
        });

        app.MapPost("/api/commercial/offers", async (HttpRequest request, CommercialOfferService offers) =>
        {
            var body = await ReadBody<CreateCommercialOfferRequest>(request);
            if (body?.PartnerId is not > 0)
            {
                return Results.BadRequest(new ApiResult(false, "PARTNER_ID_REQUIRED"));
            }

            try
            {
                var (offerId, offerRef) = offers.CreateDraftOffer(new CreateCommercialOfferCommand
                {
                    PartnerId = body.PartnerId.Value,
                    PriceGroupId = body.PriceGroupId,
                    OfferRef = body.OfferRef,
                    ContactPerson = body.ContactPerson,
                    ContactPhone = body.ContactPhone,
                    ContactEmail = body.ContactEmail,
                    Currency = body.Currency,
                    ValidUntil = ParseDate(body.ValidUntil),
                    PaymentTerms = body.PaymentTerms,
                    DeliveryTerms = body.DeliveryTerms,
                    Comment = body.Comment,
                    ManagerName = body.ManagerName
                });
                return Results.Ok(new { ok = true, offer_id = offerId, offer_ref = offerRef });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ApiResult(false, ex.Message));
            }
        });

        app.MapGet("/api/commercial/offers/{id:long}", (long id, ICommercialDataStore store) =>
        {
            var offer = store.GetCommercialOffer(id);
            if (offer == null)
            {
                return Results.NotFound(new ApiResult(false, "OFFER_NOT_FOUND"));
            }

            return Results.Ok(MapOfferDetails(offer, store));
        });

        app.MapPost("/api/commercial/offers/{id:long}", async (long id, HttpRequest request, ICommercialDataStore store, IDataStore data) =>
        {
            var offer = store.GetCommercialOffer(id);
            if (offer == null)
            {
                return Results.NotFound(new ApiResult(false, "OFFER_NOT_FOUND"));
            }

            if (offer.Status != CommercialOfferStatus.Draft)
            {
                return Results.BadRequest(new ApiResult(false, "OFFER_NOT_EDITABLE"));
            }

            var body = await ReadBody<UpdateCommercialOfferRequest>(request);
            if (body == null)
            {
                return Results.BadRequest(new ApiResult(false, "INVALID_BODY"));
            }

            if (body.PartnerId is > 0 && data.GetPartner(body.PartnerId.Value) == null)
            {
                return Results.BadRequest(new ApiResult(false, "PARTNER_NOT_FOUND"));
            }

            if (body.PriceGroupId is > 0 && store.GetPriceGroup(body.PriceGroupId.Value) == null)
            {
                return Results.BadRequest(new ApiResult(false, "PRICE_GROUP_NOT_FOUND"));
            }

            store.UpdateCommercialOffer(new CommercialOffer
            {
                Id = offer.Id,
                OfferRef = offer.OfferRef,
                PartnerId = body.PartnerId ?? offer.PartnerId,
                ContactPerson = body.ContactPerson ?? offer.ContactPerson,
                ContactPhone = body.ContactPhone ?? offer.ContactPhone,
                ContactEmail = body.ContactEmail ?? offer.ContactEmail,
                PriceGroupId = body.PriceGroupId ?? offer.PriceGroupId,
                Status = offer.Status,
                Currency = body.Currency ?? offer.Currency,
                ValidUntil = body.ValidUntil != null ? ParseDate(body.ValidUntil) : offer.ValidUntil,
                PaymentTerms = body.PaymentTerms ?? offer.PaymentTerms,
                DeliveryTerms = body.DeliveryTerms ?? offer.DeliveryTerms,
                Comment = body.Comment ?? offer.Comment,
                ManagerName = body.ManagerName ?? offer.ManagerName,
                Subtotal = offer.Subtotal,
                DiscountTotal = offer.DiscountTotal,
                Total = offer.Total,
                NextFollowUpAt = body.NextFollowUpAt ?? offer.NextFollowUpAt,
                ConvertedOrderId = offer.ConvertedOrderId,
                CreatedAt = offer.CreatedAt,
                UpdatedAt = DateTime.UtcNow,
                SentAt = offer.SentAt,
                ClosedAt = offer.ClosedAt
            });
            return Results.Ok(new ApiResult(true));
        });

        app.MapDelete("/api/commercial/offers/{id:long}", (long id, ICommercialDataStore store) =>
        {
            var offer = store.GetCommercialOffer(id);
            if (offer == null)
            {
                return Results.NotFound(new ApiResult(false, "OFFER_NOT_FOUND"));
            }

            if (offer.Status != CommercialOfferStatus.Draft)
            {
                return Results.BadRequest(new ApiResult(false, "OFFER_NOT_EDITABLE"));
            }

            store.DeleteCommercialOffer(id);
            return Results.Ok(new ApiResult(true));
        });

        app.MapPost("/api/commercial/offers/{id:long}/lines", async (long id, HttpRequest request, CommercialOfferService offers) =>
        {
            var body = await ReadBody<AddCommercialOfferLineRequest>(request);
            if (body?.ItemId is not > 0 || body.Qty is not > 0)
            {
                return Results.BadRequest(new ApiResult(false, "INVALID_BODY"));
            }

            try
            {
                var line = offers.AddLineFromQuote(id, body.ItemId.Value, body.Qty.Value, body.UomCode, body.ManualDiscountPercent ?? 0m, body.Comment);
                return Results.Ok(new { ok = true, line = MapLine(line) });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ApiResult(false, ex.Message));
            }
        });

        app.MapDelete("/api/commercial/offers/{id:long}/lines/{lineId:long}", (long id, long lineId, ICommercialDataStore store, CommercialOfferService offers) =>
        {
            var offer = store.GetCommercialOffer(id);
            if (offer == null)
            {
                return Results.NotFound(new ApiResult(false, "OFFER_NOT_FOUND"));
            }

            if (offer.Status != CommercialOfferStatus.Draft)
            {
                return Results.BadRequest(new ApiResult(false, "OFFER_NOT_EDITABLE"));
            }

            store.DeleteCommercialOfferLine(lineId);
            offers.RefreshOfferTotals(id);
            return Results.Ok(new ApiResult(true));
        });

        app.MapPost("/api/commercial/offers/{id:long}/recalculate-prices", (long id, CommercialOfferService offers) =>
        {
            try
            {
                offers.RecalculateOfferPrices(id);
                return Results.Ok(new ApiResult(true));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ApiResult(false, ex.Message));
            }
        });

        app.MapPost("/api/commercial/offers/{id:long}/status", async (long id, HttpRequest request, CommercialOfferService offers) =>
        {
            var body = await ReadBody<ChangeCommercialOfferStatusRequest>(request);
            if (string.IsNullOrWhiteSpace(body?.Status))
            {
                return Results.BadRequest(new ApiResult(false, "INVALID_STATUS"));
            }

            var status = CommercialOfferStatusMapper.FromCode(body.Status);
            if (!status.HasValue)
            {
                return Results.BadRequest(new ApiResult(false, "INVALID_STATUS"));
            }

            try
            {
                var offer = offers.ChangeStatus(id, status.Value, body.Comment, body.ChangedBy);
                return Results.Ok(new { ok = true, status = CommercialOfferStatusMapper.ToCode(offer.Status) });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ApiResult(false, ex.Message));
            }
        });

        app.MapPost("/api/commercial/offers/{id:long}/duplicate", (long id, CommercialOfferService offers) =>
        {
            try
            {
                var duplicate = offers.Duplicate(id);
                return Results.Ok(new { ok = true, offer_id = duplicate.Id, offer_ref = duplicate.OfferRef });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ApiResult(false, ex.Message));
            }
        });

        app.MapPost("/api/commercial/offers/{id:long}/create-order", async (long id, HttpRequest request, CommercialOfferService offers, IDataStore data) =>
        {
            var body = await ReadBody<CreateOrderFromOfferRequest>(request);
            if (string.IsNullOrWhiteSpace(body?.OrderRef))
            {
                return Results.BadRequest(new ApiResult(false, "INVALID_ORDER_REF"));
            }

            DateTime? dueDate = null;
            if (!string.IsNullOrWhiteSpace(body.DueDate)
                && DateTime.TryParseExact(body.DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                dueDate = parsed.Date;
            }

            try
            {
                var orderId = offers.CreateCustomerOrderFromWonOffer(id, body.OrderRef.Trim(), dueDate, body.Comment);
                return Results.Ok(new { ok = true, order_id = orderId });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ApiResult(false, ex.Message));
            }
        });
    }

    internal static object MapOfferSummary(CommercialOffer offer) => new
    {
        id = offer.Id,
        offer_ref = offer.OfferRef,
        partner_id = offer.PartnerId,
        partner_name = offer.PartnerName,
        price_group_id = offer.PriceGroupId,
        status = CommercialOfferStatusMapper.ToCode(offer.Status),
        status_display = offer.StatusDisplay,
        currency = offer.Currency,
        valid_until = offer.ValidUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        subtotal = offer.Subtotal,
        discount_total = offer.DiscountTotal,
        total = offer.Total,
        next_follow_up_at = offer.NextFollowUpAt,
        manager_name = offer.ManagerName,
        converted_order_id = offer.ConvertedOrderId,
        created_at = offer.CreatedAt,
        updated_at = offer.UpdatedAt
    };

    private static object MapOfferDetails(CommercialOffer offer, ICommercialDataStore store) => new
    {
        offer = MapOfferSummary(offer),
        lines = store.GetCommercialOfferLines(offer.Id).Select(MapLine),
        status_history = store.GetCommercialOfferStatusHistory(offer.Id).Select(h => new
        {
            id = h.Id,
            old_status = h.OldStatus.HasValue ? CommercialOfferStatusMapper.ToCode(h.OldStatus.Value) : null,
            new_status = CommercialOfferStatusMapper.ToCode(h.NewStatus),
            comment = h.Comment,
            changed_at = h.ChangedAt,
            changed_by = h.ChangedBy
        }),
        files = store.GetGeneratedDocuments("COMMERCIAL_OFFER", offer.Id).Select(d => new
        {
            id = d.Id,
            output_format = d.OutputFormat,
            file_path = d.FilePath,
            created_at = d.CreatedAt
        })
    };

    internal static object MapLine(CommercialOfferLine line) => new
    {
        id = line.Id,
        line_no = line.LineNo,
        item_id = line.ItemId,
        item_name = line.ItemName,
        qty = line.Qty,
        uom_code = line.UomCode,
        base_price = line.BasePrice,
        volume_discount_percent = line.VolumeDiscountPercent,
        manual_discount_percent = line.ManualDiscountPercent,
        final_discount_percent = line.FinalDiscountPercent,
        final_price = line.FinalPrice,
        line_total = line.LineTotal,
        comment = line.Comment
    };

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static async Task<T?> ReadBody<T>(HttpRequest request)
    {
        try
        {
            return await request.ReadFromJsonAsync<T>();
        }
        catch
        {
            return default;
        }
    }

    private sealed class CreateCommercialOfferRequest
    {
        [JsonPropertyName("offer_ref")] public string? OfferRef { get; init; }
        [JsonPropertyName("partner_id")] public long? PartnerId { get; init; }
        [JsonPropertyName("contact_person")] public string? ContactPerson { get; init; }
        [JsonPropertyName("contact_phone")] public string? ContactPhone { get; init; }
        [JsonPropertyName("contact_email")] public string? ContactEmail { get; init; }
        [JsonPropertyName("price_group_id")] public long? PriceGroupId { get; init; }
        [JsonPropertyName("currency")] public string? Currency { get; init; }
        [JsonPropertyName("valid_until")] public string? ValidUntil { get; init; }
        [JsonPropertyName("payment_terms")] public string? PaymentTerms { get; init; }
        [JsonPropertyName("delivery_terms")] public string? DeliveryTerms { get; init; }
        [JsonPropertyName("comment")] public string? Comment { get; init; }
        [JsonPropertyName("manager_name")] public string? ManagerName { get; init; }
    }

    private sealed class UpdateCommercialOfferRequest
    {
        [JsonPropertyName("partner_id")] public long? PartnerId { get; init; }
        [JsonPropertyName("contact_person")] public string? ContactPerson { get; init; }
        [JsonPropertyName("contact_phone")] public string? ContactPhone { get; init; }
        [JsonPropertyName("contact_email")] public string? ContactEmail { get; init; }
        [JsonPropertyName("price_group_id")] public long? PriceGroupId { get; init; }
        [JsonPropertyName("currency")] public string? Currency { get; init; }
        [JsonPropertyName("valid_until")] public string? ValidUntil { get; init; }
        [JsonPropertyName("payment_terms")] public string? PaymentTerms { get; init; }
        [JsonPropertyName("delivery_terms")] public string? DeliveryTerms { get; init; }
        [JsonPropertyName("comment")] public string? Comment { get; init; }
        [JsonPropertyName("manager_name")] public string? ManagerName { get; init; }
        [JsonPropertyName("next_follow_up_at")] public DateTime? NextFollowUpAt { get; init; }
    }

    private sealed class AddCommercialOfferLineRequest
    {
        [JsonPropertyName("item_id")] public long? ItemId { get; init; }
        [JsonPropertyName("qty")] public double? Qty { get; init; }
        [JsonPropertyName("uom_code")] public string? UomCode { get; init; }
        [JsonPropertyName("manual_discount_percent")] public decimal? ManualDiscountPercent { get; init; }
        [JsonPropertyName("comment")] public string? Comment { get; init; }
    }

    private sealed class ChangeCommercialOfferStatusRequest
    {
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("comment")] public string? Comment { get; init; }
        [JsonPropertyName("changed_by")] public string? ChangedBy { get; init; }
    }

    private sealed class CreateOrderFromOfferRequest
    {
        [JsonPropertyName("order_ref")] public string? OrderRef { get; init; }
        [JsonPropertyName("due_date")] public string? DueDate { get; init; }
        [JsonPropertyName("comment")] public string? Comment { get; init; }
    }
}

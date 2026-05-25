using FlowStock.Core.Abstractions;
using FlowStock.Core.Commercial;
using FlowStock.Core.Models;

namespace FlowStock.Core.Commercial;

public sealed class CommercialOfferService
{
    private readonly ICommercialDataStore _commercial;
    private readonly IDataStore _data;
    private readonly CommercialPricingService _pricing;

    public CommercialOfferService(
        ICommercialDataStore commercial,
        IDataStore data,
        CommercialPricingService pricing)
    {
        _commercial = commercial;
        _data = data;
        _pricing = pricing;
    }

    public (long OfferId, string OfferRef) CreateDraftOffer(CreateCommercialOfferCommand command)
    {
        if (command.PartnerId <= 0)
        {
            throw new InvalidOperationException("PARTNER_ID_REQUIRED");
        }

        if (_data.GetPartner(command.PartnerId) == null)
        {
            throw new InvalidOperationException("PARTNER_NOT_FOUND");
        }

        var asOfDate = DateOnly.FromDateTime(DateTime.Today);
        var priceGroupId = command.PriceGroupId is > 0
            ? command.PriceGroupId.Value
            : _pricing.ResolvePriceGroupId(command.PartnerId, asOfDate, null);

        if (priceGroupId is not > 0 || _commercial.GetPriceGroup(priceGroupId.Value) == null)
        {
            throw new InvalidOperationException("PRICE_GROUP_NOT_FOUND");
        }

        var now = DateTime.UtcNow;
        var year = now.Year;
        var seq = _commercial.GetMaxCommercialOfferRefSequenceByYear(year) + 1;
        var offerRef = string.IsNullOrWhiteSpace(command.OfferRef)
            ? $"CO-{year}-{seq:D6}"
            : command.OfferRef.Trim();

        var id = _commercial.AddCommercialOffer(new CommercialOffer
        {
            OfferRef = offerRef,
            PartnerId = command.PartnerId,
            ContactPerson = command.ContactPerson,
            ContactPhone = command.ContactPhone,
            ContactEmail = command.ContactEmail,
            PriceGroupId = priceGroupId.Value,
            Status = CommercialOfferStatus.Draft,
            Currency = string.IsNullOrWhiteSpace(command.Currency) ? "RUB" : command.Currency.Trim(),
            ValidUntil = command.ValidUntil,
            PaymentTerms = command.PaymentTerms,
            DeliveryTerms = command.DeliveryTerms,
            Comment = command.Comment,
            ManagerName = command.ManagerName,
            CreatedAt = now,
            UpdatedAt = now
        });

        _commercial.AddCommercialOfferStatusHistory(new CommercialOfferStatusHistoryEntry
        {
            OfferId = id,
            OldStatus = null,
            NewStatus = CommercialOfferStatus.Draft,
            ChangedAt = now
        });

        return (id, offerRef);
    }

    public CommercialOfferLine AddLineFromQuote(
        long offerId,
        long itemId,
        double qty,
        string? uomCode,
        decimal manualDiscountPercent,
        string? comment)
    {
        var offer = RequireOffer(offerId);
        EnsureEditable(offer);

        var quote = _pricing.Quote(new PricingQuoteRequest
        {
            ItemId = itemId,
            PartnerId = offer.PartnerId,
            Qty = qty,
            AsOfDate = DateOnly.FromDateTime(DateTime.Today),
            ManualDiscountPercent = manualDiscountPercent,
            PriceGroupOverrideId = offer.PriceGroupId
        });

        if (!quote.IsSuccess)
        {
            throw new InvalidOperationException(quote.ErrorCode ?? "PRICE_NOT_FOUND");
        }

        if (quote.GroupPrice <= 0m && manualDiscountPercent <= 0m)
        {
            throw new InvalidOperationException("PRICE_IS_ZERO");
        }

        var lines = _commercial.GetCommercialOfferLines(offerId);
        var lineNo = lines.Count == 0 ? 1 : lines.Max(l => l.LineNo) + 1;
        var line = new CommercialOfferLine
        {
            OfferId = offerId,
            LineNo = lineNo,
            ItemId = itemId,
            Qty = qty,
            UomCode = uomCode,
            BasePrice = quote.BasePrice,
            VolumeDiscountPercent = quote.VolumeDiscountPercent,
            ManualDiscountPercent = quote.ManualDiscountPercent,
            FinalDiscountPercent = quote.FinalDiscountPercent,
            FinalPrice = quote.FinalPrice,
            LineTotal = quote.LineTotal,
            Comment = comment
        };

        var lineId = _commercial.AddCommercialOfferLine(line);
        RecalculateOfferTotals(offerId);
        return _commercial.GetCommercialOfferLines(offerId).First(l => l.Id == lineId);
    }

    public void RefreshOfferTotals(long offerId) => RecalculateOfferTotals(offerId);

    public void RecalculateOfferPrices(long offerId)
    {
        var offer = RequireOffer(offerId);
        EnsureEditable(offer);

        foreach (var line in _commercial.GetCommercialOfferLines(offerId))
        {
            var quote = _pricing.Quote(new PricingQuoteRequest
            {
                ItemId = line.ItemId,
                PartnerId = offer.PartnerId,
                Qty = line.Qty,
                AsOfDate = DateOnly.FromDateTime(DateTime.Today),
                ManualDiscountPercent = line.ManualDiscountPercent,
                PriceGroupOverrideId = offer.PriceGroupId
            });

            if (!quote.IsSuccess)
            {
                throw new InvalidOperationException($"{quote.ErrorCode}:{line.ItemId}");
            }

            _commercial.UpdateCommercialOfferLine(new CommercialOfferLine
            {
                Id = line.Id,
                OfferId = line.OfferId,
                LineNo = line.LineNo,
                ItemId = line.ItemId,
                Qty = line.Qty,
                UomCode = line.UomCode,
                BasePrice = quote.BasePrice,
                VolumeDiscountPercent = quote.VolumeDiscountPercent,
                ManualDiscountPercent = quote.ManualDiscountPercent,
                FinalDiscountPercent = quote.FinalDiscountPercent,
                FinalPrice = quote.FinalPrice,
                LineTotal = quote.LineTotal,
                Comment = line.Comment
            });
        }

        RecalculateOfferTotals(offerId);
    }

    public CommercialOffer ChangeStatus(long offerId, CommercialOfferStatus newStatus, string? comment, string? changedBy)
    {
        var offer = RequireOffer(offerId);
        if (!CommercialOfferStatusMapper.CanTransition(offer.Status, newStatus))
        {
            throw new InvalidOperationException("INVALID_STATUS_TRANSITION");
        }

        if (newStatus is CommercialOfferStatus.Sent or CommercialOfferStatus.Won)
        {
            EnsureHasLines(offerId);
        }

        var now = DateTime.UtcNow;
        var sentAt = offer.SentAt;
        if (newStatus == CommercialOfferStatus.Sent && !sentAt.HasValue)
        {
            sentAt = now;
        }

        DateTime? closedAt = offer.ClosedAt;
        if (CommercialOfferStatusMapper.IsTerminal(newStatus))
        {
            closedAt = now;
        }

        _commercial.UpdateCommercialOffer(new CommercialOffer
        {
            Id = offer.Id,
            OfferRef = offer.OfferRef,
            PartnerId = offer.PartnerId,
            ContactPerson = offer.ContactPerson,
            ContactPhone = offer.ContactPhone,
            ContactEmail = offer.ContactEmail,
            PriceGroupId = offer.PriceGroupId,
            Status = newStatus,
            Currency = offer.Currency,
            ValidUntil = offer.ValidUntil,
            PaymentTerms = offer.PaymentTerms,
            DeliveryTerms = offer.DeliveryTerms,
            Comment = offer.Comment,
            ManagerName = offer.ManagerName,
            Subtotal = offer.Subtotal,
            DiscountTotal = offer.DiscountTotal,
            Total = offer.Total,
            NextFollowUpAt = offer.NextFollowUpAt,
            ConvertedOrderId = offer.ConvertedOrderId,
            CreatedAt = offer.CreatedAt,
            UpdatedAt = now,
            SentAt = sentAt,
            ClosedAt = closedAt
        });

        _commercial.AddCommercialOfferStatusHistory(new CommercialOfferStatusHistoryEntry
        {
            OfferId = offerId,
            OldStatus = offer.Status,
            NewStatus = newStatus,
            Comment = comment,
            ChangedAt = now,
            ChangedBy = changedBy
        });

        return _commercial.GetCommercialOffer(offerId)!;
    }

    public CommercialOffer Duplicate(long offerId)
    {
        var source = RequireOffer(offerId);
        var now = DateTime.UtcNow;
        var year = now.Year;
        var seq = _commercial.GetMaxCommercialOfferRefSequenceByYear(year) + 1;
        var offerRef = $"CO-{year}-{seq:D6}";

        var newOfferId = _commercial.AddCommercialOffer(new CommercialOffer
        {
            OfferRef = offerRef,
            PartnerId = source.PartnerId,
            ContactPerson = source.ContactPerson,
            ContactPhone = source.ContactPhone,
            ContactEmail = source.ContactEmail,
            PriceGroupId = source.PriceGroupId,
            Status = CommercialOfferStatus.Draft,
            Currency = source.Currency,
            ValidUntil = source.ValidUntil,
            PaymentTerms = source.PaymentTerms,
            DeliveryTerms = source.DeliveryTerms,
            Comment = source.Comment,
            ManagerName = source.ManagerName,
            Subtotal = source.Subtotal,
            DiscountTotal = source.DiscountTotal,
            Total = source.Total,
            CreatedAt = now,
            UpdatedAt = now
        });

        foreach (var line in _commercial.GetCommercialOfferLines(offerId))
        {
            _commercial.AddCommercialOfferLine(new CommercialOfferLine
            {
                OfferId = newOfferId,
                LineNo = line.LineNo,
                ItemId = line.ItemId,
                Qty = line.Qty,
                UomCode = line.UomCode,
                BasePrice = line.BasePrice,
                VolumeDiscountPercent = line.VolumeDiscountPercent,
                ManualDiscountPercent = line.ManualDiscountPercent,
                FinalDiscountPercent = line.FinalDiscountPercent,
                FinalPrice = line.FinalPrice,
                LineTotal = line.LineTotal,
                Comment = line.Comment
            });
        }

        return _commercial.GetCommercialOffer(newOfferId)!;
    }

    public long CreateCustomerOrderFromWonOffer(long offerId, string orderRef, DateTime? dueDate, string? comment)
    {
        var offer = RequireOffer(offerId);
        if (offer.Status != CommercialOfferStatus.Won)
        {
            throw new InvalidOperationException("OFFER_NOT_WON");
        }

        if (offer.ConvertedOrderId.HasValue)
        {
            return offer.ConvertedOrderId.Value;
        }

        EnsureHasLines(offerId);

        var partner = _data.GetPartner(offer.PartnerId)
            ?? throw new InvalidOperationException("PARTNER_NOT_FOUND");

        var order = new Order
        {
            OrderRef = orderRef,
            Type = OrderType.Customer,
            PartnerId = offer.PartnerId,
            DueDate = dueDate,
            Status = OrderStatus.Accepted,
            Comment = string.IsNullOrWhiteSpace(comment)
                ? $"Из КП {offer.OfferRef}"
                : comment,
            CreatedAt = DateTime.Now
        };

        var orderId = _data.AddOrder(order);
        foreach (var line in _commercial.GetCommercialOfferLines(offerId))
        {
            _data.AddOrderLine(new OrderLine
            {
                OrderId = orderId,
                ItemId = line.ItemId,
                QtyOrdered = line.Qty
            });
        }

        _commercial.SetOrderCommercialOfferId(orderId, offerId);
        _commercial.UpdateCommercialOffer(new CommercialOffer
        {
            Id = offer.Id,
            OfferRef = offer.OfferRef,
            PartnerId = offer.PartnerId,
            ContactPerson = offer.ContactPerson,
            ContactPhone = offer.ContactPhone,
            ContactEmail = offer.ContactEmail,
            PriceGroupId = offer.PriceGroupId,
            Status = offer.Status,
            Currency = offer.Currency,
            ValidUntil = offer.ValidUntil,
            PaymentTerms = offer.PaymentTerms,
            DeliveryTerms = offer.DeliveryTerms,
            Comment = offer.Comment,
            ManagerName = offer.ManagerName,
            Subtotal = offer.Subtotal,
            DiscountTotal = offer.DiscountTotal,
            Total = offer.Total,
            NextFollowUpAt = offer.NextFollowUpAt,
            ConvertedOrderId = orderId,
            CreatedAt = offer.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
            SentAt = offer.SentAt,
            ClosedAt = offer.ClosedAt
        });

        return orderId;
    }

    private void RecalculateOfferTotals(long offerId)
    {
        var offer = RequireOffer(offerId);
        var lines = _commercial.GetCommercialOfferLines(offerId);
        var subtotal = lines.Sum(l => CommercialPricingService.RoundMoney(l.BasePrice * (decimal)l.Qty));
        var total = lines.Sum(l => l.LineTotal);
        var discountTotal = subtotal - total;
        if (discountTotal < 0m)
        {
            discountTotal = 0m;
        }

        _commercial.UpdateCommercialOffer(new CommercialOffer
        {
            Id = offer.Id,
            OfferRef = offer.OfferRef,
            PartnerId = offer.PartnerId,
            ContactPerson = offer.ContactPerson,
            ContactPhone = offer.ContactPhone,
            ContactEmail = offer.ContactEmail,
            PriceGroupId = offer.PriceGroupId,
            Status = offer.Status,
            Currency = offer.Currency,
            ValidUntil = offer.ValidUntil,
            PaymentTerms = offer.PaymentTerms,
            DeliveryTerms = offer.DeliveryTerms,
            Comment = offer.Comment,
            ManagerName = offer.ManagerName,
            Subtotal = subtotal,
            DiscountTotal = discountTotal,
            Total = total,
            NextFollowUpAt = offer.NextFollowUpAt,
            ConvertedOrderId = offer.ConvertedOrderId,
            CreatedAt = offer.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
            SentAt = offer.SentAt,
            ClosedAt = offer.ClosedAt
        });
    }

    private CommercialOffer RequireOffer(long offerId)
    {
        return _commercial.GetCommercialOffer(offerId)
            ?? throw new InvalidOperationException("OFFER_NOT_FOUND");
    }

    private static void EnsureEditable(CommercialOffer offer)
    {
        if (offer.Status != CommercialOfferStatus.Draft)
        {
            throw new InvalidOperationException("OFFER_NOT_EDITABLE");
        }
    }

    private void EnsureHasLines(long offerId)
    {
        if (_commercial.GetCommercialOfferLines(offerId).Count == 0)
        {
            throw new InvalidOperationException("OFFER_LINES_REQUIRED");
        }
    }
}

namespace FlowStock.Core.Commercial;

public sealed class CommercialOffer
{
    public long Id { get; init; }
    public string OfferRef { get; init; } = string.Empty;
    public long PartnerId { get; init; }
    public string? PartnerName { get; init; }
    public string? PartnerCode { get; init; }
    public string? ContactPerson { get; init; }
    public string? ContactPhone { get; init; }
    public string? ContactEmail { get; init; }
    public long PriceGroupId { get; init; }
    public string? PriceGroupName { get; init; }
    public CommercialOfferStatus Status { get; init; }
    public string Currency { get; init; } = "RUB";
    public DateOnly? ValidUntil { get; init; }
    public string? PaymentTerms { get; init; }
    public string? DeliveryTerms { get; init; }
    public string? Comment { get; init; }
    public string? ManagerName { get; init; }
    public decimal Subtotal { get; init; }
    public decimal DiscountTotal { get; init; }
    public decimal Total { get; init; }
    public DateTime? NextFollowUpAt { get; init; }
    public long? ConvertedOrderId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? SentAt { get; init; }
    public DateTime? ClosedAt { get; init; }

    public string StatusDisplay => CommercialOfferStatusMapper.ToDisplayName(Status);
}

public sealed class CommercialOfferLine
{
    public long Id { get; init; }
    public long OfferId { get; init; }
    public int LineNo { get; init; }
    public long ItemId { get; init; }
    public string? ItemName { get; init; }
    public string? ItemBarcode { get; init; }
    public string? ItemGtin { get; init; }
    public string? ItemBrand { get; init; }
    public string? ItemVolume { get; init; }
    public double Qty { get; init; }
    public string? UomCode { get; init; }
    public decimal BasePrice { get; init; }
    public decimal VolumeDiscountPercent { get; init; }
    public decimal ManualDiscountPercent { get; init; }
    public decimal FinalDiscountPercent { get; init; }
    public decimal FinalPrice { get; init; }
    public decimal LineTotal { get; init; }
    public string? Comment { get; init; }
}

public sealed class CommercialOfferStatusHistoryEntry
{
    public long Id { get; init; }
    public long OfferId { get; init; }
    public CommercialOfferStatus? OldStatus { get; init; }
    public CommercialOfferStatus NewStatus { get; init; }
    public string? Comment { get; init; }
    public DateTime ChangedAt { get; init; }
    public string? ChangedBy { get; init; }
}

public sealed class CommercialTemplate
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public CommercialTemplateType TemplateType { get; init; }
    public string SourceFormat { get; init; } = "DOCX";
    public string FilePath { get; init; } = string.Empty;
    public string? FileHash { get; init; }
    public int VersionNo { get; init; } = 1;
    public bool IsDefault { get; init; }
    public bool IsActive { get; init; } = true;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed class GeneratedDocument
{
    public long Id { get; init; }
    public long? TemplateId { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public long SourceId { get; init; }
    public string OutputFormat { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string? FileHash { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class PriceTagBatch
{
    public long Id { get; init; }
    public long PriceGroupId { get; init; }
    public long? TemplateId { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? Comment { get; init; }
}

public sealed class PriceTagBatchLine
{
    public long Id { get; init; }
    public long BatchId { get; init; }
    public long ItemId { get; init; }
    public string? ItemName { get; init; }
    public int Copies { get; init; } = 1;
    public decimal Price { get; init; }
}

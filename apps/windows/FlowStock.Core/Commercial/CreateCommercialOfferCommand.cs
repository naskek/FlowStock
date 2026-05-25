namespace FlowStock.Core.Commercial;

public sealed record CreateCommercialOfferCommand
{
    public long PartnerId { get; init; }
    public long? PriceGroupId { get; init; }
    public string? OfferRef { get; init; }
    public string? ContactPerson { get; init; }
    public string? ContactPhone { get; init; }
    public string? ContactEmail { get; init; }
    public string? Currency { get; init; }
    public DateOnly? ValidUntil { get; init; }
    public string? PaymentTerms { get; init; }
    public string? DeliveryTerms { get; init; }
    public string? Comment { get; init; }
    public string? ManagerName { get; init; }
}

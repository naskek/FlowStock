namespace FlowStock.Core.Commercial;

public enum CommercialOfferStatus
{
    Draft,
    Sent,
    WaitingReply,
    Rejected,
    Won,
    Expired,
    Cancelled
}

public static class CommercialOfferStatusMapper
{
    public static string ToCode(CommercialOfferStatus status) => status switch
    {
        CommercialOfferStatus.Draft => "DRAFT",
        CommercialOfferStatus.Sent => "SENT",
        CommercialOfferStatus.WaitingReply => "WAITING_REPLY",
        CommercialOfferStatus.Rejected => "REJECTED",
        CommercialOfferStatus.Won => "WON",
        CommercialOfferStatus.Expired => "EXPIRED",
        CommercialOfferStatus.Cancelled => "CANCELLED",
        _ => "DRAFT"
    };

    public static CommercialOfferStatus? FromCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "DRAFT" => CommercialOfferStatus.Draft,
        "SENT" => CommercialOfferStatus.Sent,
        "WAITING_REPLY" => CommercialOfferStatus.WaitingReply,
        "REJECTED" => CommercialOfferStatus.Rejected,
        "WON" => CommercialOfferStatus.Won,
        "EXPIRED" => CommercialOfferStatus.Expired,
        "CANCELLED" => CommercialOfferStatus.Cancelled,
        _ => null
    };

    public static string ToDisplayName(CommercialOfferStatus status) => status switch
    {
        CommercialOfferStatus.Draft => "Черновик",
        CommercialOfferStatus.Sent => "Отправлено",
        CommercialOfferStatus.WaitingReply => "Ожидаем ответа",
        CommercialOfferStatus.Rejected => "Отклонено",
        CommercialOfferStatus.Won => "Продано",
        CommercialOfferStatus.Expired => "Истек срок",
        CommercialOfferStatus.Cancelled => "Отменено",
        _ => status.ToString()
    };

    public static bool IsTerminal(CommercialOfferStatus status) =>
        status is CommercialOfferStatus.Rejected
            or CommercialOfferStatus.Won
            or CommercialOfferStatus.Expired
            or CommercialOfferStatus.Cancelled;

    public static bool CanTransition(CommercialOfferStatus from, CommercialOfferStatus to)
    {
        if (from == to)
        {
            return true;
        }

        return from switch
        {
            CommercialOfferStatus.Draft => to is CommercialOfferStatus.Sent or CommercialOfferStatus.Cancelled,
            CommercialOfferStatus.Sent => to is CommercialOfferStatus.WaitingReply
                or CommercialOfferStatus.Rejected
                or CommercialOfferStatus.Won
                or CommercialOfferStatus.Expired
                or CommercialOfferStatus.Cancelled,
            CommercialOfferStatus.WaitingReply => to is CommercialOfferStatus.Rejected
                or CommercialOfferStatus.Won
                or CommercialOfferStatus.Expired
                or CommercialOfferStatus.Cancelled,
            _ => false
        };
    }
}

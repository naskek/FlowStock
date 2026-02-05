using System;

namespace FlowStock.Core.Models;

public sealed class Doc
{
    public long Id { get; init; }
    public string DocRef { get; init; } = string.Empty;
    public DocType Type { get; init; }
    public DocStatus Status { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ClosedAt { get; init; }
    public long? PartnerId { get; init; }
    public long? OrderId { get; init; }
    public string? OrderRef { get; init; }
    public string? ShippingRef { get; init; }
    public string? Comment { get; init; }
    public string? PartnerName { get; init; }
    public string? PartnerCode { get; init; }
    public int LineCount { get; init; }
    public string? SourceDeviceId { get; init; }

    public string TypeDisplay => DocTypeMapper.ToDisplayName(Type);

    public string StatusDisplay
    {
        get
        {
            if (Status == DocStatus.Draft
                && (IsTsdSource(Comment, SourceDeviceId)))
            {
                var deviceLabel = string.IsNullOrWhiteSpace(SourceDeviceId) ? null : SourceDeviceId.Trim();
                var prefix = deviceLabel == null
                    ? "Принято с ТСД"
                    : $"Принято с ТСД ({deviceLabel})";
                var tsdStatus = LineCount > 0 ? "Наполнен" : "Черновик";
                return $"{prefix} - {tsdStatus}";
            }

            return DocTypeMapper.StatusToDisplayName(Status);
        }
    }

    private static bool IsTsdSource(string? comment, string? sourceDeviceId)
    {
        if (!string.IsNullOrWhiteSpace(sourceDeviceId))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(comment)
               && comment.StartsWith("TSD", StringComparison.OrdinalIgnoreCase);
    }

    public string PartnerDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(PartnerCode) && !string.IsNullOrWhiteSpace(PartnerName))
            {
                return $"{PartnerCode} - {PartnerName}";
            }

            if (!string.IsNullOrWhiteSpace(PartnerCode))
            {
                return PartnerCode;
            }

            return PartnerName ?? string.Empty;
        }
    }

    public string HuDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ShippingRef))
            {
                return string.Empty;
            }

            if (!ShippingRef.StartsWith("HU-", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return $"HU: {ShippingRef}";
        }
    }
}


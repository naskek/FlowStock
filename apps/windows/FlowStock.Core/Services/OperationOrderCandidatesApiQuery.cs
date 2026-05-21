using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class OperationOrderCandidatesApiQuery
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 50;

    public static string BuildPath(DocType docType, string? search, int limit)
    {
        if (docType is not (DocType.ProductionReceipt or DocType.Outbound))
        {
            throw new ArgumentOutOfRangeException(nameof(docType), docType, "Only operation doc types are supported.");
        }

        var effectiveLimit = Math.Clamp(limit, 1, MaxLimit);
        var query = new List<string>
        {
            $"doc_type={Uri.EscapeDataString(DocTypeMapper.ToOpString(docType))}",
            $"limit={effectiveLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Add($"q={Uri.EscapeDataString(search.Trim())}");
        }

        return "/api/orders/candidates?" + string.Join("&", query);
    }
}

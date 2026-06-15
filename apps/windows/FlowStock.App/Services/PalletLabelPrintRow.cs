using System.Globalization;

namespace FlowStock.App;

public sealed class PalletLabelPrintRow
{
    public long PalletId { get; init; }
    public long OrderId { get; init; }
    public string OrderRef { get; init; } = string.Empty;
    public string ClientName { get; init; } = string.Empty;
    public string PrdRef { get; init; } = string.Empty;
    public string HuCode { get; init; } = string.Empty;
    public string ItemName { get; init; } = string.Empty;
    public string Brand { get; init; } = string.Empty;
    public double Qty { get; init; }
    public string Uom { get; init; } = "шт";
    public int PalletNo { get; init; }
    public int PalletCount { get; init; }
    public string StoragePlace { get; init; } = string.Empty;
    public DateTime? ProductionDate { get; init; }
    public string Comment { get; init; } = string.Empty;
    public bool IsMixedPallet { get; init; }
    public string Composition { get; init; } = string.Empty;
    public string Line1ItemName { get; init; } = string.Empty;
    public double Line1Qty { get; init; }
    public string Line2ItemName { get; init; } = string.Empty;
    public double Line2Qty { get; init; }
    public string Line3ItemName { get; init; } = string.Empty;
    public double Line3Qty { get; init; }
    public string Status { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> ToNamedSubStrings()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HuCode"] = HuCode,
            ["ItemName"] = ItemName,
            ["Qty"] = Qty.ToString("0.###", CultureInfo.InvariantCulture),
            ["OrderRef"] = OrderRef,
            ["ClientName"] = ClientName,
            ["PrdRef"] = PrdRef,
            ["Brand"] = Brand,
            ["Uom"] = Uom,
            ["PalletNo"] = PalletNo > 0 ? PalletNo.ToString(CultureInfo.InvariantCulture) : string.Empty,
            ["PalletCount"] = PalletCount > 0 ? PalletCount.ToString(CultureInfo.InvariantCulture) : string.Empty,
            ["StoragePlace"] = StoragePlace,
            ["ProductionDate"] = ProductionDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            ["Comment"] = Comment,
            ["IsMixedPallet"] = IsMixedPallet ? "1" : "0",
            ["Composition"] = Composition,
            ["Line1ItemName"] = Line1ItemName,
            ["Line1Qty"] = Line1Qty > 0 ? Line1Qty.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty,
            ["Line2ItemName"] = Line2ItemName,
            ["Line2Qty"] = Line2Qty > 0 ? Line2Qty.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty,
            ["Line3ItemName"] = Line3ItemName,
            ["Line3Qty"] = Line3Qty > 0 ? Line3Qty.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty
        };
    }
}

public sealed record PalletLabelPrintResult(bool IsSuccess, string Message, int PrintedCount)
{
    public static PalletLabelPrintResult Success(int printedCount)
    {
        return new PalletLabelPrintResult(true, "Паллетные этикетки отправлены на печать", printedCount);
    }

    public static PalletLabelPrintResult Failure(string message)
    {
        return new PalletLabelPrintResult(false, message, 0);
    }
}

public interface IPalletLabelPrintService
{
    PalletLabelPrintResult Print(IReadOnlyList<PalletLabelPrintRow> rows, int? copiesOverride = null);
}

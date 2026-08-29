using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class ProductionPalletLabelFingerprint
{
    public static string Compute(ProductionPalletPrintRow row)
    {
        var lines = row.Lines.Take(3).ToArray();
        var values = new (string Name, string Value)[]
        {
            ("HuCode", row.HuCode),
            ("ItemName", row.ItemName),
            ("Qty", FormatQty(row.Qty)),
            ("OrderRef", row.OrderRef),
            ("ClientName", row.ClientName),
            ("PrdRef", row.PrdRef),
            ("Brand", row.Brand),
            ("StorageConditions", row.StorageConditions),
            ("Uom", row.Uom),
            ("PalletNo", row.PalletNo > 0 ? row.PalletNo.ToString(CultureInfo.InvariantCulture) : string.Empty),
            ("PalletCount", row.PalletCount > 0 ? row.PalletCount.ToString(CultureInfo.InvariantCulture) : string.Empty),
            ("StoragePlace", row.StoragePlace),
            ("Comment", row.Comment),
            ("IsMixedPallet", row.IsMixedPallet ? "1" : "0"),
            ("Composition", row.Composition),
            ("Line1ItemName", LineName(lines, 0)),
            ("Line1Qty", LineQty(lines, 0)),
            ("Line2ItemName", LineName(lines, 1)),
            ("Line2Qty", LineQty(lines, 1)),
            ("Line3ItemName", LineName(lines, 2)),
            ("Line3Qty", LineQty(lines, 2))
        };

        var payload = new StringBuilder(512)
            .Append(ProductionPalletLabelContract.FingerprintV1).Append('\n');
        foreach (var (name, value) in values)
        {
            var normalized = value ?? string.Empty;
            payload.Append(name).Append(':')
                .Append(normalized.Length.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(normalized).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())))
            .ToLowerInvariant();
    }

    private static string LineName(IReadOnlyList<ProductionPalletPrintLine> lines, int index) =>
        index < lines.Count ? lines[index].ItemName : string.Empty;

    private static string LineQty(IReadOnlyList<ProductionPalletPrintLine> lines, int index) =>
        index < lines.Count && lines[index].Qty > 0 ? FormatQty(lines[index].Qty) : string.Empty;

    private static string FormatQty(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}

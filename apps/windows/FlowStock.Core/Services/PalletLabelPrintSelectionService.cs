using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class PalletLabelPrintSelectionGroup
{
    public string ItemName { get; init; } = string.Empty;
    public IReadOnlyList<PalletLabelPrintSelectionRow> Rows { get; init; } = Array.Empty<PalletLabelPrintSelectionRow>();
}

public sealed class PalletLabelPrintSelectionRow
{
    public long PalletId { get; init; }
    public string HuCode { get; init; } = string.Empty;
    public double Qty { get; init; }
    public string Status { get; init; } = string.Empty;
    public bool IsSelectedByDefault { get; init; }
    public string DisplayText { get; init; } = string.Empty;
}

public static class PalletLabelPrintSelectionService
{
    public static IReadOnlyList<PalletLabelPrintSelectionGroup> BuildGroups(IReadOnlyList<ProductionPalletPrintRow> rows)
    {
        return rows
            .GroupBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PalletLabelPrintSelectionGroup
            {
                ItemName = group.Key,
                Rows = group
                    .OrderBy(row => row.PalletNo)
                    .ThenBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase)
                    .Select(MapRow)
                    .ToArray()
            })
            .ToArray();
    }

    public static IReadOnlyList<long> ResolveDefaultSelectedPalletIds(IReadOnlyList<ProductionPalletPrintRow> rows)
    {
        return rows
            .Where(row => IsReservedHuRow(row)
                          || string.Equals(row.Status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.PalletId)
            .ToArray();
    }

    public static string FormatStatusLabel(string? status)
    {
        if (string.Equals(status, ProductionPalletStatus.Filled, StringComparison.OrdinalIgnoreCase))
        {
            return "FILLED";
        }

        if (string.Equals(status, ProductionPalletStatus.Printed, StringComparison.OrdinalIgnoreCase))
        {
            return "PRINTED";
        }

        if (string.Equals(status, ProductionPalletStatus.Planned, StringComparison.OrdinalIgnoreCase))
        {
            return "PLANNED";
        }

        return string.IsNullOrWhiteSpace(status) ? "PLANNED" : status.Trim().ToUpperInvariant();
    }

    private static PalletLabelPrintSelectionRow MapRow(ProductionPalletPrintRow row)
    {
        if (IsReservedHuRow(row))
        {
            return new PalletLabelPrintSelectionRow
            {
                PalletId = row.PalletId,
                HuCode = row.HuCode,
                Qty = row.Qty,
                Status = "BOUND",
                IsSelectedByDefault = true,
                DisplayText = $"{row.HuCode}, {FormatQty(row.Qty)} шт, привязан"
            };
        }

        var statusLabel = FormatStatusLabel(row.Status);
        var printedLabel = string.Equals(statusLabel, "PRINTED", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(statusLabel, "FILLED", StringComparison.OrdinalIgnoreCase)
            ? "printed"
            : "not printed";
        return new PalletLabelPrintSelectionRow
        {
            PalletId = row.PalletId,
            HuCode = row.HuCode,
            Qty = row.Qty,
            Status = statusLabel,
            IsSelectedByDefault = string.Equals(statusLabel, "PLANNED", StringComparison.OrdinalIgnoreCase),
            DisplayText = $"{row.HuCode}, {FormatQty(row.Qty)} шт, {statusLabel}/{printedLabel}"
        };
    }

    private static bool IsReservedHuRow(ProductionPalletPrintRow row)
    {
        return string.Equals(row.SourceType, ProductionPalletPrintSourceType.ReservedHu, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}

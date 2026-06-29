namespace FlowStock.App;

/// <summary>
/// Применяет необязательные параметры запуска печати (дата изготовления и номер партии)
/// ко всем выбранным строкам, создавая новые экземпляры <see cref="PalletLabelPrintRow"/>
/// и не мутируя исходные строки, пришедшие от API.
/// </summary>
public static class PalletLabelPrintRowFactory
{
    public static IReadOnlyList<PalletLabelPrintRow> ApplyPrintParameters(
        IReadOnlyList<PalletLabelPrintRow> rows,
        DateTime? productionDate,
        string? batchNumber)
    {
        var normalizedBatch = string.IsNullOrWhiteSpace(batchNumber)
            ? string.Empty
            : batchNumber.Trim();

        return rows
            .Select(row => new PalletLabelPrintRow
            {
                PalletId = row.PalletId,
                OrderId = row.OrderId,
                OrderRef = row.OrderRef,
                ClientName = row.ClientName,
                PrdRef = row.PrdRef,
                HuCode = row.HuCode,
                ItemName = row.ItemName,
                Brand = row.Brand,
                Qty = row.Qty,
                Uom = row.Uom,
                PalletNo = row.PalletNo,
                PalletCount = row.PalletCount,
                StoragePlace = row.StoragePlace,
                Comment = row.Comment,
                IsMixedPallet = row.IsMixedPallet,
                Composition = row.Composition,
                Line1ItemName = row.Line1ItemName,
                Line1Qty = row.Line1Qty,
                Line2ItemName = row.Line2ItemName,
                Line2Qty = row.Line2Qty,
                Line3ItemName = row.Line3ItemName,
                Line3Qty = row.Line3Qty,
                Status = row.Status,
                SourceType = row.SourceType,
                // Переопределяем только параметры текущего запуска печати:
                ProductionDate = productionDate,   // null явно очищает дату PRD, пришедшую от API
                BatchNumber = normalizedBatch
            })
            .ToArray();
    }
}

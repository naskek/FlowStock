using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services.Warehouse;

public sealed class MoveHuWarehouseActionValidator : IWarehouseActionValidator
{
    public string ActionType => WarehouseActionType.MoveHu;

    public void ValidateForBundle(
        IDataStore store,
        long? bundleId,
        WarehouseActionLine line,
        ICollection<WarehouseBundleIssue> errors,
        ICollection<WarehouseBundleIssue> warnings)
    {
        var payload = WarehousePayloadParser.ParseMoveHu(line);
        var lineNo = line.LineNo;

        if (string.IsNullOrWhiteSpace(payload.HuCode))
        {
            errors.Add(Issue("MISSING_HU_CODE", "Не указан HU.", lineNo));
            return;
        }

        var huCode = payload.HuCode.Trim();
        if (!payload.ItemId.HasValue || payload.ItemId.Value <= 0)
        {
            errors.Add(Issue("MISSING_ITEM_ID", "Не указан товар.", lineNo));
        }

        if (!payload.ToLocationId.HasValue || store.FindLocationById(payload.ToLocationId.Value) == null)
        {
            errors.Add(Issue("INVALID_TO_LOCATION", "Место назначения не найдено.", lineNo));
        }

        if (payload.FromLocationId.HasValue && store.FindLocationById(payload.FromLocationId.Value) == null)
        {
            errors.Add(Issue("INVALID_FROM_LOCATION", "Место отправления не найдено.", lineNo));
        }

        var stockRows = store.GetHuStockRows()
            .Where(row => string.Equals(row.HuCode, huCode, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (stockRows.Length == 0)
        {
            errors.Add(Issue("HU_NOT_FOUND", $"HU {huCode} не найден на складе.", lineNo));
        }
        else
        {
            var qty = payload.Qty ?? stockRows.Sum(row => row.Qty);
            if (qty <= 0)
            {
                errors.Add(Issue("HU_EMPTY", $"HU {huCode} без остатка.", lineNo));
            }

            if (payload.ItemId.HasValue && stockRows.All(row => row.ItemId != payload.ItemId.Value))
            {
                errors.Add(Issue("HU_ITEM_MISMATCH", "Товар не соответствует остатку HU.", lineNo));
            }

            if (payload.FromLocationId.HasValue
                && stockRows.All(row => row.LocationId != payload.FromLocationId.Value))
            {
                warnings.Add(Issue("FROM_LOCATION_MISMATCH", "Место отправления не совпадает с текущим остатком HU.", lineNo));
            }
        }

        if (store.IsHuLockedByActiveWarehouseTask(huCode, bundleId))
        {
            errors.Add(Issue("HU_LOCKED", $"HU {huCode} уже в активном пакете заданий.", lineNo));
        }

        if (bundleId.HasValue)
        {
            var duplicate = store.GetWarehouseActionLines(bundleId.Value)
                .Any(other => other.LineNo != line.LineNo
                              && string.Equals(other.HuCode, huCode, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(other.ActionType, WarehouseActionType.MoveHu, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                errors.Add(Issue("DUPLICATE_HU_IN_BUNDLE", "HU уже есть в этом пакете.", lineNo));
            }
        }
    }

    private static WarehouseBundleIssue Issue(string code, string message, int? lineNo) =>
        new() { Code = code, Message = message, LineNo = lineNo };
}

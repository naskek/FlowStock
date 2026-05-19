using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services.Warehouse;

public interface IWarehouseActionValidator
{
    string ActionType { get; }

    void ValidateForBundle(
        IDataStore store,
        long? bundleId,
        WarehouseActionLine line,
        ICollection<WarehouseBundleIssue> errors,
        ICollection<WarehouseBundleIssue> warnings);
}

public static class WarehouseActionValidatorRegistry
{
    private static readonly IReadOnlyDictionary<string, IWarehouseActionValidator> Validators =
        new IWarehouseActionValidator[]
        {
            new MoveHuWarehouseActionValidator(),
            new AdoptPalletPlanWarehouseActionValidator()
        }.ToDictionary(validator => validator.ActionType, StringComparer.OrdinalIgnoreCase);

    public static IWarehouseActionValidator? TryGet(string actionType)
    {
        return Validators.TryGetValue(actionType.Trim(), out var validator) ? validator : null;
    }
}

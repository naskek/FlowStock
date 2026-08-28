using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Abstractions;

/// <summary>
/// Fail-closed boundary for business paths that require the aggregate marking model.
/// Preflight/enforcement do not use this boundary.
/// </summary>
public interface IMarkingCutoverRuntimeGuard
{
    void RequireEnforcedMarkingWorkflow(string operation);
    void RequireEnforcedMarkingWorkflowForPallet(long productionPalletId, string operation);
    IReadOnlyDictionary<long, MarkingPalletEligibility> GetMarkingPalletEligibility(
        IReadOnlyCollection<long> productionPalletIds,
        string operation) => new Dictionary<long, MarkingPalletEligibility>();
}

public static class MarkingCutoverRuntimeErrors
{
    public const string MaintenanceRequired = "MARKING_CUTOVER_MAINTENANCE_REQUIRED";
}

namespace FlowStock.Core.Models;

public sealed class ProductionPlanConsistencyRepairResult
{
    public bool Ok { get; init; }
    public bool Applied { get; init; }
    public string Mode { get; init; } = string.Empty;
    public IReadOnlyList<string> ValidationErrors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ProductionPlanConsistencyRepairStep> Steps { get; init; } = Array.Empty<ProductionPlanConsistencyRepairStep>();
    public IReadOnlyList<ProductionPlanConsistencyDiagnosticItem> DiagnosticsAfter { get; init; } = Array.Empty<ProductionPlanConsistencyDiagnosticItem>();
}

public sealed class ProductionPlanConsistencyRepairStep
{
    public string Action { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public bool Skipped { get; init; }
}

using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

/// <summary>
/// Временная диагностика синхронизации плана паллет при изменении qty (Information-уровень через Trace).
/// </summary>
public static class ProductionPalletPlanSyncDiagnostics
{
    public static void Log(ProductionPalletPlanSyncReport report)
    {
        System.Diagnostics.Trace.WriteLine(report.ToLogLine());
    }
}

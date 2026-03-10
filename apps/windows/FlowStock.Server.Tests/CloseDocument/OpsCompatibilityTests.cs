namespace FlowStock.Server.Tests.CloseDocument;

public sealed class OpsCompatibilityTests
{
    [Fact(Skip = "OPS compatibility tests require executable HTTP coverage for POST /api/ops.")]
    public void ApiOps_ConvergesToCanonicalCloseSemantics()
    {
    }

    [Fact(Skip = "OPS compatibility tests require executable HTTP coverage for POST /api/ops.")]
    public void ApiOps_Replay_DoesNotDuplicateLedger()
    {
    }
}

namespace FlowStock.Server.Tests.CloseDocument;

public sealed class TsdCompatibilityTests
{
    [Fact(Skip = "TSD compatibility needs executable HTTP tests once /api/docs/{docUid}/close is wired into the automated test host.")]
    public void CreateLinesClose_ResultsInClosed()
    {
    }

    [Fact(Skip = "TSD compatibility needs executable HTTP tests once /api/docs/{docUid}/close is wired into the automated test host.")]
    public void CreateLinesWithoutClose_RemainsDraft()
    {
    }

    [Fact(Skip = "TSD compatibility needs executable HTTP tests once /api/docs/{docUid}/close is wired into the automated test host.")]
    public void RepeatedClose_IsIdempotent()
    {
    }
}

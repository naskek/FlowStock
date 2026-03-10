namespace FlowStock.Server.Tests.CloseDocument;

public sealed class ApiMetadataTests
{
    [Fact(Skip = "HTTP close endpoint automation is pending a reusable server test host and api_docs/api_events fixture.")]
    public void HttpClose_UpdatesApiDocsStatusToClosed()
    {
    }

    [Fact(Skip = "HTTP close endpoint automation is pending a reusable server test host and api_docs/api_events fixture.")]
    public void HttpClose_RecordsDocCloseEvent()
    {
    }

    [Fact(Skip = "Metadata reconciliation behavior needs executable HTTP coverage after wrapper migration starts.")]
    public void Replay_ReconcilesMetadataWithoutRepostingLedger()
    {
    }
}

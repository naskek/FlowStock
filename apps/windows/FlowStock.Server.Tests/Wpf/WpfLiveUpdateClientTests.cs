using FlowStock.App.Services;

namespace FlowStock.Server.Tests.Wpf;

public sealed class WpfLiveUpdateClientTests
{
    [Fact]
    public async Task ReadEventsAsync_EmitsOnlyChangedEvents()
    {
        const string sse = """
                           event: connected
                           data: {}

                           : ping

                           event: changed
                           data: {"path":"/api/orders/1"}

                           event: changed
                           data: {"path":"/api/docs/2"}

                           """;
        var changedCount = 0;

        await WpfLiveUpdateClient.ReadEventsAsync(
            new StringReader(sse),
            () => changedCount++,
            CancellationToken.None);

        Assert.Equal(2, changedCount);
    }

    [Fact]
    public async Task ReadEventsAsync_StopsAtEndOfDisconnectedStream()
    {
        var changedCount = 0;

        await WpfLiveUpdateClient.ReadEventsAsync(
            new StringReader("event: changed\ndata: {}\n\n"),
            () => changedCount++,
            CancellationToken.None);

        Assert.Equal(1, changedCount);
    }
}

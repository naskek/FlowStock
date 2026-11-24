using FlowStock.Core.Services;
namespace FlowStock.Server.Tests.UpdateOrder;
public sealed class OperationTimingsTests
{
    [Fact]
    public async Task StagesFlowAcrossAwaitAndRestoreAfterFailure_WithoutLeakingBetweenRequests()
    {
        using var outer = new OperationTimings();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async i =>
        {
            using var timings = new OperationTimings();
            try { using var stage = OperationTimings.Measure($"lock_{i}"); await Task.Yield(); throw new InvalidOperationException(); }
            catch (InvalidOperationException) { }
            Assert.Single(timings.Stages);
            Assert.True(timings.Stages[$"lock_{i}"] >= 0);
            return timings;
        }));
        Assert.Empty(outer.Stages);
        using (OperationTimings.Measure("restored")) { }
        Assert.Single(outer.Stages);
        Assert.All(results, t => Assert.Single(t.Stages));
    }
}

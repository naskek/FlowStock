using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Marking;

public sealed class SimpleMarkingExcelServiceTests
{
    [Fact]
    public void LegacyQueueAndExport_CannotAccessDataOrGenerateSyntheticCodes()
    {
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        var service = new MarkingExcelService(store.Object);
        Assert.Empty(service.GetOrderQueue(true));
        Assert.False(service.Export(new long[] { 1 }, DateTime.UtcNow).IsSuccess);
        Assert.False(service.Export(new[] { Guid.NewGuid() }, Array.Empty<long>(), DateTime.UtcNow).IsSuccess);
        Assert.Throws<InvalidOperationException>(() => new MarkingNeedCreationService(store.Object).CreateFromProductionNeeds(DateTime.UtcNow));
        Assert.Empty(store.Invocations);
    }

    [Theory]
    [InlineData(MarkingStatus.NotRequired)]
    [InlineData(MarkingStatus.Required)]
    [InlineData(MarkingStatus.Printed)]
    public void HistoricalMarkingState_HasNoOperationalPresentation(MarkingStatus status)
    {
        var order = new Order { MarkingStatus = status, MarkingRequired = true,
            MarkingApplies = true, MarkingCodeCovered = true, MarkingPrintedAt = DateTime.UtcNow };
        Assert.False(order.MarkingCompleted);
        Assert.Empty(order.MarkingStatusDisplay);
        Assert.Empty(order.MarkingLabel);
    }
}

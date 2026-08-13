using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderItemActivityGuardTests
{
    [Fact]
    public void NoOpLock_DoesNotDisableInactiveValidation()
    {
        var store = new Mock<IDataStore>();
        store.Setup(candidate => candidate.FindItemById(10))
            .Returns(new Item { Id = 10, Name = "Архивный товар", IsActive = false });

        var error = Assert.Throws<OrderItemActivityException>(() =>
            OrderItemActivityGuard.EnsureActiveForAdditionalOrderQuantity(store.Object, [10]));

        Assert.Equal(OrderItemActivityGuard.ItemInactiveForOrder, error.ErrorCode);
        store.Verify(candidate => candidate.LockItemsForOrderValidation(
            It.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new long[] { 10 }))), Times.Once);
        store.Verify(candidate => candidate.FindItemById(10), Times.Once);
    }

    [Fact]
    public void MissingItem_UsesStableExistingError()
    {
        var store = new Mock<IDataStore>();
        store.Setup(candidate => candidate.FindItemById(11)).Returns((Item?)null);

        var error = Assert.Throws<OrderItemActivityException>(() =>
            OrderItemActivityGuard.EnsureActiveForAdditionalOrderQuantity(store.Object, [11]));

        Assert.Equal("ITEM_NOT_FOUND", error.ErrorCode);
    }

    [Fact]
    public void ActiveItems_AreDeduplicatedAndValidatedAfterSortedLock()
    {
        var store = new Mock<IDataStore>();
        store.Setup(candidate => candidate.FindItemById(It.IsAny<long>()))
            .Returns<long>(id => new Item { Id = id, Name = $"Товар {id}", IsActive = true });

        OrderItemActivityGuard.EnsureActiveForAdditionalOrderQuantity(store.Object, [20, 10, 20]);

        store.Verify(candidate => candidate.LockItemsForOrderValidation(
            It.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new long[] { 10, 20 }))), Times.Once);
        store.Verify(candidate => candidate.FindItemById(10), Times.Once);
        store.Verify(candidate => candidate.FindItemById(20), Times.Once);
    }
}

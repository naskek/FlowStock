using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;
using FlowStock.Core.Services;
using Moq;

namespace FlowStock.Server.Tests.Orders;

public sealed class OrderCustomerSaveFollowUpBuilderTests
{
    [Fact]
    public void Build_IncludesReservationLinesAndIgnoredReasonCodes()
    {
        const long orderId = 65;
        var store = new Mock<IDataStore>(MockBehavior.Strict);
        store.Setup(s => s.GetOrder(orderId))
            .Returns(new Order
            {
                Id = orderId,
                OrderRef = "CUST-65",
                Type = OrderType.Customer,
                PartnerId = 1,
                Status = OrderStatus.InProgress,
                UseReservedStock = true,
                CreatedAt = DateTime.Now
            });
        store.Setup(s => s.GetOrderReceiptPlanLines(orderId))
            .Returns([
                new OrderReceiptPlanLine
                {
                    Id = 1,
                    OrderId = orderId,
                    OrderLineId = 501,
                    ItemId = 6,
                    QtyPlanned = 30,
                    ToHu = "HU-0000100",
                    SortOrder = 0
                }
            ]);

        var applyResult = new OrderAutoRedistributionApplyResult { TargetOrderId = orderId };
        applyResult.Transfers.Add(new OrderAutoRedistributionTransfer
        {
            SourceOrderId = 64,
            SourceOrderRef = "INT-64",
            TargetOrderId = orderId,
            ItemId = 6,
            QtyTransferred = 10,
            QtyFromUnproduced = 10,
            TransferredHuCodes = ["HU-0000460"]
        });
        applyResult.IgnoredAttempts.Add(new OrderAutoRedistributionIgnoredAttempt
        {
            SourceOrderId = 63,
            SourceOrderRef = "INT-63",
            ItemId = 6,
            Qty = 5,
            Reason = "Недостаточно выпущенного товара на складе по внутреннему заказу для переноса с привязкой HU."
        });

        var envelope = OrderCustomerSaveFollowUpBuilder.Build(store.Object, applyResult);

        Assert.True(envelope.Success);
        Assert.Equal("PARTIALLY_REDISTRIBUTED", envelope.Result);
        Assert.True(envelope.BindReservedStock);
        Assert.Single(envelope.ReservationLines);
        Assert.Equal("HU-0000100", envelope.ReservationLines[0].HuCode);
        Assert.Single(envelope.Transfers);
        Assert.Single(envelope.IgnoredAttempts);
        Assert.Equal("INSUFFICIENT_PRODUCED_STOCK", envelope.IgnoredAttempts[0].ReasonCode);
        Assert.Contains(envelope.Warnings, warning => warning.Code == "AUTO_TRANSFER_PARTIAL");
    }

    [Fact]
    public void MapFromExceptionMessage_ReturnsStableCodes()
    {
        Assert.Equal(
            "INSUFFICIENT_PRODUCED_STOCK",
            OrderAutoRedistributionReasonCodes.MapFromExceptionMessage("Недостаточно выпущенного товара на складе"));
        Assert.Equal(
            "NOTHING_TO_TRANSFER",
            OrderAutoRedistributionReasonCodes.MapFromExceptionMessage("Нет доступного объема для переноса"));
    }
}

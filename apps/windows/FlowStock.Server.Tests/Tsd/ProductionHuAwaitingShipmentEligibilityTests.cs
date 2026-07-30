using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.Server.Tests.Tsd;

public sealed class ProductionHuAwaitingShipmentEligibilityTests
{
    [Theory]
    [InlineData("IN_PROGRESS", true)]
    [InlineData("ACCEPTED", true)]
    [InlineData("DRAFT", false)]
    [InlineData("SHIPPED", false)]
    [InlineData("CANCELLED", false)]
    [InlineData("MERGED", false)]
    public void IsEligible_UsesOnlyOutboundLifecycleOrderStatuses(string orderStatus, bool expected)
    {
        var facts = EligibleFacts(orderStatus: orderStatus);

        Assert.Equal(expected, ProductionHuAwaitingShipmentEligibility.IsEligible(facts));
    }

    [Fact]
    public void IsEligible_FailsClosedForNonCustomerNonFilledAndOwnershipProblems()
    {
        Assert.False(ProductionHuAwaitingShipmentEligibility.IsEligible(
            EligibleFacts(orderType: "INTERNAL")));
        Assert.False(ProductionHuAwaitingShipmentEligibility.IsEligible(
            EligibleFacts(persistedStatus: ProductionPalletStatus.Printed)));
        Assert.False(ProductionHuAwaitingShipmentEligibility.IsEligible(
            EligibleFacts(ownerOrderId: null)));
        Assert.False(ProductionHuAwaitingShipmentEligibility.IsEligible(
            EligibleFacts(evaluatedOrderId: 218)));
        Assert.False(ProductionHuAwaitingShipmentEligibility.IsEligible(
            EligibleFacts(componentOrderLineId: null)));
        Assert.False(ProductionHuAwaitingShipmentEligibility.IsEligible(
            EligibleFacts(componentOrderLineOrderId: 218)));
    }

    [Fact]
    public void IsEligible_RequiresCompletedComponentsAndPositiveLedgerForEveryUniqueKey()
    {
        Assert.False(ProductionHuAwaitingShipmentEligibility.IsEligible(
            EligibleFacts(filledQty: 599)));
        Assert.False(ProductionHuAwaitingShipmentEligibility.IsEligible(
            EligibleFacts(ledgerBalance: 0)));
        Assert.False(ProductionHuAwaitingShipmentEligibility.IsEligible(
            EligibleFacts(includeKeyFact: false)));
    }

    [Fact]
    public void IsEligible_DeduplicatesRepeatedComponentKeyWithoutDuplicatingLedgerBalance()
    {
        var facts = EligibleFacts();
        facts = new ProductionHuAwaitingShipmentEligibilityFacts
        {
            PalletId = facts.PalletId,
            PersistedPalletStatus = facts.PersistedPalletStatus,
            OwnerOrderId = facts.OwnerOrderId,
            OwnerOrderRef = facts.OwnerOrderRef,
            OwnerOrderType = facts.OwnerOrderType,
            OwnerOrderStatus = facts.OwnerOrderStatus,
            EvaluatedOrderId = facts.EvaluatedOrderId,
            Components =
            [
                facts.Components[0],
                new ProductionHuAwaitingShipmentComponentFact
                {
                    OrderLineId = 52,
                    OrderLineOrderId = 217,
                    ItemId = 10,
                    HuCode = " hu-0001303 ",
                    PlannedQty = 600,
                    FilledQty = 600
                }
            ],
            ComponentKeys = facts.ComponentKeys
        };

        Assert.True(ProductionHuAwaitingShipmentEligibility.IsEligible(facts));
    }

    [Fact]
    public void IsEligible_ActiveReservationOrShipmentBlocksWholePallet()
    {
        Assert.False(ProductionHuAwaitingShipmentEligibility.IsEligible(
            EligibleFacts(hasActiveReservation: true)));
        Assert.False(ProductionHuAwaitingShipmentEligibility.IsEligible(
            EligibleFacts(hasActiveShipment: true)));
    }

    [Fact]
    public void IsEligible_SupersededShipmentDoesNotBlockWhenNoActiveShipmentRemains()
    {
        var facts = EligibleFacts(hasActiveShipment: false);

        Assert.True(ProductionHuAwaitingShipmentEligibility.IsEligible(facts));
    }

    private static ProductionHuAwaitingShipmentEligibilityFacts EligibleFacts(
        string persistedStatus = ProductionPalletStatus.Filled,
        string orderType = "CUSTOMER",
        string orderStatus = "IN_PROGRESS",
        long? ownerOrderId = 217,
        long? evaluatedOrderId = 217,
        long? componentOrderLineId = 51,
        long? componentOrderLineOrderId = 217,
        double filledQty = 600,
        double ledgerBalance = 600,
        bool includeKeyFact = true,
        bool hasActiveReservation = false,
        bool hasActiveShipment = false)
    {
        return new ProductionHuAwaitingShipmentEligibilityFacts
        {
            PalletId = 30,
            PersistedPalletStatus = persistedStatus,
            OwnerOrderId = ownerOrderId,
            OwnerOrderRef = "217",
            OwnerOrderType = orderType,
            OwnerOrderStatus = orderStatus,
            EvaluatedOrderId = evaluatedOrderId,
            Components =
            [
                new ProductionHuAwaitingShipmentComponentFact
                {
                    OrderLineId = componentOrderLineId,
                    OrderLineOrderId = componentOrderLineOrderId,
                    ItemId = 10,
                    HuCode = "HU-0001303",
                    PlannedQty = 600,
                    FilledQty = filledQty
                }
            ],
            ComponentKeys = includeKeyFact
                ?
                [
                    new ProductionHuAwaitingShipmentComponentKeyFact
                    {
                        ItemId = 10,
                        HuCode = "HU-0001303",
                        LedgerBalance = ledgerBalance,
                        HasActiveReservation = hasActiveReservation,
                        HasActiveShipment = hasActiveShipment
                    }
                ]
                : Array.Empty<ProductionHuAwaitingShipmentComponentKeyFact>()
        };
    }
}

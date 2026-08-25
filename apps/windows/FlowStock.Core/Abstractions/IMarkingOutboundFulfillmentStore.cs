using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Abstractions;

/// <summary>
/// Authoritative seam for marking eligibility of whole-HU OUTBOUND close.
/// Both methods must run in the same document transaction.
/// </summary>
public interface IMarkingOutboundFulfillmentStore
{
    IReadOnlyList<MarkingOutboundFulfillmentDecision> DecideOutboundMarkingEligibility(
        long outboundDocId,
        string actor,
        DateTime decidedAt);

    void RecordOutboundMarkingAttribution(
        IReadOnlyCollection<MarkingOutboundFulfillmentDecision> decisions);
}

using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Abstractions;

public interface IMarkingCutoverApprovalStore
{
    MarkingCutoverLineApprovalResult ApproveMarkingCutoverLine(
        long orderLineId,
        int? allowedQuantity,
        string expectedPreflightHash,
        string approvedBy,
        DateTime approvedAt);

    MarkingCutoverSubjectApprovalResult ApproveMarkingCutoverSubjects(
        long allowlistId,
        IReadOnlyList<MarkingCutoverSubjectApprovalIntent> subjects,
        string expectedPreflightHash,
        string approvedBy,
        DateTime approvedAt);
}

namespace FlowStock.Core.Models.Marking;

public sealed record MarkingCutoverLineApprovalResult(
    long AllowlistId,
    long OrderLineId,
    int AllowedQuantity,
    string OriginalPreflightHash,
    string CurrentPreflightHash,
    bool WasAlreadyApproved);

public sealed record MarkingCutoverSubjectApprovalIntent(Guid SubjectId, decimal ApprovedQuantity);

public sealed record MarkingCutoverSubjectApprovalResult(
    long AllowlistId,
    string CurrentPreflightHash,
    IReadOnlyList<Guid> ApprovalIds,
    bool WasAlreadyApproved);

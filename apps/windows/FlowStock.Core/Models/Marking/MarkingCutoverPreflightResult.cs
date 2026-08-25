namespace FlowStock.Core.Models.Marking;

public sealed record MarkingCutoverPreflightResult(
    DateTime GeneratedAt,
    string Hash,
    string CanonicalJson,
    IReadOnlyList<MarkingCutoverPreflightEntry> Entries,
    IReadOnlyList<MarkingLegacyCutoverLineSnapshot>? LegacyLineSnapshots = null,
    IReadOnlyList<MarkingLegacyCutoverSubjectSnapshot>? LegacySubjectSnapshots = null);

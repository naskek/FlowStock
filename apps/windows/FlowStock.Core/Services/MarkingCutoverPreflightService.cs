using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Services;

public sealed class MarkingCutoverPreflightService
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IMarkingCutoverPreflightStore _store;

    public MarkingCutoverPreflightService(IMarkingCutoverPreflightStore store)
    {
        _store = store;
    }

    public MarkingCutoverPreflightResult Run(DateTime generatedAt)
    {
        // Canonical sort must cover the full content of every entry so that the same set of
        // issues hashes identically regardless of the order the store returns them in. Nullable
        // values get a fixed "last" position so ordering stays deterministic.
        var entries = _store.GetMarkingCutoverPreflightEntries()
            .OrderBy(entry => entry.OrderId ?? long.MaxValue)
            .ThenBy(entry => entry.OrderLineId ?? long.MaxValue)
            .ThenBy(entry => entry.IssueCode, StringComparer.Ordinal)
            .ThenBy(entry => entry.Level, StringComparer.Ordinal)
            .ThenBy(entry => entry.TargetQty ?? double.MaxValue)
            .ThenBy(entry => entry.RealCodeQty ?? int.MaxValue)
            .ThenBy(entry => entry.LegacySyntheticQty ?? int.MaxValue)
            .ThenBy(entry => entry.Details, StringComparer.Ordinal)
            .ThenBy(entry => entry.SuggestedRemediation, StringComparer.Ordinal)
            .ToArray();

        var canonicalJson = JsonSerializer.Serialize(entries, CanonicalJsonOptions);
        return new MarkingCutoverPreflightResult(
            generatedAt,
            ComputeSha256(canonicalJson),
            canonicalJson,
            entries);
    }

    private static string ComputeSha256(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

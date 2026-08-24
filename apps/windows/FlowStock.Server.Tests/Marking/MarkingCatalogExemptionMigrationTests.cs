namespace FlowStock.Server.Tests.Marking;

public sealed class MarkingCatalogExemptionMigrationTests
{
    [Fact]
    public void V0038DefinesCanonicalApplicabilityAndSingleUseGrandfatherApprovals()
    {
        var sql = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "deploy", "postgres", "migrations",
            "V0038__marking_catalog_exemption_and_approval_guards.sql"));

        Assert.Contains("chz_marking_exempt BOOLEAN NOT NULL DEFAULT FALSE", sql);
        Assert.Contains("validate_item_marking_applicability_transition", sql);
        Assert.Contains("validate_item_type_marking_applicability_transition", sql);
        Assert.Contains("MARKING_GTIN_REQUIRED", sql);
        Assert.Contains("MARKING_APPLICABILITY_LINEAGE_EXISTS", sql);
        Assert.Contains("ux_marking_legacy_allowlist_one_parent_per_line", sql);
        Assert.Contains("ux_marking_allowlist_subject_once", sql);
        Assert.Contains("refresh_marking_status_for_items", sql);
        Assert.DoesNotContain("UPDATE marking_code", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ledger", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "AGENTS.md")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

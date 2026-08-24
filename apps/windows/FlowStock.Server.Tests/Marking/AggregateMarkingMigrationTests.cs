namespace FlowStock.Server.Tests.Marking;

public sealed class AggregateMarkingMigrationTests
{
    [Fact]
    public void V0036DefinesStableSubjectsImmutableScopesAggregateCoverageAndReadyFacts()
    {
        var sql = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "deploy", "postgres", "migrations",
            "V0036__aggregate_marking_subjects_and_ready_hu.sql"));

        Assert.Contains("marking_production_subject", sql);
        Assert.Contains("marking_request_scope", sql);
        Assert.Contains("marking_operational_coverage", sql);
        Assert.Contains("marking_ready_hu_fact", sql);
        Assert.Contains("marking_ready_hu_fact_lineage", sql);
        Assert.Contains("marking_ready_hu_grandfather_lineage", sql);
        Assert.Contains("WHEN pp.status = 'CORRECTED' THEN 'SUPERSEDED'", sql);
        Assert.Contains("marking_request_scope_consumption", sql);
        Assert.Contains("ux_marking_coverage_active_grandfather_allowance", sql);
        Assert.Contains("validate_active_grandfather_coverage", sql);
        Assert.Contains("marking_synthetic_legacy_allowlist_subject", sql);
        Assert.Contains("marking_grandfather_operational_allowance", sql);
        Assert.Contains("marking_import_batch_request", sql);
        Assert.Contains("default_reserve_quantity INTEGER NOT NULL DEFAULT 5", sql);
        Assert.Contains("prevent_real_marking_code_provenance_mutation", sql);
        Assert.Contains("MARKING_SUBJECT_GTIN_IMMUTABLE", sql);
        Assert.Contains("trg_items_marking_subject_gtin_guard", sql);
        Assert.DoesNotContain("TEMP-CHZ-", sql);
    }

    [Fact]
    public void RuntimeExposesHashCheckedAtomicEnforcementAndNoLegacyMutationEndpoints()
    {
        var repo = FindRepoRoot();
        var endpoint = File.ReadAllText(Path.Combine(
            repo, "apps", "windows", "FlowStock.Server", "MarkingCutoverEndpoints.cs"));
        var program = File.ReadAllText(Path.Combine(
            repo, "apps", "windows", "FlowStock.Server", "Program.cs"));
        var dataStore = File.ReadAllText(Path.Combine(
            repo, "apps", "windows", "FlowStock.Data", "PostgresDataStore.cs"));
        var orderMarkingEndpoint = File.ReadAllText(Path.Combine(
            repo, "apps", "windows", "FlowStock.Server", "OrderMarkingExportEndpoint.cs"));

        Assert.Contains("/api/admin/marking/cutover/enforce", endpoint);
        Assert.Contains("MARKING_CUTOVER_PREFLIGHT_HASH_MISMATCH", dataStore);
        Assert.Contains("ApplyMarkingEnforcedCutover", dataStore);
        Assert.Contains("/api/orders/{orderId:long}/marking/import/preview", orderMarkingEndpoint);
        Assert.Contains("/api/orders/{orderId:long}/marking/import/confirm", orderMarkingEndpoint);
        Assert.DoesNotContain("/api/marking/export", program);
        Assert.DoesNotContain("create-from-production-needs", program);
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

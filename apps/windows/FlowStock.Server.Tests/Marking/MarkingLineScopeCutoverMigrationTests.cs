using FlowStock.Core.Models.Marking;

namespace FlowStock.Server.Tests.Marking;

public sealed class MarkingLineScopeCutoverMigrationTests
{
    [Fact]
    public void MigrationAddsShadowCutoverAndLineScopedMarkingSchema()
    {
        var migration = ReadMigration();

        Assert.Contains("marking_responsibility TEXT NOT NULL DEFAULT 'FLOWSTOCK'", migration);
        Assert.Contains("CHECK (marking_responsibility IN ('FLOWSTOCK', 'CUSTOMER'))", migration);
        Assert.Contains("marking_responsibility_audit", migration);
        Assert.Contains("order_line_id BIGINT NULL REFERENCES order_lines(id) ON DELETE RESTRICT", migration);
        Assert.Contains("request_status TEXT NOT NULL DEFAULT 'NotRequested'", migration);
        Assert.Contains("marking_cutover_state", migration);
        Assert.Contains("VALUES(TRUE, 'SHADOW'", migration);
        Assert.Contains("state IN ('SHADOW', 'PREFLIGHT_READY', 'ENFORCED')", migration);
    }

    [Fact]
    public void ServerExposesReadOnlyCutoverPreflightEndpoint()
    {
        var root = FindRepoRoot();
        var endpoint = File.ReadAllText(Path.Combine(root, "apps", "windows", "FlowStock.Server", "MarkingCutoverEndpoints.cs"));
        var program = File.ReadAllText(Path.Combine(root, "apps", "windows", "FlowStock.Server", "Program.cs"));

        Assert.Contains("/api/admin/marking/cutover/preflight", endpoint);
        Assert.Contains("preflight_hash", endpoint);
        Assert.Contains("canonical_json", endpoint);
        Assert.Contains("MarkingCutoverEndpoints.Map(app)", program);
        Assert.DoesNotContain("PREFLIGHT_READY", endpoint);
        Assert.DoesNotContain("ENFORCED", endpoint);
    }

    [Fact]
    public void MigrationShipsNonUniqueRealLikePreflightIndexWithoutRealImportUniqueness()
    {
        var migration = ReadMigration();

        // PR 1 (SHADOW) ships only the non-unique preflight index for real-like historical hashes.
        Assert.Contains("ix_marking_code_real_code_hash_preflight", migration);
        Assert.Contains("LOWER(BTRIM(code_hash))", migration);
        Assert.Contains("origin IN ('RealImport', 'LegacyRealImport', 'HistoricalUnknown')", migration);

        // The unique partial index for new origin = 'RealImport' belongs to the later CSV confirm
        // PR, not to PR 1. It must be absent from V0027.
        Assert.DoesNotContain("ux_marking_code_real_import_hash", migration);
        Assert.DoesNotContain("WHERE origin = 'RealImport'", migration);
    }

    [Fact]
    public void PreflightSqlReportsEveryUnscopedLegacyTaskState()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");

        Assert.Contains("MARKING_LEGACY_TASK_LINE_UNASSIGNED", source);
        Assert.Contains("MARKING_LEGACY_TASK_LINE_AMBIGUOUS", source);
        Assert.Contains("MARKING_LEGACY_TASK_LINE_NOT_FOUND", source);
        Assert.Contains("MARKING_LEGACY_TASK_LINE_CONFLICT", source);
    }

    [Fact]
    public void PreflightSqlStaysStructuralAndDoesNotUseQtyAsCanonicalTarget()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");

        Assert.Contains("ml.marking_responsibility = 'FLOWSTOCK'", source);
        Assert.Contains("c.origin = 'LegacySynthetic'", source);
        Assert.DoesNotContain("AND c.code LIKE 'TEMP-CHZ-%'", source);
        Assert.Contains("origin IN ('RealImport', 'LegacyRealImport', 'HistoricalUnknown')", source);
        Assert.Contains("LOWER(BTRIM(code_hash))", source);
        Assert.DoesNotContain("MARKING_SURPLUS_REAL_CODES", source);
        Assert.DoesNotContain("MARKING_LINE_QTY_CHANGED_AFTER_PREVIEW", source);
        Assert.DoesNotContain("MARKING_LINE_QTY_CHANGED_AFTER_ALLOWLIST", source);
    }

    [Fact]
    public void PreflightSqlExcludesNonMarkableLinesFromScope()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");

        // markable_lines is filtered, not merely computed: non-markable lines drop out of scope.
        Assert.Contains("COALESCE(it.enable_marking, FALSE) = TRUE", source);
    }

    [Fact]
    public void PreflightSqlTreatsOrderAndSourceOrderAsExplicitLinks()
    {
        var source = ReadRepoFile("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");

        // order_id and source_order_id are two explicit links, never collapsed with COALESCE.
        Assert.DoesNotContain("COALESCE(at.order_id, at.source_order_id)", source);
        Assert.Contains("task_order_link", source);
        Assert.Contains("scope_order_id", source);
        Assert.Contains("MARKING_TASK_ORDER_LINK_CONFLICT", source);
    }

    [Fact]
    public void DomainConstantsMatchMigrationVocabulary()
    {
        Assert.Equal("FLOWSTOCK", MarkingResponsibility.FlowStock);
        Assert.Equal("CUSTOMER", MarkingResponsibility.Customer);
        Assert.Equal("NotRequested", MarkingRequestStatus.NotRequested);
        Assert.Equal("ExcelRequested", MarkingRequestStatus.ExcelRequested);
        Assert.Equal("RealImport", MarkingCodeOrigin.RealImport);
        Assert.Equal("LegacySynthetic", MarkingCodeOrigin.LegacySynthetic);
        Assert.Equal("LegacyRealImport", MarkingCodeOrigin.LegacyRealImport);
        Assert.Equal("HistoricalUnknown", MarkingCodeOrigin.HistoricalUnknown);
        Assert.Equal("SHADOW", MarkingCutoverState.Shadow);
        Assert.Equal("PREFLIGHT_READY", MarkingCutoverState.PreflightReady);
        Assert.Equal("ENFORCED", MarkingCutoverState.Enforced);
        Assert.Contains(MarkingCodeStatus.Quarantined, MarkingCodeStatus.All);
    }

    private static string ReadMigration()
    {
        var root = FindRepoRoot();
        return File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "postgres",
            "migrations",
            "V0027__marking_line_scope_cutover_base.sql"));
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var root = FindRepoRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));
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
